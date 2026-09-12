using System;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The default backend: no sleeping, no vendor calls, no swapchain pNext. It
/// only records the markers as CPU timestamps, so the stats line has the frame
/// breakdown even with latency reduction off, and so "off is off" is checkable -
/// the OpenGL path and this one add exactly the same amount of pacing, none.
///
/// <see cref="OwnsFrameCap" /> is false, so the client's own FPS limiter keeps
/// running (lib seam S3).
/// </summary>
internal sealed class NoneLatencyBackend : ILatencyBackend
{
    private readonly LatencyPhaseTracker _tracker;

    /// <summary>
    /// The finished reports, in a fixed ring: the stats sample may never run, and
    /// a full buffer must not cost the present path anything (see
    /// <see cref="LatencyReportBuffer" />).
    /// </summary>
    private readonly LatencyReportBuffer _reports = new();

    public NoneLatencyBackend(Action<string>? log = null) => _tracker = new LatencyPhaseTracker(log);

    public LatencyBackendKind Kind => LatencyBackendKind.None;

    public LatencySettings Settings { get; private set; } = LatencySettings.Disabled;

    /// <summary>Remembered only so the stats line can print what was asked for; nothing acts on it.</summary>
    public void Apply(in LatencySettings settings) => Settings = settings;

    public bool OwnsFrameCap => false;

    /// <summary>Never sleeps.</summary>
    public ulong Sleep(ulong frameId) => 0;

    public void Marker(ulong frameId, LatencyMarker marker) =>
        _tracker.Mark(frameId, marker, LatencyClock.NowUs());

    /// <summary>Nothing to re-apply.</summary>
    public void OnSwapchainCreated(SwapchainKHR swapchain)
    {
    }

    /// <summary>Nothing was bound to the swapchain, so nothing is dropped with it.</summary>
    public void OnSwapchainRetired()
    {
    }

    /// <summary>Adds nothing to the submit chain.</summary>
    public unsafe void* TagSubmit(ulong frameId, void* pNext) => pNext;

    public void OnPresent(ulong frameId, ulong presentId)
    {
        if (!_tracker.TryComplete(frameId, presentId, out LatencyFrameReport report)) return;
        _reports.Add(report);
    }

    public LatencyFrameReport[] TakeReports() => _reports.Take();

    public void Dispose()
    {
    }
}
