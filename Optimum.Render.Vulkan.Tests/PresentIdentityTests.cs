using System;
using System.Collections.Generic;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Latency seams S2 and S5, against a real (hidden) window: the present id is
/// allocated once per vkQueuePresentKHR, strictly increases, survives every
/// swapchain recreation a resize or a vsync toggle causes, and each creation
/// tells the latency backend exactly once so a backend can re-apply its
/// per-swapchain state.
/// </summary>
public class PresentIdentityTests
{
    private const int Width = 256;
    private const int Height = 192;

    private readonly ITestOutputHelper _output;

    public PresentIdentityTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public unsafe void PresentIdsIncreaseAcrossResizesAndEachSwapchainIsAnnouncedOnce()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window), "No usable window system.");

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            var latency = new RecordingLatencyBackend();
            device.SetLatencyBackend(latency);

            if (!device.Initialize((IntPtr)window, Width, Height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            using (device)
            {
                VulkanDevice seam = device;
                Swapchain swapchain = device.SwapchainForTests!;
                int programId = SwapchainTests.LinkFullscreenProgram(seam);
                var presentIds = new List<ulong>();

                void RenderFrames(int count, int w, int h)
                {
                    for (int frame = 0; frame < count; frame++)
                    {
                        seam.BeginLatencyFrame();
                        seam.BeginFrame();
                        seam.NoteRenderStageStarted();
                        seam.BindDefaultFramebuffer();
                        seam.ClearColor(0, 0.2f, 0.4f, 0.6f, 1f);
                        seam.UseProgram(programId);
                        seam.SetViewport(0, 0, w, h);
                        seam.SetDepthTest(false);
                        seam.SetCullFace(false);
                        seam.DrawFullscreenTriangle();
                        seam.Present();
                        if (device.LastPresentTimingsForTests.Presented)
                        {
                            presentIds.Add(device.LastPresentIdForTests);
                            // The map keeps the frame that produced it (1:1 until
                            // frame generation presents one frame twice).
                            Assert.True(swapchain.PresentIds.TryGetFrameId(
                                device.LastPresentIdForTests, out ulong mapped));
                            Assert.Equal(seam.LatencyFrameId, mapped);
                        }
                    }
                }

                // The first swapchain is announced like every later one.
                Assert.Equal(swapchain.Creations, latency.SwapchainCount);
                Assert.Equal(1, latency.SwapchainCount);

                RenderFrames(3, Width, Height);

                (int W, int H)[] sizes = { (320, 240), (200, 150), (512, 384), (256, 192) };
                int iterations = 0;
                foreach ((int w, int h) in sizes)
                {
                    GLFW.SetWindowSize(window, w, h);
                    GLFW.PollEvents();
                    seam.Resize(w, h);
                    if (iterations % 2 == 1) seam.SetVSync(iterations % 4 == 1);
                    RenderFrames(3, w, h);
                    iterations++;
                }

                _output.WriteLine($"{presentIds.Count} presents, ids {presentIds[0]}..{presentIds[^1]}, " +
                    $"{swapchain.Creations} swapchains created, {latency.SwapchainCount} announced");

                // Strictly increasing, recreation included: the counter is global
                // and is never reset by a rebuild.
                Assert.True(presentIds.Count >= 12, $"only {presentIds.Count} presents");
                for (int i = 1; i < presentIds.Count; i++)
                {
                    Assert.True(presentIds[i] > presentIds[i - 1],
                        $"present id {presentIds[i]} did not exceed {presentIds[i - 1]} at index {i}");
                }

                // Exactly one announcement per creation, and more than one
                // creation happened (the resizes rebuilt the chain).
                Assert.True(swapchain.Creations > 1, $"{swapchain.Creations} swapchain creations");
                Assert.Equal(swapchain.Creations, latency.SwapchainCount);

                // Distinct handles: a re-announced old handle would let a backend
                // re-apply state to a chain that is already retired.
                Assert.Equal(latency.Swapchains.Count, new HashSet<ulong>(HandlesOf(latency)).Count);

                GpuTest.AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    private static IEnumerable<ulong> HandlesOf(RecordingLatencyBackend latency)
    {
        foreach (Silk.NET.Vulkan.SwapchainKHR handle in latency.Swapchains) yield return handle.Handle;
    }

    /// <summary>
    /// The counter itself, without a GPU: one value per present, never reused,
    /// and the map answers with the frame that produced each id while it holds it.
    /// </summary>
    [Fact]
    public void ThePresentIdCounterAndMapArePlainMonotonicBookkeeping()
    {
        ulong first = PresentIdCounter.Next();
        ulong second = PresentIdCounter.Next();
        Assert.True(second > first);
        Assert.Equal(second, PresentIdCounter.Current);

        var map = new PresentIdMap(4);
        Assert.False(map.TryGetFrameId(1, out _));
        for (ulong i = 1; i <= 4; i++) map.Record(i, 100 + i);
        Assert.True(map.TryGetFrameId(3, out ulong frame));
        Assert.Equal(103UL, frame);
        Assert.Equal(4UL, map.LastPresentId);
        Assert.Equal(104UL, map.LastFrameId);

        // It wraps rather than growing; the oldest entry is the one that goes.
        map.Record(5, 105);
        Assert.False(map.TryGetFrameId(1, out _));
        Assert.True(map.TryGetFrameId(5, out frame));
        Assert.Equal(105UL, frame);
    }

    /// <summary>
    /// Seam S8: a present path says which latency backends it can run with. The
    /// blit path is an ordinary Vulkan present, so all four apply to it.
    /// </summary>
    [Fact]
    public void TheBlitPresentPathSupportsEveryVulkanSwapchainBackend()
    {
        IPresentPath path = new BlitPresentPath(null!, null!, static () => null);
        Assert.Equal(
            new[]
            {
                LatencyBackendKind.None,
                LatencyBackendKind.Native,
                LatencyBackendKind.NvLowLatency2,
                LatencyBackendKind.AmdAntiLag,
            },
            path.SupportedLatencyBackends);
    }
}
