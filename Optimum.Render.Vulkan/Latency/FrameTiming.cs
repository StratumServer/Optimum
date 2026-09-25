using System;
using System.Diagnostics;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// CPU-observed frame intervals in microseconds.
/// TotalUs ends when vkQueuePresentKHR returns, not when the display scans out.
/// </summary>
internal readonly record struct LatencyFrameReport(
    ulong FrameId,
    ulong PresentId,
    ulong InputUs,
    ulong SimulationUs,
    ulong RenderSubmitUs,
    ulong PresentUs,
    ulong TotalUs)
{
    /// <summary>
    /// Builds a report from CPU timestamps on one monotonic clock in microseconds (see
    /// <see cref="LatencyClock" />). A missing marker is passed as 0 and makes the
    /// intervals that need it 0; intervals never go negative.
    /// </summary>
    public static LatencyFrameReport FromCpuTimestamps(
        ulong frameId,
        ulong presentId,
        long inputSampleUs,
        long simulationStartUs,
        long simulationEndUs,
        long renderSubmitStartUs,
        long renderSubmitEndUs,
        long presentStartUs,
        long presentEndUs)
    {
        long start = inputSampleUs != 0 ? inputSampleUs : simulationStartUs;
        return new LatencyFrameReport(
            frameId,
            presentId,
            Span(inputSampleUs, simulationStartUs),
            Span(simulationStartUs, simulationEndUs),
            Span(renderSubmitStartUs, renderSubmitEndUs),
            Span(presentStartUs, presentEndUs),
            Span(start, presentEndUs));
    }

    /// <summary>0 when either end is missing or the pair is out of order; the difference otherwise.</summary>
    private static ulong Span(long fromUs, long toUs) =>
        fromUs <= 0 || toUs <= 0 || toUs <= fromUs ? 0UL : (ulong)(toUs - fromUs);
}

/// <summary>CPU phase boundaries owned by the input, render and present call sites.</summary>
internal enum LatencyMarker
{
    SimulationStart,
    SimulationEnd,
    RenderSubmitStart,
    RenderSubmitEnd,
    PresentStart,
    PresentEnd,
    InputSample,
}

/// <summary>One monotonic microsecond clock for every latency timestamp.</summary>
internal static class LatencyClock
{
    private static readonly double TicksToUs = 1_000_000.0 / Stopwatch.Frequency;

    /// <summary>Microseconds since an arbitrary origin; never 0, so 0 stays "no timestamp".</summary>
    public static long NowUs()
    {
        long us = (long)(Stopwatch.GetTimestamp() * TicksToUs);
        return us > 0 ? us : 1;
    }
}

/// <summary>
/// Collects the current frame's CPU phases. A new frame discards incomplete
/// timestamps and logs the interruption once, so missing markers cannot leak
/// into another frame or repeatedly fill the client log.
/// </summary>
internal sealed class LatencyPhaseTracker
{
    private readonly Action<string>? _log;

    private ulong _frameId;
    private bool _frameOpen;
    private bool _logged;

    private long _inputSample;
    private long _simStart;
    private long _simEnd;
    private long _renderSubmitStart;
    private long _renderSubmitEnd;
    private long _presentStart;
    private long _presentEnd;

    public LatencyPhaseTracker(Action<string>? log = null) => _log = log;

    /// <summary>How often a phase was still open when the next frame started.</summary>
    public int SelfHealCount { get; private set; }

    /// <summary>Whether the self-heal note has already gone to the log.</summary>
    public bool SelfHealLogged => _logged;

    /// <summary>The frame currently collecting markers; 0 before the first one.</summary>
    public ulong CurrentFrameId => _frameId;

    /// <summary>True while some Start has no matching End in the current frame.</summary>
    public bool HasOpenPhase => _frameOpen &&
        ((_simStart != 0 && _simEnd == 0) ||
         (_renderSubmitStart != 0 && _renderSubmitEnd == 0) ||
         (_presentStart != 0 && _presentEnd == 0));

    /// <summary>
    /// Stamps one marker. A marker of a frame id other than the current one
    /// starts a new frame first, closing whatever the old one left open.
    /// </summary>
    public void Mark(ulong frameId, LatencyMarker marker, long timestampUs)
    {
        if (!_frameOpen || frameId != _frameId) BeginFrame(frameId);

        switch (marker)
        {
            case LatencyMarker.InputSample: _inputSample = timestampUs; break;
            case LatencyMarker.SimulationStart: _simStart = timestampUs; break;
            case LatencyMarker.SimulationEnd: _simEnd = timestampUs; break;
            case LatencyMarker.RenderSubmitStart: _renderSubmitStart = timestampUs; break;
            case LatencyMarker.RenderSubmitEnd: _renderSubmitEnd = timestampUs; break;
            case LatencyMarker.PresentStart: _presentStart = timestampUs; break;
            case LatencyMarker.PresentEnd: _presentEnd = timestampUs; break;
        }
    }

    /// <summary>
    /// Starts a frame: discards any phases the previous frame left open (counted,
    /// and logged the first time only) and clears the timestamps.
    /// </summary>
    private void BeginFrame(ulong frameId)
    {
        if (_frameOpen && HasOpenPhase)
        {
            SelfHealCount++;
            if (!_logged)
            {
                _logged = true;
                _log?.Invoke("latency: a phase of frame " + _frameId +
                    " was still open when frame " + frameId +
                    " started; closing it. Reported once, however often it happens.");
            }
        }

        _frameId = frameId;
        _frameOpen = true;
        _inputSample = 0;
        _simStart = 0;
        _simEnd = 0;
        _renderSubmitStart = 0;
        _renderSubmitEnd = 0;
        _presentStart = 0;
        _presentEnd = 0;
    }

    /// <summary>
    /// Closes the frame and builds its report. False when
    /// <paramref name="frameId" /> is not the frame being collected (a present of
    /// a frame whose markers never arrived), leaving the state untouched.
    /// </summary>
    public bool TryComplete(ulong frameId, ulong presentId, out LatencyFrameReport report)
    {
        if (!_frameOpen || frameId != _frameId)
        {
            report = default;
            return false;
        }

        report = LatencyFrameReport.FromCpuTimestamps(
            frameId, presentId,
            _inputSample, _simStart, _simEnd,
            _renderSubmitStart, _renderSubmitEnd,
            _presentStart, _presentEnd);
        _frameOpen = false;
        return true;
    }
}

/// <summary>
/// Bounded completed-frame reports. Overflow drops the oldest entry in constant
/// time; taking reports returns chronological order and empties the buffer.
/// </summary>
internal sealed class LatencyReportBuffer
{
    /// <summary>A few seconds of frames; a client that never samples must not grow this.</summary>
    public const int DefaultCapacity = 256;

    private readonly LatencyFrameReport[] _reports;
    private readonly object _lock = new();

    /// <summary>Where the next report is written.</summary>
    private int _next;

    /// <summary>How many of the slots hold a report that has not been taken.</summary>
    private int _count;

    public LatencyReportBuffer(int capacity = DefaultCapacity)
    {
        if (capacity < 1) capacity = 1;
        _reports = new LatencyFrameReport[capacity];
    }

    public int Capacity => _reports.Length;

    /// <summary>How many reports are waiting to be taken.</summary>
    public int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>Adds one report, dropping the oldest when the ring is full.</summary>
    public void Add(in LatencyFrameReport report)
    {
        lock (_lock)
        {
            _reports[_next] = report;
            _next = _next + 1 == _reports.Length ? 0 : _next + 1;
            if (_count < _reports.Length) _count++;
        }
    }

    /// <summary>The reports since the last call, oldest first, and clears them.</summary>
    public LatencyFrameReport[] Take()
    {
        lock (_lock)
        {
            if (_count == 0) return Array.Empty<LatencyFrameReport>();

            var taken = new LatencyFrameReport[_count];
            int index = _next - _count;
            if (index < 0) index += _reports.Length;
            for (int i = 0; i < _count; i++)
            {
                taken[i] = _reports[index];
                index = index + 1 == _reports.Length ? 0 : index + 1;
            }

            _count = 0;
            _next = 0;
            return taken;
        }
    }
}

/// <summary>CPU phase timestamps and bounded reports for completed frames.</summary>
internal sealed class FrameTimingRecorder
{
    private readonly LatencyPhaseTracker _tracker;
    private readonly LatencyReportBuffer _reports = new();

    public FrameTimingRecorder(Action<string>? log = null) => _tracker = new LatencyPhaseTracker(log);

    public void Marker(ulong frameId, LatencyMarker marker) =>
        _tracker.Mark(frameId, marker, LatencyClock.NowUs());

    public void OnPresent(ulong frameId, ulong presentId)
    {
        if (_tracker.TryComplete(frameId, presentId, out LatencyFrameReport report)) _reports.Add(report);
    }

    public LatencyFrameReport[] TakeReports() => _reports.Take();
}
