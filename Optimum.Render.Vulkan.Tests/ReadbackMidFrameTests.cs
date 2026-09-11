using System;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Phase 1B step 2: readback inside a frame through the ReadbackManager and
/// FrameRing.SubmitPartial. The frame's recorded part is submitted, the copy is
/// waited for on its one timeline value, and the frame carries on in the same
/// slot: no flush into the next slot, no device-idle wait, no frame counter bump.
/// </summary>
public class ReadbackMidFrameTests
{
    private readonly ITestOutputHelper _output;

    public ReadbackMidFrameTests(ITestOutputHelper output) => _output = output;

    private static readonly WaitSite[] NeverSites =
    {
        WaitSite.FlushFrame, WaitSite.DeviceWaitIdle, WaitSite.OcclusionQuery,
    };

    private static long[] Counts(WaitSite[] sites)
    {
        var counts = new long[sites.Length];
        for (int i = 0; i < sites.Length; i++) counts[i] = VulkanStats.WaitCount(sites[i]);
        return counts;
    }

    private static void AssertUnchanged(WaitSite[] sites, long[] before)
    {
        long[] after = Counts(sites);
        for (int i = 0; i < sites.Length; i++)
        {
            Assert.True(after[i] == before[i],
                VulkanStats.WaitSiteTokens[(int)sites[i]] + ": " + (after[i] - before[i]) + " waits, expected 0");
        }
    }

    private static int CreateTarget(VulkanDevice seam, int size, out int texture)
    {
        texture = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(size, size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        seam.SetDrawBuffers(framebuffer, 1);
        return framebuffer;
    }

    /// <summary>
    /// Binds and reads a target. Only meaningful inside a frame for more than one
    /// target: between frames BindFramebuffer is a no-op and the read sees
    /// whichever target the last frame bound.
    /// </summary>
    private static byte[] Read(VulkanDevice seam, int framebuffer, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        unsafe
        {
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, width, height, (IntPtr)destination);
            }
        }
        return pixels;
    }

    private static void AssertEvery(byte[] pixels, byte r, byte g, byte b, byte a, string what)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.True(pixels[i] == r && pixels[i + 1] == g && pixels[i + 2] == b && pixels[i + 3] == a,
                what + ": pixel " + i / 4 + " is " + pixels[i] + "," + pixels[i + 1] + "," + pixels[i + 2] + "," +
                pixels[i + 3] + ", expected " + r + "," + g + "," + b + "," + a);
        }
    }

    private static unsafe void SetTint(VulkanDevice seam, int ubo, byte r, byte g, byte b)
    {
        var tint = new float[] { r / 255f, g / 255f, b / 255f, 1f };
        fixed (float* values = tint)
        {
            seam.UpdateUniformBuffer(ubo, (IntPtr)values, 0, sizeof(float) * 4);
        }
    }

    /// <summary>
    /// Draw into A, read A mid-frame, then draw into B with the same uniform block
    /// (its ring snapshot was taken before the partial submit and is reused after
    /// it), change the block and draw into A again, present. The mid-frame read
    /// sees the first draw; after present A holds the second tint and B the first.
    /// The frame submitted twice under two timeline values from one pacing wait,
    /// the read waited once at the readback site, and nothing flushed or waited
    /// for the device.
    /// </summary>
    [SkippableFact]
    public void AReadbackMidFrameSeesEarlierDrawsAndLaterDrawsStillReachTheFrame()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 16;

            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, """
                #version 330 core
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                }
                """, """
                #version 330 core
                layout(std140) uniform Tint { vec4 tint; };
                out vec4 outColor;
                void main(void) { outColor = tint; }
                """, "readback-tint");

            int targetA = CreateTarget(seam, size, out _);
            int targetB = CreateTarget(seam, size, out _);
            int ubo = seam.CreateUniformBuffer(program, 0, "Tint", sizeof(float) * 4);
            SetTint(seam, ubo, 60, 120, 180);
            seam.BindUniformBuffer(ubo);

            seam.BeginFrame();
            seam.Present();

            long[] neverBefore = Counts(NeverSites);
            long uploadsBefore = VulkanStats.WaitCount(WaitSite.UploadSubmit);
            long readbacksBefore = VulkanStats.WaitCount(WaitSite.Readback);
            long pacingBefore = VulkanStats.WaitCount(WaitSite.FramePacing);
            long submitsBefore = VulkanStats.WaitCount(WaitSite.QueueSubmit);
            ulong signalledBefore = device!.TimelineForTests.FrameSignalled;

            seam.BeginFrame();
            seam.UseProgram(program);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.SetViewport(0, 0, size, size);

            seam.BindFramebuffer(targetA);
            seam.DrawFullscreenTriangle();
            byte[] midFrame = Read(seam, targetA, size, size);
            long readbacksMid = VulkanStats.WaitCount(WaitSite.Readback) - readbacksBefore;

            seam.BindFramebuffer(targetB);
            seam.DrawFullscreenTriangle();

            SetTint(seam, ubo, 200, 40, 20);
            seam.BindFramebuffer(targetA);
            seam.DrawFullscreenTriangle();
            seam.Present();

            long submits = VulkanStats.WaitCount(WaitSite.QueueSubmit) - submitsBefore;
            ulong signalled = device.TimelineForTests.FrameSignalled - signalledBefore;
            long pacing = VulkanStats.WaitCount(WaitSite.FramePacing) - pacingBefore;

            AssertEvery(midFrame, 60, 120, 180, 255, "mid-frame read of A");
            Assert.Equal(1, readbacksMid);
            Assert.Equal(2, submits);
            Assert.Equal(2UL, signalled);
            Assert.Equal(1, pacing);
            Assert.Equal(uploadsBefore, VulkanStats.WaitCount(WaitSite.UploadSubmit));

            // Binding is a no-op between frames, so both targets are read at the
            // start of the next frame (attachments load what the last frame stored).
            seam.BeginFrame();
            AssertEvery(Read(seam, targetA, size, size), 200, 40, 20, 255, "A after present");
            AssertEvery(Read(seam, targetB, size, size), 60, 120, 180, 255, "B after present");
            seam.Present();
            AssertUnchanged(NeverSites, neverBefore);

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// Twelve presented frames, each clearing a target three times with two
    /// readbacks in between. Every read sees its own frame's latest clear, the
    /// last clear of each frame survives to the next frame's start, and the
    /// timeline counts three submissions per frame from one pacing wait each:
    /// slots rotate normally even though every frame submits in parts.
    /// </summary>
    [SkippableFact]
    public void ReadbacksInConsecutiveFramesPaceOncePerFrameAndReadTheirOwnFrame()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 4;
            const int frames = 12;
            int target = CreateTarget(seam, size, out _);

            seam.BeginFrame();
            seam.Present();

            long[] neverBefore = Counts(NeverSites);
            long readbacksBefore = VulkanStats.WaitCount(WaitSite.Readback);
            long pacingBefore = VulkanStats.WaitCount(WaitSite.FramePacing);
            long submitsBefore = VulkanStats.WaitCount(WaitSite.QueueSubmit);
            ulong signalledBefore = device!.TimelineForTests.FrameSignalled;

            for (int frame = 0; frame < frames; frame++)
            {
                seam.BeginFrame();
                if (frame > 0)
                {
                    // Nothing cleared yet this frame: the previous frame's last clear.
                    byte[] carried = Read(seam, target, 1, 1);
                    Assert.Equal(new byte[] { (byte)(frame - 1 + 200), 51, 64, 255 }, carried);
                }
                else
                {
                    seam.BindFramebuffer(target);
                    seam.ClearColor(0, 0f, 0.2f, 0.25f, 1f);
                    Read(seam, target, 1, 1);
                }

                seam.BindFramebuffer(target);
                seam.ClearColor(0, (frame + 100) / 255f, 0.2f, 0.25f, 1f);
                AssertEvery(Read(seam, target, size, size), (byte)(frame + 100), 51, 64, 255, "frame " + frame);

                seam.BindFramebuffer(target);
                seam.ClearColor(0, (frame + 200) / 255f, 0.2f, 0.25f, 1f);
                seam.Present();
            }

            Assert.Equal(frames, VulkanStats.WaitCount(WaitSite.FramePacing) - pacingBefore);
            Assert.Equal(2 * frames, VulkanStats.WaitCount(WaitSite.Readback) - readbacksBefore);
            Assert.Equal(3 * frames, VulkanStats.WaitCount(WaitSite.QueueSubmit) - submitsBefore);
            Assert.Equal((ulong)(3 * frames), device.TimelineForTests.FrameSignalled - signalledBefore);
            AssertUnchanged(NeverSites, neverBefore);

            AssertEvery(Read(seam, target, size, size), frames - 1 + 200, 51, 64, 255, "after the loop");
            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// Two 4 MiB readbacks in one frame outgrow the 1 MiB arena twice; the second
    /// growth retires an arena a submitted copy wrote into. Both reads are exact,
    /// and retiring through the timeline keeps validation clean.
    /// </summary>
    [SkippableFact]
    public void ReadbacksLargerThanTheArenaGrowItAndStayExact()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 1024;
            Assert.True((ulong)size * size * 4 > ReadbackManager.MinimumArenaSize);
            int target = CreateTarget(seam, size, out _);

            seam.BeginFrame();
            seam.BindFramebuffer(target);
            seam.ClearColor(0, 10 / 255f, 20 / 255f, 30 / 255f, 1f);
            AssertEvery(Read(seam, target, size, size), 10, 20, 30, 255, "first");
            seam.BindFramebuffer(target);
            seam.ClearColor(0, 40 / 255f, 50 / 255f, 60 / 255f, 1f);
            AssertEvery(Read(seam, target, size, size), 40, 50, 60, 255, "second");
            seam.Present();

            for (int frame = 0; frame < 3; frame++)
            {
                seam.BeginFrame();
                seam.Present();
            }

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// Review fix: an RGBA8 readback leaves the arena cursor at 4, which the old
    /// 8-byte alignment rounded to 8, an illegal offset for the RGBA32F copy that
    /// follows (a multiple of the 16-byte texel is required). Both reads are
    /// exact, the second one lands on a legal offset and validation stays clean.
    /// </summary>
    [SkippableFact]
    public unsafe void ReadbacksOfDifferentTexelSizesInOneFrameUseLegalOffsets()
    {
        Assert.Equal(8UL, ReadbackManager.OffsetAlignmentFor(1));
        Assert.Equal(8UL, ReadbackManager.OffsetAlignmentFor(4));
        Assert.Equal(16UL, ReadbackManager.OffsetAlignmentFor(16));
        Assert.Equal(24UL, ReadbackManager.OffsetAlignmentFor(12));

        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            byte[] rgba = { 11, 22, 33, 44 };
            var floats = new float[2 * 2 * 4];
            for (int i = 0; i < floats.Length; i++) floats[i] = i * 0.25f - 1.5f;
            var floatBytes = new byte[floats.Length * sizeof(float)];
            System.Buffer.BlockCopy(floats, 0, floatBytes, 0, floatBytes.Length);

            int small;
            fixed (byte* pixels = rgba)
                small = seam.CreateTexture2D(1, 1, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba,
                    (IntPtr)pixels, false);
            int wide;
            fixed (byte* pixels = floatBytes)
                wide = seam.CreateTexture2DRaw(2, 2, 0x8814, (IntPtr)pixels, 16);

            seam.BeginFrame();
            seam.Present();

            seam.BeginFrame();
            byte[] first = device!.ReadBackLevel0ForTests(small);
            byte[] second = device.ReadBackLevel0ForTests(wide);
            seam.Present();

            Assert.Equal(rgba, first);
            Assert.Equal(floatBytes, second);
            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// A texture upload on the render thread in the middle of a frame submits
    /// the frame's recorded part first (the clear of A) and recording continues
    /// (the clear of B): both clears and the uploaded texels land, with two
    /// frame submissions and no flush.
    /// </summary>
    [SkippableFact]
    public unsafe void AnUploadInsideAFrameSubmitsTheRecordedPartAndRecordingContinues()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 4;
            int targetA = CreateTarget(seam, size, out _);
            int targetB = CreateTarget(seam, size, out _);
            int uploaded = CreateTarget(seam, size, out int texture);

            var data = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++)
            {
                data[i * 4] = (byte)(i * 16);
                data[i * 4 + 1] = 77;
                data[i * 4 + 2] = 99;
                data[i * 4 + 3] = 255;
            }

            seam.BeginFrame();
            seam.Present();

            long[] neverBefore = Counts(NeverSites);
            long submitsBefore = VulkanStats.WaitCount(WaitSite.QueueSubmit);

            seam.BeginFrame();
            seam.BindFramebuffer(targetA);
            seam.ClearColor(0, 1f, 0f, 0f, 1f);
            fixed (byte* pixels = data)
                seam.UploadTexture2D(texture, 0, 0, 0, size, size, EnumTexturePixelFormat.Rgba, (IntPtr)pixels);
            seam.BindFramebuffer(targetB);
            seam.ClearColor(0, 0f, 0f, 1f, 1f);
            seam.Present();

            Assert.Equal(2, VulkanStats.WaitCount(WaitSite.QueueSubmit) - submitsBefore);
            AssertUnchanged(NeverSites, neverBefore);

            // Binding is a no-op between frames: read all three in the next frame.
            seam.BeginFrame();
            AssertEvery(Read(seam, targetA, size, size), 255, 0, 0, 255, "A");
            AssertEvery(Read(seam, targetB, size, size), 0, 0, 255, 255, "B");
            Assert.Equal(data, Read(seam, uploaded, size, size));
            seam.Present();
            AssertUnchanged(NeverSites, neverBefore);

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// The ring itself: a partial submit signals the slot's current value, stays
    /// in the slot with its uniform cursor, and records on under a fresh value;
    /// the next frame in that slot starts only after the frame's last value.
    /// </summary>
    [SkippableFact]
    public void APartialSubmitStaysInItsSlotAndTheSlotsNextFrameWaitsForItsLastValue()
    {
        Skip.IfNot(GpuTest.TryCreateContext(_output, null, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var ring = new FrameRing(context!, framesInFlight: 2, uniformRingSize: 1 << 20);

            FrameSlot first = ring.BeginFrame();
            Assert.Equal(1UL, first.FrameValue);
            Assert.True(first.TryAllocateUniforms(100, out _));
            ulong used = first.UniformBytesUsed;
            CommandBufferHandle before = new(first.CommandBuffer.Handle);

            Assert.Equal(1UL, ring.SubmitPartial());
            Assert.Same(first, ring.Current);
            Assert.Equal(2UL, first.FrameValue);
            Assert.Equal(1UL, ring.Timeline.FrameSignalled);
            Assert.Equal(1, first.PartialSubmits);
            Assert.Equal(used, first.UniformBytesUsed);
            Assert.NotEqual(before.Value, first.CommandBuffer.Handle);
            ring.EndFrame();
            Assert.Equal(2UL, first.LastSignalledValue);

            FrameSlot second = ring.BeginFrame();
            Assert.NotSame(first, second);
            Assert.Equal(3UL, second.FrameValue);
            ring.EndFrame();

            FrameSlot again = ring.BeginFrame();
            Assert.Same(first, again);
            Assert.True(ring.Timeline.FrameCompleted >= 2UL, "the slot restarted before its partial frame finished");
            Assert.Equal(4UL, again.FrameValue);
            Assert.Equal(0, again.PartialSubmits);
            Assert.Equal(0UL, again.UniformBytesUsed);
            ring.EndFrame();

            VulkanStats.WaitDeviceIdle(context!.Api, context.Device);
        }
    }

    private readonly record struct CommandBufferHandle(nint Value);
}
