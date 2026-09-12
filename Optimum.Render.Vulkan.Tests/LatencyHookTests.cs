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

    /// <summary>
    /// Latency review 2026-09-12: every backend answers <c>OwnsFrameCap</c> true
    /// as soon as its mode is not Off, which stands the lib's own FPS limiter
    /// down (seam S3). The cap therefore has to reach the backend, or turning
    /// LatencyMode on would silently uncap the client. It is handed over at the
    /// sleep, and only when it changed - an Apply per frame would re-arm the
    /// driver's heuristic every frame.
    /// </summary>
    [Fact]
    public void TheClientsFrameCapReachesAPacingBackendOnceAndOnlyWhenItChanges()
    {
        var backend = new RecordingLatencyBackend();
        VulkanClientPlatform platform = PlatformWith(backend);
        platform.MaxFps = 60f;
        // A cap no client setting produces, so the first sleep always changes it,
        // whatever this machine's vsync setting says.
        backend.Apply(new LatencySettings(LatencyMode.On, 999_999));
        backend.Applied.Clear();

        ulong expected = VulkanClientPlatform.FrameCapIntervalUs(60f, ClientSettings.VsyncMode);

        platform.LatencySleep();
        platform.LatencySleep();
        platform.LatencySleep();

        Assert.Equal(new List<LatencySettings> { new(LatencyMode.On, expected) }, backend.Applied);
        // The mode is the device's business; this site only ever sets the cap.
        Assert.Equal(LatencyMode.On, backend.Settings.Mode);
    }

    /// <summary>
    /// Off is off: a backend whose mode is Off is never applied to, so the frame
    /// with LatencyMode off is the frame Milestone 1 delivered.
    /// </summary>
    [Fact]
    public void ADisabledBackendIsNeverTouchedByTheFrameCap()
    {
        var backend = new RecordingLatencyBackend();
        VulkanClientPlatform platform = PlatformWith(backend);
        platform.MaxFps = 60f;

        for (int frame = 0; frame < 4; frame++) platform.LatencySleep();

        Assert.Empty(backend.Applied);
        Assert.Equal(LatencySettings.Disabled, backend.Settings);
    }

    /// <summary>
    /// The cap conversion itself, under exactly the conditions the lib's own
    /// limiter uses: vsync off, MaxFps above 10 and below the client's 241
    /// "unlimited". 0 is uncapped for every backend.
    /// </summary>
    [Theory]
    [InlineData(60f, 0, 16666UL)]
    [InlineData(120f, 0, 8333UL)]
    [InlineData(240f, 0, 4166UL)]
    [InlineData(241f, 0, 0UL)]
    [InlineData(1000f, 0, 0UL)]
    [InlineData(10f, 0, 0UL)]
    [InlineData(5f, 0, 0UL)]
    [InlineData(60f, 1, 0UL)]
    public void TheFrameCapFollowsTheClientsOwnConditions(float maxFps, int vsyncMode, ulong expectedUs)
    {
        Assert.Equal(expectedUs, VulkanClientPlatform.FrameCapIntervalUs(maxFps, vsyncMode));
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
