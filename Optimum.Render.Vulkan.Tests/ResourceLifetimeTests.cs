using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class ResourceLifetimeTests(ITestOutputHelper output)
{
    private const string Triangle = """
        #version 330 core
        void main() {
            gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2), -1 + ((gl_VertexID & 2) << 1), 0, 1);
        }
        """;

    private VulkanDevice Open(bool aliasing = false) =>
        GpuTest.CreateDevice(output, device => device.TransientAliasingOverride = aliasing);

    private static int Attach(VulkanDevice device, int texture, int width, int height)
    {
        int target = device.CreateFramebuffer(width, height);
        device.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        device.SetDrawBuffers(target, 1);
        return target;
    }

    private static void Draw(VulkanDevice device, int framebuffer, int program, int width, int height)
    {
        device.BindFramebuffer(framebuffer);
        device.SetViewport(0, 0, width, height);
        device.SetDepthTest(false);
        device.SetCullFace(false);
        device.SetBlend(false, EnumBlendMode.Standard);
        device.UseProgram(program);
        device.DrawFullscreenTriangle();
    }

    private static unsafe byte[] Read(VulkanDevice device, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        fixed (byte* pointer = pixels)
            device.ReadDefaultFramebuffer(0, 0, width, height, (IntPtr)pointer);
        return pixels;
    }

    [Fact]
    public void ResourceAgeWindowExpiresWithoutAffectingPermanentBindings()
    {
        var age = new ResourceAge(2);
        age.NoteFrame(100); age.NoteFrame(200); age.NoteFrame(300);
        Assert.False(age.IsShortLived(0)); Assert.False(age.IsShortLived(200));
        Assert.True(age.IsShortLived(201));
        var permanent = new BufferBindingValue(0, new Silk.NET.Vulkan.Buffer(1), 0, 64);
        var recent = new SamplerBindingValue(0, new ImageView(1), new Sampler(2), Resource: 201);
        var contents = new DescriptorSetContents(1, 0, new[] { recent }, new[] { permanent });
        Assert.True(age.NamesShortLived(contents));
        age.NoteFrame(400);
        Assert.False(age.NamesShortLived(contents));
        age.ShortLivedFrames = 0;
        Assert.False(age.IsShortLived(401));
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReusedPostTargetsContainTheCurrentFrame(bool aliasing, bool frameGraph)
    {
        using var device = Open(aliasing);
        device.FrameGraphForTests.Enabled = frameGraph;
        int fill = GpuTest.LinkProgram(device, Triangle, """
            #version 330 core
            uniform float frameBlue;
            out vec4 color;
            void main() { color = vec4(floor(gl_FragCoord.xy) / 16.0, frameBlue, 1); }
            """, "reuse-fill");
        int rotate = GpuTest.LinkProgram(device, Triangle, """
            #version 330 core
            uniform sampler2D source;
            out vec4 color;
            void main() {
                vec3 pixel = texelFetch(source, ivec2(gl_FragCoord.xy), 0).rgb;
                color = vec4(pixel.g, pixel.b, pixel.r * 0.5 + 0.25, 1);
            }
            """, "reuse-transform");
        device.SetSamplerUnit(rotate, "source", 0);
        int blue = device.GetUniformLocation(fill, "frameBlue");
        int[] textures = new int[4], targets = new int[4];
        for (int i = 0; i < 4; i++)
        {
            textures[i] = i < 3
                ? device.CreateTransientTexture2D(16, 16, EnumTextureInternalFormat.Rgba8, 2 + i)
                : device.CreateTexture2D(16, 16, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            targets[i] = Attach(device, textures[i], 16, 16);
        }
        byte[] pixels = Array.Empty<byte>();
        for (int frame = 0; frame < 5; frame++)
        {
            device.BeginFrame();
            for (int i = 0; i < 3; i++) device.BindTransientForFrame(textures[i], i, i + 1);
            Assert.Equal(aliasing ? 1 : 0, device.Transients.AliasedLeaseCount);
            device.SetUniform(fill, blue, (frame + 1) / 16f);
            for (int pass = 0; pass < 4; pass++)
            {
                device.BindTexture(0, pass == 0 ? 0 : textures[pass - 1]);
                Draw(device, targets[pass], pass == 0 ? fill : rotate, 16, 16);
            }
            device.BindTexture(0, 0);
            if (frame == 4) pixels = Read(device, 16, 16);
            device.Present();
        }
        // Three channel rotations leave each original channel halved plus 0.25.
        // Allow two UNORM8 rounding steps; expected values come from the scene inputs.
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int index = (y * 16 + x) * 4;
                Assert.InRange((int)pixels[index], (int)Math.Round((x / 32.0 + .25) * 255) - 2, (int)Math.Round((x / 32.0 + .25) * 255) + 2);
                Assert.InRange((int)pixels[index + 1], (int)Math.Round((y / 32.0 + .25) * 255) - 2, (int)Math.Round((y / 32.0 + .25) * 255) + 2);
                Assert.InRange((int)pixels[index + 2], 102, 106);
                Assert.Equal(255, pixels[index + 3]);
            }
        if (aliasing) Assert.Equal(2, device.Transients.PhysicalImageCount);
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void SamplingTheTargetUsesAFreshSnapshotForEveryDraw()
    {
        using var device = Open();
        int swap = GpuTest.LinkProgram(device, Triangle, """
            #version 330 core
            uniform sampler2D source;
            out vec4 color;
            void main() { color = texelFetch(source, ivec2(gl_FragCoord.x < 1.0 ? 1 : 0, 0), 0); }
            """, "feedback-swap");
        byte[] original = { 255, 0, 0, 255, 0, 255, 0, 255 };
        int texture;
        fixed (byte* pixels = original)
            texture = device.CreateTexture2D(2, 1, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)pixels, false);
        int target = Attach(device, texture, 2, 1);
        device.SetSamplerUnit(swap, "source", 0);
        for (int frame = 0; frame < 6; frame++)
        {
            device.BeginFrame();
            device.BindTexture(0, texture);
            Draw(device, target, swap, 2, 1);
            Draw(device, target, swap, 2, 1);
            Assert.Equal(original, Read(device, 2, 1));
            device.Present();
        }
        Assert.InRange(device.ReadSelfCopiesForTests.Created, 1, 3);
        GpuTest.AssertClean(device);
    }

    private sealed class Clock : ITimelineClock
    {
        public ulong FrameRecorded { get; set; }
        public ulong TransferRecorded { get; set; }
        public ulong FrameCompleted { get; set; }
        public ulong TransferCompleted { get; set; }
    }

    [Fact]
    public void FeedbackCopiesWaitForCompletionBeforeReuseOrRetirement()
    {
        var clock = new Clock { FrameRecorded = 7 };
        int next = 0;
        var destroyed = new List<int>();
        var pool = new FeedbackCopyPool(clock, _ => ++next, destroyed.Add, idleFrames: 2);
        var shape = new FeedbackCopyDesc(16, 16, Format.R8G8B8A8Unorm, 1, 1, false);
        int first = pool.Acquire(shape);
        pool.Release(first); pool.EndFrame(); pool.Collect();
        int second = pool.Acquire(shape);
        Assert.NotEqual(first, second);
        Assert.Empty(destroyed);
        clock.FrameCompleted = 7; pool.Collect();
        Assert.Equal(first, pool.Acquire(shape));
        pool.Release(first); pool.Release(second); pool.EndFrame();
        for (int i = 0; i < 5; i++) pool.Collect();
        Assert.Equal(0, pool.Live);
        Assert.Contains(first, destroyed);
        Assert.Contains(second, destroyed);
    }

    [SkippableFact]
    public void DeletingASharedAttachmentTwiceRetiresItOnce()
    {
        using var device = Open();
        int image = device.CreateTexture2D(4, 4, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int before = device.TexturesForTests.Count;
        device.DeleteTexture(image);
        Assert.Equal(before - 1, device.TexturesForTests.Count);
        device.DeleteTexture(image);
        Assert.Equal(before - 1, device.TexturesForTests.Count);
        Assert.Null(device.TexturesForTests.Get(image));
        device.BeginFrame();
        device.Present();
        GpuTest.AssertClean(device);
    }
    private sealed class Retirement(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Fact]
    public void RetirementSnapshotsBothTimelinesAndDoesNotBlockReadyFollowers()
    {
        var clock = new Clock { FrameRecorded = 9, TransferRecorded = 3 };
        var queue = new RetireQueue(clock);
        var destroyed = new List<int>();
        queue.Retire(new Retirement(() => destroyed.Add(1)));
        clock.FrameRecorded = 4; clock.TransferRecorded = 8;
        queue.Retire(new Retirement(() => destroyed.Add(2)));
        clock.FrameRecorded = 100; clock.TransferRecorded = 100;
        clock.FrameCompleted = 4; clock.TransferCompleted = 7;
        Assert.Equal(0, queue.Collect());
        clock.TransferCompleted = 8;
        Assert.Equal(1, queue.Collect());
        Assert.Equal(new[] { 2 }, destroyed);
        clock.FrameCompleted = 9;
        Assert.Equal(1, queue.Collect());
        Assert.Equal(new[] { 2, 1 }, destroyed);
        Assert.Equal(0, queue.Collect());
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void ConcurrentRetirementAndReentrantDisposalDoNotLoseResources()
    {
        var clock = new Clock { FrameRecorded = 3, TransferRecorded = 5 };
        var queue = new RetireQueue(clock);
        var destroyed = new int[128];
        Parallel.For(0, destroyed.Length, i => queue.Retire(new Retirement(() => destroyed[i]++)));
        Assert.Equal(destroyed.Length, queue.PendingCount);
        Assert.Equal(0, queue.Collect());
        clock.FrameCompleted = 3; clock.TransferCompleted = 5;
        Assert.Equal(destroyed.Length, queue.Collect());
        Assert.All(destroyed, count => Assert.Equal(1, count));
        int nested = 0;
        queue.Retire(new Retirement(() => queue.Retire(new Retirement(() => nested++))));
        Assert.Equal(1, queue.Collect());
        Assert.Equal(0, nested);
        Assert.Equal(1, queue.Collect());
        Assert.Equal(1, nested);
        queue.DisposeAll();
        Assert.Equal(1, nested);
    }

    [Fact]
    public void RecycledVulkanHandlesDoNotAliasDescriptorCacheEntries()
    {
        DescriptorSetContents Entry(ulong lifetime) => new(1, 1,
            new[] { new SamplerBindingValue(0, new ImageView(123), new Sampler(456), Resource: lifetime) },
            new[] { new BufferBindingValue(1, new Silk.NET.Vulkan.Buffer(789), 0, 256, Resource: lifetime) });
        var cache = new Dictionary<DescriptorSetContents, string>();
        cache.Add(Entry(10), "original");
        cache.Add(Entry(11), "replacement");
        Assert.Equal("original", cache[Entry(10)]);
        Assert.Equal("replacement", cache[Entry(11)]);
    }

    [SkippableFact]
    public unsafe void UploadedTexturesAndMeshesSurviveDeletionUntilTheirDrawCompletes()
    {
        var device = Open();
        try
        {
            int program = GpuTest.LinkProgram(device, """
                #version 330 core
                layout(location=0) in vec3 position;
                void main() { gl_Position = vec4(position, 1); }
                """, """
                #version 330 core
                uniform sampler2D image;
                out vec4 color;
                void main() { color = texelFetch(image, ivec2(0), 0); }
                """, "retirement-churn");
            device.SetSamplerUnit(program, "image", 0);
            int targetTexture = device.CreateTexture2D(4, 4, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int target = Attach(device, targetTexture, 4, 4);
            for (int frame = 0; frame < 48; frame++)
            {
                device.BeginFrame();
                byte[] expected = { (byte)(frame * 5), 77, 151, 255 };
                int texture;
                fixed (byte* pointer = expected)
                    texture = device.CreateTexture2D(1, 1, EnumTextureInternalFormat.Rgba8,
                        EnumTexturePixelFormat.Rgba, (IntPtr)pointer, false);
                int mesh = device.CreateMesh(new MeshData(3, 3)
                {
                    xyz = new[] { -1f, -1f, 0f, 3f, -1f, 0f, -1f, 3f, 0f },
                    VerticesCount = 3, Indices = new[] { 0, 1, 2 }, IndicesCount = 3,
                    mode = EnumDrawMode.Triangles,
                }, true);
                device.BindFramebuffer(target);
                device.SetViewport(0, 0, 4, 4);
                device.SetDepthTest(false); device.SetCullFace(false);
                device.SetBlend(false, EnumBlendMode.Standard);
                device.UseProgram(program); device.BindTexture(0, texture);
                device.DrawMesh(mesh);
                device.DeleteMesh(mesh); device.DeleteTexture(texture);
                // Deletion precedes submission; readback must still see this frame's upload.
                if (frame % 6 == 5)
                {
                    byte[] actual = Read(device, 4, 4);
                    for (int i = 0; i < actual.Length; i++) Assert.Equal(expected[i % 4], actual[i]);
                }
                device.Present();
            }
            device.DeleteFramebuffer(target); device.DeleteTexture(targetTexture);
            GpuTest.AssertClean(device);
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }


    private const ulong MiB = 1024 * 1024;
    private const MemoryPropertyFlags HostMemory = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

    private VulkanContext AllocationContext(List<string> messages) => GpuTest.CreateContext(output, messages);

    [SkippableFact]
    public unsafe void PooledAllocationsRemainIsolatedThroughChurnAndReuse()
    {
        var messages = new List<string>();
        using (var context = AllocationContext(messages))
        {
            var live = new List<(VulkanBuffer Buffer, int Stamp)>();
            int serial = 0;
            try
            {
                for (int round = 0; round < 6; round++)
                {
                    while (live.Count < 2048)
                    {
                        int stamp = ++serial;
                        // Different sizes and lifetimes exercise splitting and merging holes.
                        ulong size = (ulong)(1 + stamp % 16) * 4096;
                        var buffer = new VulkanBuffer(context, size, BufferUsageFlags.VertexBufferBit, HostMemory);
                        live.Add((buffer, stamp));
                        new Span<int>((void*)buffer.Mapped, (int)size / sizeof(int)).Fill(stamp);
                    }
                    foreach (var group in live.GroupBy(entry => entry.Buffer.Allocation.Memory.Handle))
                    {
                        ulong end = 0;
                        foreach (var entry in group.OrderBy(entry => entry.Buffer.Allocation.Offset))
                        {
                            var allocation = entry.Buffer.Allocation;
                            Assert.True(allocation.Offset >= end, "Live allocations overlap.");
                            end = allocation.Offset + allocation.Size;
                        }
                    }
                    foreach (var entry in live)
                    {
                        var words = new ReadOnlySpan<int>((void*)entry.Buffer.Mapped, (int)entry.Buffer.Size / sizeof(int));
                        Assert.Equal(-1, words.IndexOfAnyExcept(entry.Stamp));
                    }
                    Assert.InRange(context.Allocator.BlockCount, 1, 16);
                    for (int i = live.Count - 1; i >= 0; i--)
                        if ((i + round) % 3 != 0) { live[i].Buffer.Dispose(); live.RemoveAt(i); }
                }
                using var commands = new SetupQueue(context);
                using var textures = new TextureManager(context, commands.Uploads);
                int image = textures.Create(64, 64, Format.R8G8B8A8Unorm);
                Assert.DoesNotContain(live, entry => entry.Buffer.Allocation.Memory.Handle == textures.Get(image)!.Allocation.Memory.Handle);
            }
            finally { foreach (var entry in live) entry.Buffer.Dispose(); }
            // Repeated complete release must reuse blocks instead of growing indefinitely.
            int blocks = context.Allocator.BlockCount;
            for (int round = 0; round < 3; round++)
            {
                var batch = new List<VulkanBuffer>();
                try
                {
                    for (int i = 0; i < 200; i++)
                        batch.Add(new VulkanBuffer(context, 128 * 1024, BufferUsageFlags.VertexBufferBit, HostMemory));
                    Assert.InRange(context.Allocator.BlockCount, 1, blocks);
                }
                finally { foreach (var buffer in batch) buffer.Dispose(); }
            }
        }
        ValidationAssert.NoErrors(messages);
    }

    [SkippableFact]
    public unsafe void DedicatedAllocationsAndEmptyPoolTrimmingRespectTheirBoundaries()
    {
        var messages = new List<string>();
        using (var context = AllocationContext(messages))
        {
            var allocator = context.Allocator;
            int baseline = allocator.BlockCount;
            var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = 4096,
                Usage = BufferUsageFlags.VertexBufferBit, SharingMode = SharingMode.Exclusive };
            Assert.Equal(Result.Success, context.Api.CreateBuffer(context.Device, &info, null, out var handle));
            MemoryAllocation dedicated = default;
            try
            {
                var requirements = VulkanAllocator.BufferRequirements(context, handle, out _);
                dedicated = allocator.Allocate(requirements, HostMemory, true, "dedicated acceptance",
                    MemoryPoolClass.DeviceBuffers, true, handle, default);
                Assert.True(dedicated.Block!.Dedicated);
                Assert.Equal(requirements.Size, dedicated.Block.Size);
                Assert.Equal(Result.Success, context.Api.BindBufferMemory(context.Device, handle, dedicated.Memory, dedicated.Offset));
                *(int*)dedicated.Mapped = 7919;
                Assert.Equal(7919, *(int*)dedicated.Mapped);
            }
            finally
            {
                context.Api.DestroyBuffer(context.Device, handle, null);
                if (dedicated.Block != null) allocator.Free(dedicated);
            }
            Assert.Equal(baseline, allocator.BlockCount);
            foreach (var pool in new[] { MemoryPoolClass.DeviceBuffers, MemoryPoolClass.Staging })
            {
                ulong threshold = VulkanAllocator.BlockSizeOf(pool) / 4;
                using var below = new VulkanBuffer(context, threshold / 2, BufferUsageFlags.TransferSrcBit, HostMemory, pool);
                using var at = new VulkanBuffer(context, threshold, BufferUsageFlags.TransferSrcBit, HostMemory, pool);
                VulkanAllocator.BufferRequirements(context, below.Handle, out bool driverDedicated);
                Assert.Equal(driverDedicated, below.Allocation.Block!.Dedicated);
                Assert.True(at.Allocation.Block!.Dedicated);
            }
            allocator.HeapBudgetOverrideForTests = 1;
            allocator.AdvanceFrame();
            Assert.Equal(baseline, allocator.BlockCount);
            allocator.HeapBudgetOverrideForTests = null;
            var first = new VulkanBuffer(context, MiB, BufferUsageFlags.VertexBufferBit, HostMemory);
            Assert.False(first.Allocation.Block!.Dedicated);
            var snapshot = allocator.Snapshot();
            context.Api.GetPhysicalDeviceMemoryProperties(context.PhysicalDevice, out var memory);
            Assert.Equal((int)memory.MemoryHeapCount, snapshot.HeapUsed.Length);
            Assert.Equal((int)memory.MemoryHeapCount, snapshot.HeapBudget.Length);
            Assert.True(snapshot.HeapUsed[first.Allocation.Block.HeapIndex] >= first.Allocation.Block.Size);
            Assert.True(snapshot.ClassBytes[(int)MemoryPoolClass.DeviceBuffers] >= first.Allocation.Block.Size);
            for (int heap = 0; heap < memory.MemoryHeapCount; heap++)
            {
                Assert.True(snapshot.HeapBudget[heap] > 0);
                if (!allocator.BudgetExtension)
                    Assert.Equal((ulong)(memory.MemoryHeaps[heap].Size * VulkanAllocator.FallbackBudgetShare), snapshot.HeapBudget[heap]);
            }
            first.Dispose();
            for (int i = 0; i < 60; i++) allocator.AdvanceFrame();
            using (var refill = new VulkanBuffer(context, MiB, BufferUsageFlags.VertexBufferBit, HostMemory))
                Assert.Equal(baseline + 1, allocator.BlockCount);
            long freed = allocator.Snapshot().EmptyBlocksFreed;
            for (int i = 0; i < VulkanAllocator.EmptyBlockFrames - 1; i++) allocator.AdvanceFrame();
            Assert.Equal(baseline + 1, allocator.BlockCount);
            allocator.AdvanceFrame();
            Assert.Equal(baseline, allocator.BlockCount);
            Assert.Equal(freed + 1, allocator.Snapshot().EmptyBlocksFreed);
            using (var pressured = new VulkanBuffer(context, MiB, BufferUsageFlags.VertexBufferBit, HostMemory)) { }
            allocator.HeapBudgetOverrideForTests = 1;
            allocator.AdvanceFrame();
            Assert.Equal(baseline, allocator.BlockCount);
        }
        ValidationAssert.NoErrors(messages);
    }

    [SkippableFact]
    public unsafe void ReBarBudgetFallsBackToMappedStagingMemory()
    {
        var messages = new List<string>();
        using (var context = AllocationContext(messages))
        {
            var allocator = context.Allocator;
            allocator.ReBarCapOverrideForTests = 16 * MiB;
            var logged = new List<string>();
            allocator.Log = logged.Add;
            long misses = allocator.ReBarMisses, stats = VulkanStats.RebarFallbacks;
            var buffers = new List<VulkanBuffer>();
            try
            {
                for (int i = 0; i < 40; i++)
                {
                    var buffer = new VulkanBuffer(context, MiB, BufferUsageFlags.UniformBufferBit,
                        HostMemory | MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.ReBar);
                    buffers.Add(buffer);
                    *(int*)buffer.Mapped = i + 1;
                    Assert.Contains(buffer.Allocation.Block!.Class, new[] { MemoryPoolClass.ReBar, MemoryPoolClass.Staging });
                }
                Assert.True(allocator.ReBarUsed <= 16 * MiB);
                Assert.True(allocator.ReBarMisses > misses);
                Assert.True(VulkanStats.RebarFallbacks - stats >= allocator.ReBarMisses - misses);
                Assert.Contains(logged, line => line.Contains("ReBAR miss"));
                Assert.Contains(buffers, buffer => buffer.Allocation.Block!.Class == MemoryPoolClass.Staging);
                if (allocator.HasMemoryType(uint.MaxValue, HostMemory | MemoryPropertyFlags.DeviceLocalBit, 0))
                    Assert.Contains(buffers, buffer => buffer.Allocation.Block!.Class == MemoryPoolClass.ReBar);
                for (int i = 0; i < buffers.Count; i++) Assert.Equal(i + 1, *(int*)buffers[i].Mapped);
            }
            finally { foreach (var buffer in buffers) buffer.Dispose(); }
        }
        ValidationAssert.NoErrors(messages);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void MeshAllocationPolicyStillProducesCorrectPixels(bool staticMesh)
    {
        var device = Open();
        try
        {
            var allocator = device.ContextForTests.Allocator;
            allocator.ReBarCapOverrideForTests = 0;
            long misses = allocator.ReBarMisses;
            int texture = device.CreateTexture2D(8, 8, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int target = Attach(device, texture, 8, 8);
            int program = GpuTest.LinkProgram(device, """
                #version 330 core
                layout(location = 0) in vec3 position;
                void main() { gl_Position = vec4(position, 1); }
                """, """
                #version 330 core
                out vec4 color;
                void main() { color = vec4(1); }
                """, "allocation-policy");
            int mesh = device.CreateMesh(new MeshData(4, 6) {
                xyz = new[] { -1f, -1f, 0f, 0f, -1f, 0f, 0f, 1f, 0f, -1f, 1f, 0f },
                VerticesCount = 4, Indices = new[] { 0, 1, 2, 0, 2, 3 }, IndicesCount = 6,
            }, staticMesh);
            foreach (int slot in new[] { MeshManager.BufferXyz, -1 })
            {
                var buffer = device.MeshesForTests.BufferOf(mesh, slot)!;
                Assert.Equal(MemoryPoolClass.DeviceBuffers, buffer.Allocation.Block!.Class);
                var flags = allocator.FlagsOf(buffer.Allocation.Block.TypeIndex);
                if (!staticMesh) Assert.NotEqual(IntPtr.Zero, buffer.Mapped);
                else
                {
                    Assert.True((flags & MemoryPropertyFlags.DeviceLocalBit) != 0);
                    var requirements = VulkanAllocator.BufferRequirements(device.ContextForTests, buffer.Handle, out _);
                    if (allocator.HasMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.HostVisibleBit))
                        Assert.Equal(IntPtr.Zero, buffer.Mapped);
                }
            }
            for (int frame = 0; frame < 4; frame++)
            {
                device.BeginFrame(); device.BindFramebuffer(target); device.SetViewport(0, 0, 8, 8);
                device.SetDepthTest(false); device.SetCullFace(false);
                device.SetBlend(false, EnumBlendMode.Standard); device.UseProgram(program);
                device.ClearColor(0, 0, 0, 0, 1);
                if (staticMesh) device.DrawMesh(mesh);
                else device.DrawMeshMulti(mesh, new[] { 0, 0 }, new[] { 6 }, 1, false);
                byte[] pixels = Read(device, 8, 8);
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        for (int channel = 0; channel < 4; channel++)
                            Assert.Equal((byte)(channel == 3 || x < 4 ? 255 : 0), pixels[(y * 8 + x) * 4 + channel]);
                device.Present();
            }
            if (!staticMesh) Assert.True(allocator.ReBarMisses > misses);
            device.DeleteMesh(mesh); device.DeleteFramebuffer(target); device.DeleteTexture(texture);
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void YoungDescriptorsReuseSlotStorageAndThenBecomeCached()
    {
        var device = Open();
        try
        {
            device.ShortLivedFramesForTests = 4;
            int target = Attach(device, device.CreateTexture2D(4, 4, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false), 4, 4);
            int program = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                uniform sampler2D sky;
                out vec4 color;
                void main() { color = texelFetch(sky, ivec2(0), 0); }
                """, "descriptor-age");
            device.SetSamplerUnit(program, "sky", 0);
            for (int i = 0; i < 5; i++) { device.BeginFrame(); device.Present(); }
            byte[] pixel = { 40, 120, 200, 255 };
            int texture;
            fixed (byte* pointer = pixel) texture = device.CreateTexture2D(1, 1, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, (IntPtr)pointer, false);
            int cached = -1;
            var poolCounts = new Dictionary<int, int>();
            for (int frame = 0; frame < 10; frame++)
            {
                device.BeginFrame();
                int slot = device.CurrentSlotForTests;
                var arena = device.DescriptorArenaForTests(slot);
                Assert.Equal(0, arena.SetsThisFrame);
                device.BindTexture(0, texture);
                Draw(device, target, program, 4, 4);
                long allocations = arena.Allocations;
                for (int i = 0; i < 16; i++) device.DrawFullscreenTriangle();
                Assert.Equal(allocations, arena.Allocations);
                if (frame == 0) cached = device.CachedDescriptorSets;
                if (frame < 3) { Assert.Equal(1, arena.SetsThisFrame); Assert.Equal(cached, device.CachedDescriptorSets); }
                if (frame >= 4) { Assert.Equal(0, arena.SetsThisFrame); Assert.Equal(cached + 1, device.CachedDescriptorSets); }
                if (poolCounts.TryGetValue(slot, out int pools)) Assert.Equal(pools, arena.PoolCount);
                else poolCounts.Add(slot, arena.PoolCount);
                byte[] actual = Read(device, 4, 4);
                for (int i = 0; i < actual.Length; i++) Assert.Equal(pixel[i % 4], actual[i]);
                device.Present();
            }
            device.DeleteTexture(texture);
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }
}
