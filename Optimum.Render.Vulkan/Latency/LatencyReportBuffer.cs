using System;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The finished frame reports a CPU-timestamp backend keeps until the stats
/// sample drains them (seam S7), as a fixed ring.
///
/// A ring rather than a <c>List</c> with <c>RemoveAt(0)</c>, which is what the
/// backends used before the review of 2026-09-12: nothing guarantees the stats
/// sample ever runs (<c>OPTIMUM_VULKAN_STATS</c> is normally unset), so the
/// buffer sits full for the whole session and every present shifted the whole
/// array down by one - about 20 KB of memmove per frame, under a lock, in the
/// present path, with the default None backend. The ring drops the oldest entry
/// by moving one index instead.
///
/// Every member takes the lock: the frame thread adds and amends, the stats
/// sample takes.
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

    /// <summary>
    /// Fills in the GPU interval of a report that is still waiting, newest first.
    /// False when that frame's report has already been taken (the Native backend
    /// observes the completion after the fact and never guesses).
    /// </summary>
    public bool AmendGpuUs(ulong frameId, ulong gpuUs)
    {
        lock (_lock)
        {
            for (int i = 1; i <= _count; i++)
            {
                int index = _next - i;
                if (index < 0) index += _reports.Length;
                if (_reports[index].FrameId != frameId) continue;
                _reports[index] = _reports[index] with { GpuUs = gpuUs };
                return true;
            }
            return false;
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
