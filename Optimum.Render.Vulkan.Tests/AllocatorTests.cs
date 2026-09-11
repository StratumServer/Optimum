using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The suballocator. A device allocation is a driver-tracked object whose cost
/// is paid on every submit, so what matters is the count, not the bytes: a
/// loaded world at one allocation per resource reached eighteen thousand of them
/// and 200 ms frames.
/// </summary>
public class AllocatorTests
{
    private readonly ITestOutputHelper _output;

    public AllocatorTests(ITestOutputHelper output) => _output = output;

    private static bool TryCreateContext(ITestOutputHelper output, out VulkanContext? context) =>
        GpuTest.TryCreateContext(output, null, out context);

    /// <summary>
    /// The regression that matters. A chunk mesh is several buffers, and a world
    /// is thousands of chunks; that has to stay in the tens of allocations.
    /// </summary>
    [SkippableFact]
    public void AWorldsWorthOfBuffersCostsTensOfAllocationsNotThousands()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            int before = VulkanMemory.LiveAllocations;
            var buffers = new List<VulkanBuffer>();

            // 2000 buffers of 64 KB: about the shape of a loaded world's meshes.
            for (int i = 0; i < 2000; i++)
            {
                buffers.Add(new VulkanBuffer(context!, 64 * 1024,
                    BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit));
            }

            int used = VulkanMemory.LiveAllocations - before;
            _output.WriteLine($"2000 buffers cost {used} device allocations, " +
                $"{context!.Allocator.BlockCount} blocks live");

            // 2000 * 64 KB is 128 MB, so two 64 MB blocks is the floor; a handful
            // of extra for alignment is fine. Anything near 2000 is the old bug.
            Assert.InRange(used, 1, 16);

            foreach (VulkanBuffer buffer in buffers) buffer.Dispose();
        }
    }

    [SkippableFact]
    public void EveryBufferGetsDistinctMappedMemoryWithinItsBlock()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            var buffers = new List<VulkanBuffer>();
            for (int i = 0; i < 64; i++)
            {
                buffers.Add(new VulkanBuffer(context!, 4096,
                    BufferUsageFlags.VertexBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit));
            }

            // Each buffer writes its own index through its own pointer; if two
            // shared a region, or a pointer were the block base rather than the
            // buffer's slice, the values would not survive.
            for (int i = 0; i < buffers.Count; i++)
            {
                Assert.NotEqual(IntPtr.Zero, buffers[i].Mapped);
                unsafe { *(int*)buffers[i].Mapped = i; }
            }
            for (int i = 0; i < buffers.Count; i++)
            {
                unsafe { Assert.Equal(i, *(int*)buffers[i].Mapped); }
            }

            foreach (VulkanBuffer buffer in buffers) buffer.Dispose();
        }
    }

    /// <summary>
    /// No two live resources may ever share a byte.
    ///
    /// Distinct pointers are not the same claim: two regions can start in
    /// different places and still overlap, and the free list is where that goes
    /// wrong - a range merged with a neighbour it does not touch, or a split
    /// that loses its alignment padding, hands the same bytes out twice. On the
    /// GPU that is one resource writing over another's contents, which reads as
    /// corrupt geometry or textures rather than as a crash.
    ///
    /// So this runs the shape chunk streaming actually has - many live at once,
    /// mixed sizes and alignments, a churn of frees and fresh allocations - and
    /// checks every live pair after every round.
    /// </summary>
    [SkippableFact]
    public void LiveAllocationsNeverOverlapUnderChurn()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            // Sizes a chunk pool really asks for: the xyz slot is large, the
            // flag and colour streams small, and index buffers in between.
            int[] sizes = { 1024, 4096, 12_288, 65_536, 262_144, 3072, 48_000, 6144 };

            var live = new List<VulkanBuffer>();
            var random = new Random(20260909);

            void AssertNoOverlap(int round)
            {
                // Grouped by memory, since regions in different allocations are
                // unrelated however their offsets compare.
                var byMemory = new Dictionary<ulong, List<(ulong Start, ulong End, int Index)>>();

                for (int i = 0; i < live.Count; i++)
                {
                    MemoryAllocation allocation = live[i].Allocation;
                    ulong memory = allocation.Memory.Handle;
                    if (!byMemory.TryGetValue(memory, out var regions))
                    {
                        regions = new List<(ulong, ulong, int)>();
                        byMemory[memory] = regions;
                    }
                    regions.Add((allocation.Offset, allocation.Offset + allocation.Size, i));
                }

                foreach ((ulong memory, var regions) in byMemory)
                {
                    regions.Sort((a, b) => a.Start.CompareTo(b.Start));
                    for (int i = 1; i < regions.Count; i++)
                    {
                        Assert.True(regions[i].Start >= regions[i - 1].End,
                            $"round {round}: buffers {regions[i - 1].Index} and {regions[i].Index} overlap in " +
                            $"memory {memory:x}: [{regions[i - 1].Start}, {regions[i - 1].End}) and " +
                            $"[{regions[i].Start}, {regions[i].End})");
                    }
                }
            }

            for (int round = 0; round < 12; round++)
            {
                for (int i = 0; i < 120; i++)
                {
                    live.Add(new VulkanBuffer(context!, (ulong)sizes[random.Next(sizes.Length)],
                        BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit,
                        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit));
                }

                AssertNoOverlap(round);

                // Drop a scattered half, so the free list has to merge some
                // neighbours and keep others apart.
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    if (random.Next(2) != 0) continue;
                    live[i].Dispose();
                    live.RemoveAt(i);
                }

                AssertNoOverlap(round);
            }

            // And the bytes themselves survive, which no offset arithmetic can
            // fake: each buffer stamps its own identity and reads it back.
            for (int i = 0; i < live.Count; i++)
            {
                unsafe { *(int*)live[i].Mapped = i * 7919; }
            }
            for (int i = 0; i < live.Count; i++)
            {
                unsafe
                {
                    Assert.Equal(i * 7919, *(int*)live[i].Mapped);
                }
            }

            _output.WriteLine($"{live.Count} live buffers in {context!.Allocator.BlockCount} blocks");
            foreach (VulkanBuffer buffer in live) buffer.Dispose();
        }
    }

    /// <summary>
    /// Chunk streaming frees and reallocates constantly. Freed space has to come
    /// back, or a long session grows blocks without bound.
    /// </summary>
    [SkippableFact]
    public void FreedSpaceIsReusedRatherThanGrowingThePool()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const int batch = 200;
            for (int round = 0; round < 5; round++)
            {
                var buffers = new List<VulkanBuffer>();
                for (int i = 0; i < batch; i++)
                {
                    buffers.Add(new VulkanBuffer(context!, 128 * 1024,
                        BufferUsageFlags.VertexBufferBit,
                        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit));
                }
                foreach (VulkanBuffer buffer in buffers) buffer.Dispose();

                _output.WriteLine($"round {round}: {context!.Allocator.BlockCount} blocks");
            }

            // Five rounds of the same batch must not leave five rounds of blocks.
            Assert.InRange(context!.Allocator.BlockCount, 0, 4);
        }
    }

    /// <summary>
    /// A block only ever holds one kind of resource, which is what makes
    /// bufferImageGranularity a non-issue. Mixing them in one block is the
    /// classic source of corruption that only shows on some hardware.
    /// </summary>
    [SkippableFact]
    public void ImagesAndBuffersNeverShareABlock()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);

            var buffer = new VulkanBuffer(context!, 4096,
                BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            int textureId = textures.Create(64, 64, Format.R8G8B8A8Unorm);
            VulkanTexture? texture = textures.Get(textureId);
            Assert.NotNull(texture);

            Assert.NotEqual(buffer.MemoryHandleForTest, texture!.Allocation.Memory.Handle);

            buffer.Dispose();
        }
    }

    /// <summary>A resource too large to pool gets its own block rather than forcing a huge one.</summary>
    [SkippableFact]
    public void AnOversizedResourceGetsItsOwnBlockAndGivesItBack()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            int before = context!.Allocator.BlockCount;

            // Larger than the pooling threshold: an atlas is this shape.
            var big = new VulkanBuffer(context, 48UL * 1024 * 1024,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            Assert.Equal(before + 1, context.Allocator.BlockCount);

            big.Dispose();
            Assert.Equal(before, context.Allocator.BlockCount);
        }
    }
}
