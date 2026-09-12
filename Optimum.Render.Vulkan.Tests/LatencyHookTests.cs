using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.Client.NoObf;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Vulkan-native plan, "Latency seams" S3: the lib calls <c>LatencySleep()</c> immediately
/// before it samples input, and the Vulkan platform turns that into the backend's sleep
/// followed by the two markers this site owns - InputSample then SimulationStart - against
/// one strictly increasing frame id. The OpenGL platform declares neither member, so the
/// vanilla frame is the vanilla frame plus one neutral virtual call.
///
/// Headless: no device and no window, so nothing here touches Vulkan or GL. The backend is
/// the recording fake the L0 stage shipped, which is the point of it.
/// </summary>
public class LatencyHookTests
{
    private static VulkanClientPlatform PlatformWith(RecordingLatencyBackend backend)
    {
        var platform = new VulkanClientPlatform(null!);
        platform.LatencyBackendOverride = backend;
        return platform;
    }

    [Fact]
    public void TheSleepRunsBeforeTheInputAndSimulationMarkers()
    {
        var backend = new RecordingLatencyBackend();
        VulkanClientPlatform platform = PlatformWith(backend);

        platform.LatencySleep();

        Assert.Equal(new ulong[] { 1 }, backend.Sleeps.ToArray());
        Assert.Equal(
            new List<(ulong, LatencyMarker)>
            {
                (1UL, LatencyMarker.InputSample),
                (1UL, LatencyMarker.SimulationStart),
            },
            backend.Markers);
    }

    [Fact]
    public void EveryFrameGetsItsOwnStrictlyIncreasingId()
    {
        var backend = new RecordingLatencyBackend();
        VulkanClientPlatform platform = PlatformWith(backend);

        for (int frame = 0; frame < 8; frame++) platform.LatencySleep();

        Assert.Equal(8, backend.SleepCount);
        for (int i = 0; i < backend.Sleeps.Count; i++)
        {
            Assert.Equal((ulong)(i + 1), backend.Sleeps[i]);
            // The two markers of frame i carry that frame's id and no other.
            Assert.Equal(backend.Sleeps[i], backend.Markers[i * 2].FrameId);
            Assert.Equal(backend.Sleeps[i], backend.Markers[i * 2 + 1].FrameId);
        }
        Assert.Equal(16, backend.Markers.Count);
    }

    [Fact]
    public void TheFrameCapFollowsTheBackend()
    {
        var backend = new RecordingLatencyBackend();
        VulkanClientPlatform platform = PlatformWith(backend);

        Assert.False(backend.OwnsFrameCap);
        Assert.False(platform.LatencyOwnsFrameCap);

        backend.OwnsFrameCapValue = true;
        Assert.True(platform.LatencyOwnsFrameCap);

        backend.OwnsFrameCapValue = false;
        Assert.False(platform.LatencyOwnsFrameCap);
    }

    [Fact]
    public void WithNoBackendAndNoDeviceTheSleepIsANoOp()
    {
        var platform = new VulkanClientPlatform(null!);
        Assert.Null(platform.LatencyBackend);

        platform.LatencySleep();

        Assert.False(platform.LatencyOwnsFrameCap);
    }

    [Fact]
    public void TheOpenGlPlatformKeepsTheNeutralMembers()
    {
        var platform = new ClientPlatformWindows(null!);

        // Neutral: calling them on the base platform does nothing and never throws.
        platform.LatencySleep();
        Assert.False(platform.LatencyOwnsFrameCap);

        Assert.Equal(typeof(ClientPlatformAbstract),
            typeof(ClientPlatformWindows).GetMethod(nameof(ClientPlatformAbstract.LatencySleep))!.DeclaringType);
        Assert.Equal(typeof(ClientPlatformAbstract),
            typeof(ClientPlatformWindows).GetProperty(nameof(ClientPlatformAbstract.LatencyOwnsFrameCap))!.DeclaringType);
    }

    [Fact]
    public void TheVulkanPlatformOverridesBothMembers()
    {
        Assert.Equal(typeof(VulkanClientPlatform),
            typeof(VulkanClientPlatform).GetMethod(nameof(ClientPlatformAbstract.LatencySleep))!.DeclaringType);
        Assert.Equal(typeof(VulkanClientPlatform),
            typeof(VulkanClientPlatform).GetProperty(nameof(ClientPlatformAbstract.LatencyOwnsFrameCap))!.DeclaringType);
    }
}
