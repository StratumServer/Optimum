using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class SubmissionTests(ITestOutputHelper output)
{
    private const string Triangle = """
        #version 330 core
        void main() { gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2), -1 + ((gl_VertexID & 2) << 1), 0, 1); }
        """;
    private const string White = """
        #version 330 core
        out vec4 color;
        void main() { color = vec4(1); }
        """;
    private static readonly WaitSite[] NonblockingSites = {
        WaitSite.UploadSubmit, WaitSite.FlushFrame, WaitSite.DeviceWaitIdle,
        WaitSite.Readback, WaitSite.OcclusionQuery, WaitSite.SwapchainAcquire, WaitSite.Present,
    };
    private static long[] WaitCounts() => NonblockingSites.Select(VulkanStats.WaitCount).ToArray();

    private VulkanContext OpenContext(List<string> messages) => GpuTest.CreateContext(output, messages);
    private VulkanDevice OpenDevice() => GpuTest.CreateDevice(output);

    private sealed class Retired(Action destroy) : IDisposable
    {
        public void Dispose() => destroy();
    }

    [Fact]
    public void IndirectRegionsDoNotWrapOrGrowUntilTheirSlotIsRecycled()
    {
        var ring = new IndirectRing(2, minimumCapacity: 100);
        ring.BeginFrame(0, out _); ring.Attach(100);
        for (ulong i = 0; i < 5; i++)
        {
            Assert.True(ring.TryAllocate(20, out ulong offset));
            Assert.Equal(i * 20, offset);
        }
        for (int i = 0; i < 3; i++) Assert.False(ring.TryAllocate(20, out _));
        Assert.Equal(100UL, ring.CapacityOf(0));
        ring.BeginFrame(1, out _);
        Assert.Equal(100UL, ring.CursorOf(0));
        Assert.True(ring.NeedsBuffer(20, out ulong capacity));
        Assert.True(capacity >= 160);
        ring.Attach(capacity); Assert.True(ring.TryAllocate(20, out _));
        Assert.True(ring.BeginFrame(0, out capacity));
        ring.Attach(capacity);
        for (ulong i = 0; i < 8; i++)
        {
            Assert.True(ring.TryAllocate(20, out ulong offset));
            Assert.Equal(i * 20, offset);
        }
        Assert.Equal(20UL, ring.CursorOf(1));
        Assert.Equal(0, ring.OverflowsOf(0));
    }

    [SkippableFact]
    public unsafe void FrameSlotsKeepAlignedDisjointStorageUntilTheirLatestSubmissionCompletes()
    {
        var messages = new List<string>();
        using (var context = OpenContext(messages))
        using (var ring = new FrameRing(context, framesInFlight: 3, uniformRingSize: 65537))
        {
            var slots = new FrameSlot[3];
            var allocations = new RingAllocation[3];
            var stamps = new int[3];
            var submitted = new ulong[3];
            int retired = 0;
            for (int frame = 0; frame < 18; frame++)
            {
                int index = frame % 3;
                var slot = ring.BeginFrame();
                if (frame >= 3)
                {
                    Assert.Same(slots[index], slot);
                    Assert.True(ring.Timeline.FrameCompleted >= submitted[index]);
                }
                else slots[index] = slot;
                Assert.Equal(0UL, slot.UniformBytesUsed);
                Assert.True(slot.TryAllocateUniforms(100, out var allocation));
                Assert.Equal(0UL, allocation.Offset % Math.Max(1UL, context.Capabilities.MinUniformBufferOffsetAlignment));
                Assert.Equal(0UL, allocation.Offset % Math.Max(1UL, context.Capabilities.MinStorageBufferOffsetAlignment));
                Assert.Equal(ring.UniformBuffer.Handle, allocation.Buffer.Handle);
                allocations[index] = allocation;
                stamps[index] = frame + 1;
                *(int*)allocation.Pointer = stamps[index];
                for (int other = 0; other < 3; other++)
                    if (stamps[other] != 0)
                    {
                        Assert.Equal(stamps[other], *(int*)allocations[other].Pointer);
                        if (other != index) Assert.True(Math.Abs((long)allocation.Offset - allocations[other].Offset) >= 100);
                    }
                Assert.False(slot.TryAllocateUniforms((int)slot.UniformCapacity + 1, out _));
                ulong serial = slot.RecordingSerial;
                ulong partial = ring.SubmitPartial();
                Assert.Equal(partial, slot.LastSignalledValue);
                Assert.True(slot.FrameValue > partial);
                Assert.True(slot.RecordingSerial > serial);
                Assert.True(slot.TryAllocateUniforms(100, out var next));
                Assert.True(next.Offset >= allocation.Offset + 100);
                Assert.Equal(stamps[index], *(int*)allocation.Pointer);
                ulong retiringAt = slot.FrameValue;
                Parallel.For(0, 4, _ => ring.DeferDeletion(new Retired(() => {
                    Assert.True(ring.Timeline.FrameCompleted >= retiringAt);
                    retired++;
                })));
                submitted[index] = ring.EndFrame();
                Assert.Equal(submitted[index], slot.LastSignalledValue);
            }
            ring.Timeline.WaitForFrame(ring.Timeline.FrameSignalled, WaitSite.DeviceWaitIdle);
            ring.BeginFrame(); ring.EndFrame();
            Assert.Equal(18 * 4, retired);
            Assert.Equal(0, ring.PendingDeletionCount);
        }
        ValidationAssert.NoErrors(messages);
    }

    [SkippableFact]
    public unsafe void DescriptorCacheReusesBindingsAndEvictsReleasedLifetimesAcrossPools()
    {
        var messages = new List<string>();
        using (var context = OpenContext(messages))
        {
            var binding = new DescriptorSetLayoutBinding(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
            var layoutInfo = new DescriptorSetLayoutCreateInfo {
                SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding,
            };
            Assert.Equal(Result.Success, context.Api.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out var layout));
            var samplerInfo = new SamplerCreateInfo {
                SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Nearest, MinFilter = Filter.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
            };
            Sampler sampler = default;
            try
            {
                Assert.Equal(Result.Success, context.Api.CreateSampler(context.Device, &samplerInfo, null, out sampler));
                using var image = new VulkanImage(context, 4, 4, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit);
                using var cache = new DescriptorCache(context);
                DescriptorSetContents Contents(int program, ulong resource) => new(program, 0,
                    new[] { new SamplerBindingValue(0, image.View, sampler, resource) }, Array.Empty<BufferBindingValue>());
                var handles = new HashSet<ulong>();
                for (int i = 1; i <= 700; i++)
                {
                    var set = cache.Get(Contents(i, 10), layout);
                    Assert.True(handles.Add(set.Handle));
                    Assert.Equal(set.Handle, cache.Get(Contents(i, 10), layout).Handle);
                }
                Assert.Equal(700, cache.Count);
                Assert.Equal(700, cache.Hits); Assert.Equal(700, cache.Misses);
                // Same Vulkan handles with a new lifetime must not hit a stale set.
                var replacement = cache.Get(Contents(1, 11), layout);
                Assert.DoesNotContain(replacement.Handle, handles);
                cache.Release(10);
                using (var released = cache.CollectReleases()) Assert.NotNull(released);
                Assert.Equal(1, cache.Count);
                Assert.Equal(replacement.Handle, cache.Get(Contents(1, 11), layout).Handle);
                Assert.Null(cache.CollectReleases());
                cache.Release(11);
                using (var released = cache.CollectReleases()) Assert.NotNull(released);
                Assert.Equal(0, cache.Count);
            }
            finally
            {
                if (sampler.Handle != 0) context.Api.DestroySampler(context.Device, sampler, null);
                context.Api.DestroyDescriptorSetLayout(context.Device, layout, null);
            }
        }
        ValidationAssert.NoErrors(messages);
    }

    private static int Target(VulkanDevice device, int image = 0)
    {
        if (image == 0) image = device.CreateTexture2D(8, 8, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int target = device.CreateFramebuffer(8, 8);
        device.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, image, 0);
        device.SetDrawBuffers(target, 1);
        return target;
    }

    private static void Prepare(VulkanDevice device, int target, int program, int coverage)
    {
        device.BindFramebuffer(target); device.UseProgram(program);
        device.SetViewport(0, 0, coverage, coverage);
        device.SetDepthTest(false); device.SetCullFace(false);
        device.SetBlend(false, EnumBlendMode.Standard);
        device.SetColorMask(false, false, false, false);
    }

    private static void Samples(VulkanDevice device, int query, int expected)
    {
        Assert.True(device.IsQueryResultAvailable(query));
        int actual = device.GetQueryResult(query);
        if (device.PreciseOcclusionForTests) Assert.Equal(expected, actual);
        else Assert.InRange(actual, 1, int.MaxValue - 1);
    }

    [SkippableFact]
    public void QueryResultsSurviveSlotReuseWithoutExtraSubmissionsOrBlocking()
    {
        var device = OpenDevice();
        try
        {
            int target = Target(device), program = GpuTest.LinkProgram(device, Triangle, White, "submission-query");
            int[] queries = Enumerable.Range(0, 40).Select(_ => device.CreateOcclusionQuery()).ToArray();
            device.BeginFrame(); device.Present();
            long[] waits = WaitCounts();
            long submits = VulkanStats.WaitCount(WaitSite.QueueSubmit);
            int frames = 0;
            for (int round = 0; round < 4; round++)
            {
                device.BeginFrame();
                for (int i = 0; i < queries.Length; i++)
                {
                    Prepare(device, target, program, (i + round) % 8 + 1);
                    device.BeginOcclusionQuery(queries[i]); device.DrawFullscreenTriangle(); device.EndOcclusionQuery(queries[i]);
                    Assert.False(device.IsQueryResultAvailable(queries[i]));
                }
                device.Present(); frames++;
                // f+2 reuses f's slot and must have collected its query results.
                for (int age = 1; age <= 4; age++)
                {
                    device.BeginFrame();
                    if (age >= 2)
                        for (int i = 0; i < queries.Length; i++) Samples(device, queries[i], (int)Math.Pow((i + round) % 8 + 1, 2));
                    device.Present(); frames++;
                }
            }
            Assert.Equal(waits, WaitCounts());
            Assert.Equal(frames, VulkanStats.WaitCount(WaitSite.QueueSubmit) - submits);
            Assert.InRange(device.OcclusionQueryPoolsForTests, 2, 4);
            foreach (int query in queries) device.DeleteQuery(query);
            Assert.False(device.IsQueryResultAvailable(queries[0]));
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void QueryContinuesAcrossFramebufferChangesAndPartialReadback()
    {
        var device = OpenDevice();
        try
        {
            int targetA = Target(device), targetB = Target(device);
            int program = GpuTest.LinkProgram(device, Triangle, White, "submission-query-split");
            int[] probes = Enumerable.Range(0, 31).Select(_ => device.CreateOcclusionQuery()).ToArray();
            int spanning = device.CreateOcclusionQuery();
            device.BeginFrame();
            foreach (int probe in probes)
            {
                Prepare(device, targetA, program, 1);
                device.BeginOcclusionQuery(probe); device.DrawFullscreenTriangle(); device.EndOcclusionQuery(probe);
            }
            Prepare(device, targetA, program, 4);
            device.BeginOcclusionQuery(spanning); device.DrawFullscreenTriangle();
            device.BindFramebuffer(targetB); device.DrawFullscreenTriangle();
            uint pixel = 0;
            device.ReadDefaultFramebuffer(0, 0, 1, 1, (IntPtr)(&pixel));
            device.DrawFullscreenTriangle(); device.EndOcclusionQuery(spanning);
            device.Present();
            for (int i = 0; i < 4; i++) { device.BeginFrame(); device.Present(); }
            Samples(device, spanning, 48);
            foreach (int probe in probes) Samples(device, probe, 1);
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    private static byte[] Pixels(int size, byte red, byte green, byte blue)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = red; pixels[i + 1] = green; pixels[i + 2] = blue; pixels[i + 3] = 255; }
        return pixels;
    }

    private static unsafe void Upload(VulkanDevice device, int texture, byte[] pixels)
    {
        fixed (byte* pointer = pixels)
            device.UploadTexture2D(texture, 0, 0, 0, 8, 8, EnumTexturePixelFormat.Rgba, (IntPtr)pointer);
    }

    private static unsafe byte[] Read(VulkanDevice device, int target)
    {
        var pixels = new byte[8 * 8 * 4];
        device.BindFramebuffer(target);
        fixed (byte* pointer = pixels) device.ReadDefaultFramebuffer(0, 0, 8, 8, (IntPtr)pointer);
        return pixels;
    }

    [SkippableFact]
    public unsafe void WorkerUploadsAndGeneratedMipsReachTheNextFrameWithoutUploadWaits()
    {
        var device = OpenDevice();
        try
        {
            int[] textures = Enumerable.Range(0, 4).Select(_ => device.CreateTexture2D(8, 8,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false)).ToArray();
            int[] targets = textures.Select(texture => Target(device, texture)).ToArray();
            int renderTarget = Target(device), renderProgram = GpuTest.LinkProgram(device, Triangle, White, "upload-concurrent-render");
            int mip = device.CreateTexture2D(8, 8, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, true);
            device.BeginFrame(); device.Present();
            long[] waits = WaitCounts();
            long blocking = VulkanStats.BlockingUploads, uploads = VulkanStats.UploadRequests;
            for (int frame = 0; frame < 24; frame++)
            {
                int current = frame;
                device.BeginFrame();
                var worker = Task.Run(() => {
                    for (int i = 0; i < textures.Length; i++)
                        Upload(device, textures[i], Pixels(8, (byte)(current * 9), (byte)(i * 41), 173));
                });
                try
                {
                    Upload(device, mip, Pixels(8, (byte)(frame * 9), 71, 163));
                    device.GenerateMipmaps(mip);
                    // Record rendering while a worker independently fills the upload batch.
                    Prepare(device, renderTarget, renderProgram, 8); device.SetColorMask(true, true, true, true);
                    device.DrawFullscreenTriangle();
                }
                finally { worker.GetAwaiter().GetResult(); }
                device.Present();
            }
            Assert.Equal(waits, WaitCounts());
            Assert.Equal(blocking, VulkanStats.BlockingUploads);
            Assert.True(VulkanStats.UploadRequests - uploads >= 24 * 6);
            // The next submission carries any remaining batch before frame N+2 reads it.
            device.BeginFrame(); device.Present();
            Assert.Equal(device.TimelineForTests.TransferRecorded, device.TimelineForTests.TransferSignalled);
            device.BeginFrame();
            for (int i = 0; i < textures.Length; i++)
                Assert.Equal(Pixels(8, 207, (byte)(i * 41), 173), Read(device, targets[i]));
            Assert.Equal(Pixels(1, 207, 71, 163), device.ReadBackLevelForTests(mip, 3));
            Assert.Equal(Pixels(8, 255, 255, 255), Read(device, renderTarget));
            device.Present();
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public void ReuploadAndMipGenerationOnlyChangeDrawsRecordedAfterThem()
    {
        var device = OpenDevice();
        try
        {
            int source = device.CreateTexture2D(8, 8, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, true);
            int program = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                uniform sampler2D source;
                out vec4 color;
                void main() { color = textureLod(source, vec2(0.5), 3.0); }
                """, "submission-reupload");
            int sampler = device.CreateSampler(false);
            int before = Target(device), after = Target(device), nextFrame = Target(device);
            device.BeginFrame(); device.Present();
            long submits = VulkanStats.WaitCount(WaitSite.QueueSubmit), blocking = VulkanStats.BlockingUploads;
            device.BeginFrame();
            Upload(device, source, Pixels(8, 255, 0, 0)); device.GenerateMipmaps(source);
            Prepare(device, before, program, 8); device.SetColorMask(true, true, true, true);
            device.SetSamplerUnit(program, "source", 0); device.BindTexture(0, source); device.BindSampler(0, sampler);
            device.DrawFullscreenTriangle();
            Upload(device, source, Pixels(8, 0, 255, 0)); device.GenerateMipmaps(source);
            device.BindFramebuffer(after); device.DrawFullscreenTriangle();
            Assert.Equal(submits, VulkanStats.WaitCount(WaitSite.QueueSubmit));
            Assert.Equal(Pixels(8, 255, 0, 0), Read(device, before));
            Assert.Equal(Pixels(8, 0, 255, 0), Read(device, after));
            device.Present();
            device.BeginFrame();
            Prepare(device, nextFrame, program, 8); device.SetColorMask(true, true, true, true);
            device.BindTexture(0, source); device.BindSampler(0, sampler); device.DrawFullscreenTriangle();
            Assert.Equal(Pixels(8, 0, 255, 0), Read(device, nextFrame));
            device.Present();
            Assert.Equal(blocking, VulkanStats.BlockingUploads);
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void OpenTransferBatchRetainsOverflowStagingUntilSubmissionCompletes()
    {
        var messages = new List<string>();
        using (var context = OpenContext(messages))
        using (var ring = new FrameRing(context, framesInFlight: 2, uniformRingSize: 65536, stagingPerSlot: 65536))
        using (var textures = new TextureManager(context, ring.Uploads))
        {
            var sizes = new uint[] { 8, 256 };
            var images = sizes.Select(size => textures.Create(size, size, Format.R8G8B8A8Unorm)).ToArray();
            var expected = sizes.Select(size => Enumerable.Range(0, (int)(size * size * 4)).Select(i => (byte)(i * 13 % 251)).ToArray()).ToArray();
            long overflows = VulkanStats.StagingOverflows;
            for (int i = 0; i < images.Length; i++)
                fixed (byte* pointer = expected[i]) textures.Upload(images[i], 0, 0, 0, sizes[i], sizes[i], (IntPtr)pointer, 4);
            Assert.Equal(overflows + 1, VulkanStats.StagingOverflows);
            ulong transfer = ring.Timeline.TransferRecorded;
            Assert.True(transfer > ring.Timeline.TransferSignalled);
            bool retired = false;
            ring.DeferDeletion(new Retired(() => {
                Assert.True(ring.Timeline.TransferCompleted >= transfer);
                retired = true;
            }));
            ring.BeginFrame();
            Assert.False(retired);
            Assert.True(ring.PendingDeletionCount >= 2);
            ring.EndFrame();
            Assert.Equal(transfer, ring.Timeline.TransferSignalled);
            ring.Timeline.WaitForFrame(ring.Timeline.FrameSignalled, WaitSite.Readback);
            ring.BeginFrame();
            Assert.True(retired); Assert.Equal(0, ring.PendingDeletionCount);
            ring.EndFrame();
            for (int i = 0; i < images.Length; i++)
            {
                using var readback = new VulkanBuffer(context, (ulong)expected[i].Length, BufferUsageFlags.TransferDstBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
                var commands = ring.Uploads.BeginRecording(inlineInFrame: false);
                try
                {
                    var image = textures.Get(images[i])!;
                    textures.TransitionTexture(commands, image, ImageLayout.TransferSrcOptimal);
                    var region = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                        ImageExtent = new Extent3D(sizes[i], sizes[i], 1) };
                    context.Api.CmdCopyImageToBuffer(commands, image.Image, ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
                }
                finally { ring.Uploads.EndRecording(); }
                ring.Timeline.WaitForTransfer(ring.Uploads.SubmitStandalone(), WaitSite.Readback);
                Assert.True(new ReadOnlySpan<byte>((void*)readback.Mapped, expected[i].Length).SequenceEqual(expected[i]));
            }
        }
        ValidationAssert.NoErrors(messages);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void DynamicStateReusePreservesPixelsAcrossPartialSubmissions(bool cached)
    {
        var device = OpenDevice();
        try
        {
            device.DynamicStateMaskingForTests = cached;
            int target = Target(device);
            int red = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                out vec4 color;
                void main() { color = vec4(1, 0, 0, 1); }
                """, "state-red");
            int green = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                out vec4 color;
                void main() { color = vec4(0, 1, 0, 1); }
                """, "state-green");
            int complete = device.DynamicStateCommandsPerDrawForTests;
            for (int frame = 0; frame < 3; frame++)
            {
                device.BeginFrame();
                Prepare(device, target, red, 8); device.SetColorMask(true, true, true, true);
                device.SetScissorEnabled(false); device.ClearColor(0, 0, 0, 0, 1);
                device.SetViewport(0, 0, 4, 8);
                long commands = device.DynamicStateCommandsForTests;
                for (int i = 0; i < 32; i++) device.DrawFullscreenTriangle();
                Assert.Equal((cached ? 1 : 32) * complete, device.DynamicStateCommandsForTests - commands);
                device.UseProgram(green); device.SetViewport(0, 0, 8, 8);
                device.SetScissorEnabled(true); device.SetScissor(4, 4, 4, 4);
                device.DrawFullscreenTriangle();
                byte[] pixels = Read(device, target);
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int at = (y * 8 + x) * 4;
                        Assert.Equal(x < 4 ? 255 : 0, pixels[at]);
                        Assert.Equal(x >= 4 && y >= 4 ? 255 : 0, pixels[at + 1]);
                        Assert.Equal(0, pixels[at + 2]); Assert.Equal(255, pixels[at + 3]);
                    }
                commands = device.DynamicStateCommandsForTests;
                device.DrawFullscreenTriangle(); // Same state, new command buffer after readback.
                Assert.Equal(complete, device.DynamicStateCommandsForTests - commands);
                device.Present();
            }
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public void IndirectOverflowAndLaterGrowthPreserveEveryDrawsRegion()
    {
        var device = OpenDevice();
        try
        {
            device.IndirectMinimumCapacityForTests = 40; // Two indexed commands fit initially.
            var positions = new float[48]; var indices = new int[24];
            for (int strip = 0; strip < 4; strip++)
            {
                float left = -1 + strip * 0.5f, right = left + 0.5f;
                new[] { left, -1f, 0f, right, -1f, 0f, right, 1f, 0f, left, 1f, 0f }.CopyTo(positions, strip * 12);
                new[] { 0, 1, 2, 0, 2, 3 }.Select(index => index + strip * 4).ToArray().CopyTo(indices, strip * 6);
            }
            int mesh = device.CreateMesh(new MeshData(16, 24) { xyz = positions, Indices = indices, VerticesCount = 16, IndicesCount = 24 }, false);
            int program = GpuTest.LinkProgram(device, """
                #version 330 core
                layout(location = 0) in vec3 position;
                void main() { gl_Position = vec4(position, 1); }
                """, """
                #version 330 core
                uniform float tint;
                out vec4 color;
                void main() { color = vec4(tint, 1, 1, 1); }
                """, "indirect-region-lifetime");
            int tint = device.GetUniformLocation(program, "tint");
            int[] targets = Enumerable.Range(0, 3).Select(_ => Target(device)).ToArray();
            for (int frame = 0; frame < 3; frame++)
            {
                device.BeginFrame(); Prepare(device, targets[frame], program, 8);
                device.SetColorMask(true, true, true, true); device.ClearColor(0, 0, 0, 0, 1);
                device.SetUniform(program, tint, (30 + frame * 60) / 255f);
                for (int strip = 0; strip < 4; strip++)
                    device.DrawMeshMulti(mesh, new[] { strip * 6 * sizeof(int), 0 }, new[] { 6 }, 1, false);
                Assert.Equal(2, device.IndirectOverflowsForTests);
                if (frame == 0) Assert.Equal(40UL, device.IndirectRingForTests.CapacityOf(device.CurrentSlotForTests));
                if (frame == 2) Assert.True(device.IndirectGrowthsForTests > 0);
                device.Present();
            }
            // Read only after slot reuse; early readbacks would hide lifetime mistakes.
            device.BeginFrame();
            for (int frame = 0; frame < targets.Length; frame++)
                Assert.Equal(Pixels(8, (byte)(30 + frame * 60), 255, 255), Read(device, targets[frame]));
            device.Present(); device.DeleteMesh(mesh);
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }
}
