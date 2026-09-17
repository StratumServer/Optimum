using System;
using System.Diagnostics;

namespace Optimum.Render.Vulkan.Core;

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
/// Turns the marker stream of a frame into one <see cref="LatencyFrameReport" />,
/// and applies the self-healing rule from the plan: a phase still open when the
/// next frame starts is closed at the new frame's first marker and logged - once,
/// never every frame, because a dropped marker repeats and would otherwise fill
/// the log.
///
/// Pure logic, no Vulkan: the None backend uses it, the Native and AMD backends
/// will, and it is unit-tested on its own.
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
    /// starts a new frame first, closing whatever the old one left open. The
    /// OutOfBand markers belong to submissions outside the frame loop and are
    /// ignored here.
    /// </summary>
    public void Mark(ulong frameId, LatencyMarker marker, long timestampUs)
    {
        if (marker >= LatencyMarker.OutOfBandRenderSubmitStart) return;

        if (!_frameOpen || frameId != _frameId) BeginFrame(frameId, timestampUs);

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
    /// Starts a frame: closes every phase the previous frame left open (counted,
    /// and logged the first time only) and clears the timestamps.
    /// </summary>
    public void BeginFrame(ulong frameId, long timestampUs)
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
            // Closing means exactly that: the open phases end here, so the frame
            // that follows starts from a clean slate.
            CloseOpenPhases(timestampUs);
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

    private void CloseOpenPhases(long timestampUs)
    {
        if (_simStart != 0 && _simEnd == 0) _simEnd = timestampUs;
        if (_renderSubmitStart != 0 && _renderSubmitEnd == 0) _renderSubmitEnd = timestampUs;
        if (_presentStart != 0 && _presentEnd == 0) _presentEnd = timestampUs;
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
