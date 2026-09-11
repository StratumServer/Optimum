using System;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Phase 1B step 1 through the seam: the Frame timeline paces the frame ring and
/// keys every deferred destruction.
/// </summary>
public class FrameTimelinePacingTests
{
    private readonly ITestOutputHelper _output;

    public FrameTimelinePacingTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A hundred presented frames, each of which renders into a texture and a
    /// framebuffer it creates, then deletes both (and a mesh) before presenting.
    /// The frame's own command buffer still names the texture, so destroying it
    /// before the timeline passed that frame is a validation error (image in use
    /// by a pending command buffer). Each frame start is exactly one pacing wait,
    /// nothing else in the loop waits, and the retire queue drains instead of growing.
    /// </summary>
    [SkippableFact]
    public unsafe void HundredFramesWithDeferredDeletesPaceOnceEachAndStayClean()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 4;
            const int frames = 100;

            int target = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int targetFramebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(targetFramebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(targetFramebuffer, 1);

            // Warm-up frame outside the counted window.
            seam.BeginFrame();
            seam.Present();

            long pacingBefore = VulkanStats.WaitCount(WaitSite.FramePacing);
            long[] othersBefore = OtherWaits();
            ulong signalledBefore = device!.TimelineForTests.FrameSignalled;
            int peakPending = 0;

            for (int frame = 0; frame < frames; frame++)
            {
                seam.BeginFrame();
                peakPending = Math.Max(peakPending, device.PendingRetirementsForTests);

                int scratch = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                int scratchFramebuffer = seam.CreateFramebuffer(size, size);
                seam.AttachTexture(scratchFramebuffer, EnumFramebufferAttachment.ColorAttachment0, scratch, 0);
                seam.SetDrawBuffers(scratchFramebuffer, 1);
                seam.BindFramebuffer(scratchFramebuffer);
                seam.ClearColor(0, 1f, 0f, 0f, 1f);

                int mesh = seam.CreateMesh(new MeshData(4, 6)
                {
                    xyz = new[] { -1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f },
                    VerticesCount = 4,
                    Indices = new[] { 0, 1, 2, 0, 2, 3 },
                    IndicesCount = 6,
                    mode = EnumDrawMode.Triangles,
                }, true);

                seam.BindFramebuffer(targetFramebuffer);
                // Exact in 8 bits (x.5 rounds either way): 0.2 -> 51, 0.25 -> 64.
                seam.ClearColor(0, frame / 255f, 0.2f, 0.25f, 1f);

                seam.DeleteMesh(mesh);
                seam.DeleteFramebuffer(scratchFramebuffer);
                seam.DeleteTexture(scratch);

                seam.Present();
            }

            long pacingDelta = VulkanStats.WaitCount(WaitSite.FramePacing) - pacingBefore;
            long[] othersAfter = OtherWaits();
            ulong signalledDelta = device.TimelineForTests.FrameSignalled - signalledBefore;
            _output.WriteLine($"pacing waits {pacingDelta}, frames signalled {signalledDelta}, peak pending {peakPending}");

            Assert.Equal(frames, pacingDelta);
            Assert.Equal((ulong)frames, signalledDelta);
            for (int i = 0; i < OtherSites.Length; i++)
            {
                long delta = othersAfter[i] - othersBefore[i];
                // A frame submit is counted at its own site; it is not a pacing wait.
                long expected = OtherSites[i] == WaitSite.QueueSubmit ? frames : 0;
                Assert.True(delta == expected,
                    $"{VulkanStats.WaitSiteTokens[(int)OtherSites[i]]}: {delta} waits in the loop, expected {expected}");
            }

            // Two frames in flight: at a frame start at most the last two frames'
            // deletions (texture, mesh, freed descriptor sets) can still be pending.
            Assert.True(peakPending <= 3 * 3, $"retire queue grew to {peakPending}");

            // Once the timeline passed the last frame, the next frame start frees everything.
            device.TimelineForTests.WaitForFrame(device.TimelineForTests.FrameSignalled, WaitSite.DeviceWaitIdle);
            seam.BeginFrame();
            Assert.Equal(0, device.PendingRetirementsForTests);

            seam.BindFramebuffer(targetFramebuffer);
            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            Assert.Equal(new byte[] { 99, 51, 64, 255 }, pixels[0..4]);
            seam.Present();

            GpuTest.AssertClean(seam);
        }
    }

    private static readonly WaitSite[] OtherSites =
    {
        WaitSite.UploadSubmit, WaitSite.FlushFrame, WaitSite.DeviceWaitIdle, WaitSite.Readback,
        WaitSite.OcclusionQuery, WaitSite.SwapchainAcquire, WaitSite.Present, WaitSite.QueueSubmit,
    };

    private static long[] OtherWaits()
    {
        var counts = new long[OtherSites.Length];
        for (int i = 0; i < OtherSites.Length; i++) counts[i] = VulkanStats.WaitCount(OtherSites[i]);
        return counts;
    }

    /// <summary>
    /// Frame values are handed out in order, one per submitted frame including
    /// mid-frame flushes, and the timeline counter reaches the last one.
    /// </summary>
    [SkippableFact]
    public unsafe void EverySubmittedFrameSignalsTheNextFrameValue()
    {
        Skip.IfNot(GpuTest.TryCreateContext(_output, null, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var ring = new FrameRing(context!, framesInFlight: 2, uniformRingSize: 1 << 20);
            for (ulong frame = 1; frame <= 7; frame++)
            {
                FrameSlot slot = ring.BeginFrame();
                Assert.Equal(frame, slot.FrameValue);
                Assert.Equal(frame, ring.Timeline.FrameRecorded);
                ring.EndFrame();
                Assert.Equal(frame, ring.Timeline.FrameSignalled);
            }

            ring.Timeline.WaitForFrame(7, WaitSite.DeviceWaitIdle);
            Assert.Equal(7UL, ring.Timeline.FrameCompleted);
            // Nothing ever signals Transfer yet.
            Assert.Equal(0UL, ring.Timeline.TransferCompleted);

            VulkanStats.WaitDeviceIdle(context!.Api, context.Device);
        }
    }
}
