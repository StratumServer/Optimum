using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// A latency backend that only records. Later stages hand it to a device and
/// assert the marker order of a real frame against it, which is the whole point:
/// the seams can be tested without an NVIDIA or AMD GPU present.
/// </summary>
internal sealed class RecordingLatencyBackend : ILatencyBackend
{
    private readonly List<LatencyFrameReport> _reports = new();

    /// <summary>Every (frameId, marker) in the order it was stamped.</summary>
    public List<(ulong FrameId, LatencyMarker Marker)> Markers { get; } = new();

    /// <summary>The frame ids <see cref="Sleep" /> was called with, in order.</summary>
    public List<ulong> Sleeps { get; } = new();

    /// <summary>The frame/present id pairs <see cref="OnPresent" /> was called with.</summary>
    public List<(ulong FrameId, ulong PresentId)> Presents { get; } = new();

    /// <summary>The swapchains handed to <see cref="OnSwapchainCreated" />.</summary>
    public List<SwapchainKHR> Swapchains { get; } = new();

    /// <summary>Every settings object handed to <see cref="Apply" />, in order.</summary>
    public List<LatencySettings> Applied { get; } = new();

    public int SleepCount => Sleeps.Count;

    public int SwapchainCount => Swapchains.Count;

    public int TagSubmitCount { get; private set; }

    /// <summary>What <see cref="Sleep" /> claims to have waited, in microseconds.</summary>
    public ulong SleepDurationUs { get; set; }

    /// <summary>What <see cref="OwnsFrameCap" /> answers.</summary>
    public bool OwnsFrameCapValue { get; set; }

    public LatencyBackendKind Kind { get; set; } = LatencyBackendKind.Native;

    public LatencySettings Settings { get; private set; } = LatencySettings.Disabled;

    public void Apply(in LatencySettings settings)
    {
        Settings = settings;
        Applied.Add(settings);
    }

    public bool OwnsFrameCap => OwnsFrameCapValue;

    public ulong Sleep(ulong frameId)
    {
        Sleeps.Add(frameId);
        return SleepDurationUs;
    }

    public void Marker(ulong frameId, LatencyMarker marker) => Markers.Add((frameId, marker));

    public void OnSwapchainCreated(SwapchainKHR swapchain) => Swapchains.Add(swapchain);

    public unsafe void* TagSubmit(ulong frameId, void* pNext)
    {
        TagSubmitCount++;
        return pNext;
    }

    public void OnPresent(ulong frameId, ulong presentId)
    {
        Presents.Add((frameId, presentId));
        _reports.Add(new LatencyFrameReport(frameId, presentId, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    public LatencyFrameReport[] TakeReports()
    {
        LatencyFrameReport[] taken = _reports.ToArray();
        _reports.Clear();
        return taken;
    }

    /// <summary>The markers of one frame, in order.</summary>
    public LatencyMarker[] MarkersOf(ulong frameId)
    {
        var markers = new List<LatencyMarker>();
        foreach ((ulong id, LatencyMarker marker) in Markers)
        {
            if (id == frameId) markers.Add(marker);
        }
        return markers.ToArray();
    }

    public void Dispose()
    {
    }
}
