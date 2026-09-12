using System;
using System.Collections.Generic;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Latency seams S2 and S4, against a real (hidden) window: several presented
/// frames through the recording backend must stamp the renderer's markers in one
/// order, once each, under one frame id per frame that increases by exactly one.
///
/// The two markers the lib hook owns (InputSample and SimulationStart, stamped in
/// <c>VulkanClientPlatform.LatencySleep</c>) are not the renderer's, so they are
/// not asserted here; what is asserted is that the renderer stamps its own five
/// and never stamps one twice, however often the client brackets a render stage.
/// </summary>
public class LatencyMarkerOrderTests
{
    private const int Width = 256;
    private const int Height = 192;
    private const int Frames = 6;

    private readonly ITestOutputHelper _output;

    public LatencyMarkerOrderTests(ITestOutputHelper output) => _output = output;

    /// <summary>What the renderer owns, in the order one frame must produce it.</summary>
    private static readonly LatencyMarker[] ExpectedPerFrame =
    {
        LatencyMarker.SimulationEnd,
        LatencyMarker.RenderSubmitStart,
        LatencyMarker.RenderSubmitEnd,
        LatencyMarker.PresentStart,
        LatencyMarker.PresentEnd,
    };

    [SkippableFact]
    public unsafe void EveryFrameStampsTheRenderersMarkersOnceInOrderUnderItsOwnId()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window), "No usable window system.");

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            var latency = new RecordingLatencyBackend();
            // Before Initialize: the backend has to be the one the first
            // swapchain, the frame ring and the stats source see.
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
                int programId = SwapchainTests.LinkFullscreenProgram(seam);
                var frameIds = new List<ulong>();

                for (int frame = 0; frame < Frames; frame++)
                {
                    // What the lib hook does before input is sampled (seam S3).
                    ulong frameId = seam.BeginLatencyFrame();
                    frameIds.Add(frameId);

                    seam.BeginFrame();
                    Assert.Equal(frameId, seam.LatencyFrameId);

                    // The client brackets many render stages per frame; only the
                    // first may stamp the pair.
                    seam.NoteRenderStageStarted();
                    seam.NoteRenderStageStarted();
                    seam.NoteRenderStageStarted();

                    seam.BindDefaultFramebuffer();
                    seam.ClearColor(0, 0.1f, 0.3f, 0.5f, 1f);
                    seam.UseProgram(programId);
                    seam.SetViewport(0, 0, Width, Height);
                    seam.SetDepthTest(false);
                    seam.SetCullFace(false);
                    seam.DrawFullscreenTriangle();
                    seam.Present();
                }

                // One id per frame, increasing by exactly one.
                for (int i = 1; i < frameIds.Count; i++)
                {
                    Assert.Equal(frameIds[i - 1] + 1, frameIds[i]);
                }

                foreach (ulong id in frameIds)
                {
                    LatencyMarker[] markers = latency.MarkersOf(id);
                    _output.WriteLine($"frame {id}: {string.Join(", ", markers)}");
                    Assert.Equal(ExpectedPerFrame, markers);
                }

                // Every frame presented, each present paired with its own frame
                // id, and the present ids increase.
                Assert.Equal(frameIds.Count, latency.Presents.Count);
                for (int i = 0; i < frameIds.Count; i++)
                {
                    Assert.Equal(frameIds[i], latency.Presents[i].FrameId);
                    if (i > 0) Assert.True(latency.Presents[i].PresentId > latency.Presents[i - 1].PresentId);
                }

                // Seam S4: every submit of the frame passed the tag hook. Two
                // submits a frame at least (Submit A and Submit B).
                Assert.True(latency.TagSubmitCount >= 2 * Frames,
                    $"{latency.TagSubmitCount} tagged submits over {Frames} frames");

                GpuTest.AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    /// <summary>
    /// A headless device (no swapchain) still owns the frame identity and the
    /// submit markers: nothing presents, so PresentStart/End and OnPresent stay
    /// absent rather than being stamped against a present that never happened.
    /// </summary>
    [SkippableFact]
    public void AHeadlessFrameStampsRenderSubmitEndAndNoPresentMarkers()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? created), "No Vulkan device.");
        using VulkanDevice device = created!;

        var latency = new RecordingLatencyBackend();
        device.SetLatencyBackend(latency);

        VulkanDevice seam = device;
        for (int frame = 0; frame < 3; frame++)
        {
            seam.BeginFrame();
            seam.NoteRenderStageStarted();
            seam.Present();
        }

        Assert.Empty(latency.Presents);
        for (ulong id = 1; id <= 3; id++)
        {
            Assert.Equal(
                new[] { LatencyMarker.SimulationEnd, LatencyMarker.RenderSubmitStart, LatencyMarker.RenderSubmitEnd },
                latency.MarkersOf(id));
        }

        GpuTest.AssertClean(seam);
    }

    /// <summary>
    /// A frame that reaches BeginFrame without the lib hook (headless, or any
    /// path with no platform) allocates its own id, so the identity exists
    /// exactly once either way and the ids still increase by one.
    /// </summary>
    [SkippableFact]
    public void AFrameWithoutTheHookAllocatesItsOwnIdExactlyOnce()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? created), "No Vulkan device.");
        using VulkanDevice device = created!;

        VulkanDevice seam = device;
        var ids = new List<ulong>();
        for (int frame = 0; frame < 4; frame++)
        {
            if (frame % 2 == 0) seam.BeginLatencyFrame();
            seam.BeginFrame();
            ids.Add(seam.LatencyFrameId);
            seam.Present();
        }

        Assert.Equal(new ulong[] { 1, 2, 3, 4 }, ids);
    }
}
