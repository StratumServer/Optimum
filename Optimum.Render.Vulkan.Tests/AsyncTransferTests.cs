using System;
using System.Collections.Generic;
using System.Threading;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Phase 1B step 3: no upload waits. Uploads, mip chains and staged buffer writes
/// are recorded (from any thread) into an upload batch the next frame submission
/// carries first, or inline into the frame command buffer when that already used
/// the destination, so GL's call order holds. Nothing in the upload path waits.
/// </summary>
public class AsyncTransferTests
{
    private readonly ITestOutputHelper _output;

    public AsyncTransferTests(ITestOutputHelper output) => _output = output;

    private const string FullscreenVertex = """
        #version 330 core
        void main() {
            gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2),
                               -1 + ((gl_VertexID & 2) << 1), 0, 1);
        }
        """;

    private static int CreateTarget(IOptimumGraphicsDevice seam, int size, out int texture)
    {
        texture = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(size, size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        seam.SetDrawBuffers(framebuffer, 1);
        return framebuffer;
    }

    /// <summary>Binds and reads a target; inside a frame, so the bind takes effect.</summary>
    private static unsafe byte[] Read(IOptimumGraphicsDevice seam, int framebuffer, int size)
    {
        var pixels = new byte[size * size * 4];
        fixed (byte* destination = pixels)
        {
            seam.BindFramebuffer(framebuffer);
            seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
        }
        return pixels;
    }

    private static byte[] Fill(int size, byte r, byte g, byte b, byte a = 255)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }
        return pixels;
    }

    private static unsafe void Upload(IOptimumGraphicsDevice seam, int texture, int size, byte[] pixels)
    {
        fixed (byte* source = pixels)
        {
            seam.UploadTexture2D(texture, 0, 0, 0, size, size, EnumTexturePixelFormat.Rgba, (IntPtr)source);
        }
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

    /// <summary>A quad from x = -1 to <paramref name="right" />, full height.</summary>
    private static MeshData Quad(float right) => new(4, 6)
    {
        xyz = new[] { -1f, -1f, 0f, right, -1f, 0f, right, 1f, 0f, -1f, 1f, 0f },
        VerticesCount = 4,
        Indices = new[] { 0, 1, 2, 0, 2, 3 },
        IndicesCount = 6,
    };

    /// <summary>
    /// The plan's gate. A worker thread uploads into its own textures the whole
    /// time the render thread records 60 frames with Present between them; every
    /// frame inserts a mipmapped texture, regenerates its chain, and creates and
    /// draws a static mesh on device-local memory (its vertices and indices staged
    /// through the same batches). Over all of it: zero blocking uploads, zero waits
    /// at the upload, flush and readback sites. The worker's last uploads are read
    /// back on frame N+2, the last mesh's geometry and the last inserted texture's
    /// smallest mip are checked, every reserved Transfer value was signalled, and
    /// the retire queue drains.
    /// </summary>
    [SkippableFact]
    public unsafe void WorkerUploadsWhileFramesRecordNeverBlockAndLandByFrameNPlus2()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            device!.DeviceLocalStaticMeshesForTests = true;
            const int size = 8;
            const int frames = 60;
            const int workerTextureCount = 4;

            int meshProgram = VulkanDeviceIntegrationTests.LinkProgram(seam, """
                #version 330 core
                layout(location = 0) in vec3 position;
                void main() { gl_Position = vec4(position, 1); }
                """, """
                #version 330 core
                out vec4 color;
                void main() { color = vec4(1); }
                """, "async-mesh");
            int mipProgram = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                uniform sampler2D source;
                out vec4 color;
                void main() { color = textureLod(source, vec2(0.5), 3.0); }
                """, "async-mip");
            int mipSampler = seam.CreateSampler(false);

            var workerTextures = new int[workerTextureCount];
            var workerTargets = new int[workerTextureCount];
            for (int i = 0; i < workerTextureCount; i++) workerTargets[i] = CreateTarget(seam, size, out workerTextures[i]);
            int meshTarget = CreateTarget(seam, size, out _);
            int mipTarget = CreateTarget(seam, size, out _);

            // Warm up: placeholder uploads and the first pipelines are not what is measured.
            seam.BeginFrame();
            seam.Present();

            long blockingBefore = VulkanStats.BlockingUploads;
            long uploadWaitsBefore = VulkanStats.WaitCount(WaitSite.UploadSubmit);
            long flushWaitsBefore = VulkanStats.WaitCount(WaitSite.FlushFrame);
            long readbackWaitsBefore = VulkanStats.WaitCount(WaitSite.Readback);
            long idleWaitsBefore = VulkanStats.WaitCount(WaitSite.DeviceWaitIdle);
            long requestsBefore = VulkanStats.UploadRequests;

            int stop = 0;
            int rounds = 0;
            Exception? workerError = null;
            using var started = new ManualResetEventSlim(false);
            var worker = new Thread(() =>
            {
                try
                {
                    started.Set();
                    while (Volatile.Read(ref stop) == 0)
                    {
                        byte value = (byte)(rounds * 7);
                        Upload(seam, workerTextures[rounds % workerTextureCount], size, Fill(size, value, value, value));
                        rounds++;
                        Thread.Sleep(1);
                    }
                    for (int i = 0; i < workerTextureCount; i++)
                    {
                        Upload(seam, workerTextures[i], size, Fill(size, (byte)(10 + i * 40), (byte)(200 - i * 30), (byte)(5 * i)));
                    }
                }
                catch (Exception error)
                {
                    workerError = error;
                }
            }) { IsBackground = true, Name = "async-transfer-worker" };
            worker.Start();
            started.Wait();

            int previousTexture = 0;
            int previousMesh = 0;
            int lastTexture = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                seam.BeginFrame();

                // A texture insert with its mip chain, then the chain again.
                byte[] colour = Fill(size, (byte)(frame * 4), (byte)(255 - frame * 4), 90);
                fixed (byte* pixels = colour)
                {
                    lastTexture = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                        EnumTexturePixelFormat.Rgba, (IntPtr)pixels, true);
                }
                seam.GenerateMipmaps(lastTexture);

                // A static mesh: device-local buffers, filled through staging, drawn this frame.
                int mesh = seam.CreateMesh(Quad(frame % 2 == 1 ? 0f : 1f), true);
                seam.BindFramebuffer(meshTarget);
                seam.UseProgram(meshProgram);
                seam.SetViewport(0, 0, size, size);
                seam.SetDepthTest(false);
                seam.SetCullFace(false);
                seam.SetBlend(false, EnumBlendMode.Standard);
                seam.ClearColor(0, 0f, 0f, 0f, 1f);
                seam.DrawMesh(mesh);

                seam.Present();

                if (previousTexture != 0) seam.DeleteTexture(previousTexture);
                if (previousMesh != 0) seam.DeleteMesh(previousMesh);
                previousTexture = lastTexture;
                previousMesh = mesh;
            }

            Volatile.Write(ref stop, 1);
            Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "the worker did not finish");
            Assert.Null(workerError);
            _output.WriteLine("worker upload rounds during " + frames + " frames: " + rounds +
                "; batches " + device.UploadsForTests.BatchCount +
                "; staging overflows so far " + VulkanStats.StagingOverflows);
            Assert.True(rounds > 0, "the worker never uploaded while frames recorded");

            Assert.Equal(0, VulkanStats.BlockingUploads - blockingBefore);
            Assert.Equal(0, VulkanStats.WaitCount(WaitSite.UploadSubmit) - uploadWaitsBefore);
            Assert.Equal(0, VulkanStats.WaitCount(WaitSite.FlushFrame) - flushWaitsBefore);
            Assert.Equal(0, VulkanStats.WaitCount(WaitSite.Readback) - readbackWaitsBefore);
            Assert.Equal(0, VulkanStats.WaitCount(WaitSite.DeviceWaitIdle) - idleWaitsBefore);
            // Per frame: upload + mip chain, the explicit chain, and the mesh's staged parts.
            Assert.True(VulkanStats.UploadRequests - requestsBefore >= frames * 4L);

            // Frame N+1 carries the worker's last batch; frame N+2 reads.
            seam.BeginFrame();
            seam.Present();
            FrameTimeline timeline = device.TimelineForTests;
            timeline.WaitForFrame(timeline.FrameSignalled, WaitSite.DeviceWaitIdle);
            Assert.Equal(timeline.TransferRecorded, timeline.TransferSignalled);

            seam.BeginFrame();
            Assert.Equal(0, device.PendingRetirementsForTests);

            for (int i = 0; i < workerTextureCount; i++)
            {
                AssertEvery(Read(seam, workerTargets[i], size), (byte)(10 + i * 40), (byte)(200 - i * 30), (byte)(5 * i), 255,
                    "worker texture " + i);
            }

            // The last quad covered the left half only (frame 59 is odd).
            byte[] meshPixels = Read(seam, meshTarget, size);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int at = (y * size + x) * 4;
                    byte expected = x < size / 2 ? (byte)255 : (byte)0;
                    Assert.True(meshPixels[at] == expected && meshPixels[at + 3] == 255,
                        "mesh pixel " + x + "," + y + " is " + meshPixels[at] + ", expected " + expected);
                }
            }

            // The last inserted texture's 1x1 level holds its uniform colour.
            seam.BindFramebuffer(mipTarget);
            seam.UseProgram(mipProgram);
            seam.SetViewport(0, 0, size, size);
            seam.SetSamplerUnit(mipProgram, "source", 0);
            seam.BindTexture(0, lastTexture);
            seam.BindSampler(0, mipSampler);
            seam.DrawFullscreenTriangle();
            AssertEvery(Read(seam, mipTarget, size), (byte)((frames - 1) * 4), (byte)(255 - (frames - 1) * 4), 90, 255,
                "smallest mip of the last inserted texture");
            seam.Present();

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// GL order for a re-upload. A texture sampled by a draw in this frame and
    /// then uploaded again (level 0 and its chain) has to reach only the draws
    /// recorded after the upload. The batch runs before the whole frame command
    /// buffer, so those two go inline; the earlier upload of the same frame, before
    /// any use, stays batched. Draw A sees red, draw B green, and nothing waits.
    /// </summary>
    [SkippableFact]
    public unsafe void AReuploadOfATextureSampledThisFrameKeepsGlOrdering()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            IOptimumGraphicsDevice seam = device!;
            const int size = 4;
            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                uniform sampler2D source;
                out vec4 color;
                void main() { color = textureLod(source, vec2(0.5), 2.0); }
                """, "reupload-order");
            int source = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, true);
            int sampler = seam.CreateSampler(false);
            int targetA = CreateTarget(seam, size, out _);
            int targetB = CreateTarget(seam, size, out _);
            int targetC = CreateTarget(seam, size, out _);

            seam.BeginFrame();
            seam.Present();

            long blockingBefore = VulkanStats.BlockingUploads;
            long inlineBefore = VulkanStats.InlineUploads;
            long submitsBefore = VulkanStats.WaitCount(WaitSite.QueueSubmit);

            seam.BeginFrame();
            Upload(seam, source, size, Fill(size, 255, 0, 0));
            seam.GenerateMipmaps(source);
            Assert.Equal(0, VulkanStats.InlineUploads - inlineBefore);

            seam.BindFramebuffer(targetA);
            seam.UseProgram(program);
            seam.SetViewport(0, 0, size, size);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.SetSamplerUnit(program, "source", 0);
            seam.BindTexture(0, source);
            seam.BindSampler(0, sampler);
            seam.DrawFullscreenTriangle();

            Upload(seam, source, size, Fill(size, 0, 255, 0));
            seam.GenerateMipmaps(source);
            Assert.Equal(2, VulkanStats.InlineUploads - inlineBefore);

            seam.BindFramebuffer(targetB);
            seam.DrawFullscreenTriangle();
            // Both draws, the batch and the inline copies are still one submission.
            Assert.Equal(0, VulkanStats.WaitCount(WaitSite.QueueSubmit) - submitsBefore);

            AssertEvery(Read(seam, targetA, size), 255, 0, 0, 255, "draw before the re-upload");
            AssertEvery(Read(seam, targetB, size), 0, 255, 0, 255, "draw after the re-upload");
            seam.Present();

            // The inline upload persists into later frames like any other.
            seam.BeginFrame();
            seam.BindFramebuffer(targetC);
            seam.UseProgram(program);
            seam.BindTexture(0, source);
            seam.BindSampler(0, sampler);
            seam.DrawFullscreenTriangle();
            AssertEvery(Read(seam, targetC, size), 0, 255, 0, 255, "next frame");
            seam.Present();

            Assert.Equal(0, VulkanStats.BlockingUploads - blockingBefore);
            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// The step-1 review's rule at ring level. An upload between frames opens a
    /// batch and reserves a Transfer value; a resource retired while it is open is
    /// keyed on that value and survives the next frame start, because no submission
    /// signalled it yet. The frame's submission carries the batch and signals it,
    /// and the retire queue then drains. An upload larger than the batch's staging
    /// region takes a dedicated staging buffer, counted, retired the same way. The
    /// staged texels read back exactly.
    /// </summary>
    [SkippableFact]
    public unsafe void AnUploadBetweenFramesRidesTheNextFrameAndItsTransferValueReleasesRetiredResources()
    {
        var messages = new List<string>();
        Skip.IfNot(GpuTest.TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var ring = new FrameRing(context!, framesInFlight: 2, uniformRingSize: 1 << 20, stagingPerSlot: 64 * 1024);
            using var textures = new TextureManager(context!, ring.Uploads);

            const uint smallSize = 8;
            const uint largeSize = 256;
            int small = textures.Create(smallSize, smallSize, Format.R8G8B8A8Unorm);
            int large = textures.Create(largeSize, largeSize, Format.R8G8B8A8Unorm);

            var smallPixels = new byte[smallSize * smallSize * 4];
            for (int i = 0; i < smallPixels.Length; i++) smallPixels[i] = (byte)(i * 13 % 251);
            var largePixels = new byte[largeSize * largeSize * 4];
            for (int i = 0; i < largePixels.Length; i++) largePixels[i] = (byte)(i * 7 % 253);

            long overflowsBefore = VulkanStats.StagingOverflows;
            fixed (byte* pixels = smallPixels) textures.Upload(small, 0, 0, 0, smallSize, smallSize, (IntPtr)pixels, 4);
            Assert.Equal(0, VulkanStats.StagingOverflows - overflowsBefore);
            fixed (byte* pixels = largePixels) textures.Upload(large, 0, 0, 0, largeSize, largeSize, (IntPtr)pixels, 4);
            Assert.Equal(1, VulkanStats.StagingOverflows - overflowsBefore);

            Assert.True(ring.Uploads.HasOpenBatch);
            ulong reserved = ring.Timeline.TransferRecorded;
            Assert.True(reserved > ring.Timeline.TransferSignalled, "the open batch has no unsignalled Transfer value");
            // The dedicated staging buffer, then one resource retired while the batch is open.
            Assert.Equal(1, ring.PendingDeletionCount);
            ring.DeferDeletion(new VulkanBuffer(context!, 16, BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit));

            ring.BeginFrame();
            Assert.Equal(2, ring.PendingDeletionCount);
            ring.EndFrame();
            Assert.Equal(reserved, ring.Timeline.TransferSignalled);
            Assert.False(ring.Uploads.HasOpenBatch);

            for (int frame = 0; frame < 3; frame++)
            {
                ring.BeginFrame();
                ring.EndFrame();
            }
            ring.Timeline.WaitForFrame(ring.Timeline.FrameSignalled, WaitSite.DeviceWaitIdle);
            ring.BeginFrame();
            Assert.Equal(0, ring.PendingDeletionCount);
            ring.EndFrame();

            Assert.Equal(smallPixels, ReadTexture(context!, ring, textures, small, smallSize));
            Assert.Equal(largePixels, ReadTexture(context!, ring, textures, large, largeSize));
            Assert.Equal(ring.Timeline.TransferRecorded, ring.Timeline.TransferSignalled);

            VulkanStats.WaitDeviceIdle(context!.Api, context.Device);
            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>Between frames: a copy appended to the open batch, submitted on its own, waited for.</summary>
    private static unsafe byte[] ReadTexture(VulkanContext context, FrameRing ring, TextureManager textures, int id, uint size)
    {
        VulkanTexture texture = textures.Get(id)!;
        ulong bytes = (ulong)size * size * 4;
        using var readback = new VulkanBuffer(context, bytes, BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        CommandBuffer commandBuffer = ring.Uploads.BeginRecording(inlineInFrame: false);
        try
        {
            textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(size, size, 1),
            };
            context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image, ImageLayout.TransferSrcOptimal,
                readback.Handle, 1, &region);
        }
        finally
        {
            ring.Uploads.EndRecording();
        }
        ring.Timeline.WaitForTransfer(ring.Uploads.SubmitStandalone(), WaitSite.Readback);

        var result = new byte[bytes];
        System.Runtime.InteropServices.Marshal.Copy(readback.Mapped, result, 0, result.Length);
        return result;
    }
}
