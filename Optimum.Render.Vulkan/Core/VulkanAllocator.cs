using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>A region of a memory block handed to one resource.</summary>
internal readonly struct MemoryAllocation
{
    public DeviceMemory Memory { get; init; }
    public ulong Offset { get; init; }
    public ulong Size { get; init; }

    /// <summary>Host pointer to this region, or zero when the memory is not mapped.</summary>
    public IntPtr Mapped { get; init; }

    internal MemoryBlock? Block { get; init; }

    public bool IsValid => Block != null;
}

/// <summary>
/// One vkAllocateMemory, divided up among many resources.
///
/// Free space is tracked as ranges in address order, so neighbouring frees merge
/// back into one range and the block does not fragment into confetti as chunk
/// meshes come and go.
/// </summary>
internal sealed unsafe class MemoryBlock : IDisposable
{
    private readonly struct FreeRange
    {
        public FreeRange(ulong offset, ulong size)
        {
            Offset = offset;
            Size = size;
        }

        public ulong Offset { get; }
        public ulong Size { get; }
        public ulong End => Offset + Size;
    }

    private readonly VulkanContext _context;
    private readonly List<FreeRange> _free = new();
    private bool _disposed;

    public DeviceMemory Memory { get; }
    public ulong Size { get; }
    public uint TypeIndex { get; }

    /// <summary>
    /// Whether this block holds linear resources (buffers) or optimally tiled
    /// ones (images). They are never mixed, which is what makes
    /// bufferImageGranularity irrelevant here: the spec only requires padding
    /// between the two kinds, and there is never a boundary between them.
    /// </summary>
    public bool Linear { get; }

    /// <summary>Set when the block backs exactly one oversized resource.</summary>
    public bool Dedicated { get; }

    /// <summary>Base host pointer when the memory type is host visible.</summary>
    public IntPtr Mapped { get; private set; }

    public ulong Used { get; private set; }

    public bool IsEmpty => Used == 0;

    public MemoryBlock(
        VulkanContext context, ulong size, uint typeIndex, bool linear, bool dedicated, bool hostVisible)
    {
        _context = context;
        Size = size;
        TypeIndex = typeIndex;
        Linear = linear;
        Dedicated = dedicated;

        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = size,
            MemoryTypeIndex = typeIndex,
        };

        Memory = VulkanMemory.Allocate(context, allocateInfo,
            $"a {size} byte {(dedicated ? "dedicated" : "pooled")} memory block");

        if (hostVisible)
        {
            void* mapped;
            // Mapped once for the block's whole life. Mapping is not free and a
            // resource may be written from any thread, so per-resource mapping
            // would be both slower and harder to synchronise.
            if (context.Api.MapMemory(context.Device, Memory, 0, size, 0, &mapped) == Result.Success)
            {
                Mapped = (IntPtr)mapped;
            }
        }

        _free.Add(new FreeRange(0, size));
    }

    public bool TryAllocate(ulong size, ulong alignment, out ulong offset)
    {
        offset = 0;
        if (size == 0 || _disposed) return false;

        for (int i = 0; i < _free.Count; i++)
        {
            FreeRange range = _free[i];

            ulong aligned = alignment <= 1
                ? range.Offset
                : (range.Offset + alignment - 1) / alignment * alignment;

            ulong padding = aligned - range.Offset;
            if (range.Size < padding || range.Size - padding < size) continue;

            ulong tail = range.Size - padding - size;

            // The alignment padding stays free rather than being lost, so a
            // later smaller or less strictly aligned resource can use it.
            _free.RemoveAt(i);
            if (tail > 0) _free.Insert(i, new FreeRange(aligned + size, tail));
            if (padding > 0) _free.Insert(i, new FreeRange(range.Offset, padding));

            Used += size;
            offset = aligned;
            return true;
        }

        return false;
    }

    public void Free(ulong offset, ulong size)
    {
        if (_disposed || size == 0) return;

        Used -= Math.Min(Used, size);

        int index = 0;
        while (index < _free.Count && _free[index].Offset < offset) index++;

        ulong start = offset;
        ulong end = offset + size;

        // Merge with the range before, if they touch.
        if (index > 0 && _free[index - 1].End == start)
        {
            start = _free[index - 1].Offset;
            _free.RemoveAt(index - 1);
            index--;
        }

        // And with the range after.
        if (index < _free.Count && _free[index].Offset == end)
        {
            end = _free[index].End;
            _free.RemoveAt(index);
        }

        _free.Insert(index, new FreeRange(start, end - start));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Mapped != IntPtr.Zero)
        {
            _context.Api.UnmapMemory(_context.Device, Memory);
            Mapped = IntPtr.Zero;
        }

        _context.Api.FreeMemory(_context.Device, Memory, null);
        VulkanMemory.NoteFree();
        _free.Clear();
    }
}

/// <summary>
/// Hands resources memory out of a few large blocks instead of giving each its
/// own allocation.
///
/// A device allocation is not a cheap object. The driver tracks every one of
/// them and builds a residency list over the whole set on each submit, so cost
/// grows with the count rather than with the bytes. Backing every buffer and
/// image individually put a loaded world at eighteen thousand live allocations,
/// where frames took 200 ms; the same world's memory in a few dozen blocks is
/// the difference between four frames a second and fifty. The allocation limit
/// the spec exposes - commonly 4096 - is the same problem stated as a hard cap,
/// and NVIDIA not enforcing one is why this degraded instead of failing.
///
/// Buffers and images are kept in separate blocks so bufferImageGranularity
/// never applies, and anything large enough to waste a block gets its own.
/// </summary>
internal sealed unsafe class VulkanAllocator : IDisposable
{
    /// <summary>Size of a pooled block.</summary>
    private const ulong BlockSize = 64UL * 1024 * 1024;

    /// <summary>Above this, a resource gets its own allocation rather than a slice.</summary>
    private const ulong DedicatedThreshold = BlockSize / 4;

    /// <summary>
    /// Set by OPTIMUM_VULKAN_DEDICATED_MEMORY=1 to give every resource its own
    /// vkAllocateMemory, which is what this backend did before pooling existed.
    ///
    /// It is ruinously slow - that is the whole reason pooling is here - but it
    /// removes every question of one resource landing on another's bytes, so a
    /// rendering fault that survives it is not a suballocation fault. Keeping
    /// the old behaviour reachable is what makes that a one-run experiment
    /// rather than a bisect.
    /// </summary>
    private static readonly bool AlwaysDedicated =
        Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_DEDICATED_MEMORY") == "1";

    private readonly VulkanContext _context;
    private readonly object _gate = new();
    private readonly Dictionary<(uint TypeIndex, bool Linear), List<MemoryBlock>> _pools = new();
    private readonly List<MemoryBlock> _dedicated = new();
    private readonly PhysicalDeviceMemoryProperties _memoryProperties;
    private bool _disposed;

    public VulkanAllocator(VulkanContext context)
    {
        _context = context;
        context.Api.GetPhysicalDeviceMemoryProperties(context.PhysicalDevice, out _memoryProperties);
    }

    /// <summary>Blocks currently held, which is the real vkAllocateMemory count.</summary>
    public int BlockCount
    {
        get
        {
            lock (_gate)
            {
                int count = _dedicated.Count;
                foreach (List<MemoryBlock> blocks in _pools.Values) count += blocks.Count;
                return count;
            }
        }
    }

    public MemoryAllocation Allocate(
        MemoryRequirements requirements, MemoryPropertyFlags properties, bool linear, string what)
    {
        uint typeIndex = FindMemoryType(requirements.MemoryTypeBits, properties);
        bool hostVisible = (_memoryProperties.MemoryTypes[(int)typeIndex].PropertyFlags
            & MemoryPropertyFlags.HostVisibleBit) != 0;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (AlwaysDedicated || requirements.Size >= DedicatedThreshold)
            {
                var block = new MemoryBlock(
                    _context, requirements.Size, typeIndex, linear, dedicated: true, hostVisible);
                _dedicated.Add(block);

                if (!block.TryAllocate(requirements.Size, requirements.Alignment, out ulong dedicatedOffset))
                {
                    throw new InvalidOperationException("a dedicated block could not satisfy " + what);
                }
                return Describe(block, dedicatedOffset, requirements.Size);
            }

            var key = (typeIndex, linear);
            if (!_pools.TryGetValue(key, out List<MemoryBlock>? pool))
            {
                pool = new List<MemoryBlock>();
                _pools[key] = pool;
            }

            foreach (MemoryBlock candidate in pool)
            {
                if (candidate.TryAllocate(requirements.Size, requirements.Alignment, out ulong offset))
                {
                    return Describe(candidate, offset, requirements.Size);
                }
            }

            var fresh = new MemoryBlock(
                _context, BlockSize, typeIndex, linear, dedicated: false, hostVisible);
            pool.Add(fresh);

            if (!fresh.TryAllocate(requirements.Size, requirements.Alignment, out ulong freshOffset))
            {
                throw new InvalidOperationException("a fresh block could not satisfy " + what);
            }
            return Describe(fresh, freshOffset, requirements.Size);
        }
    }

    private static MemoryAllocation Describe(MemoryBlock block, ulong offset, ulong size) =>
        new()
        {
            Memory = block.Memory,
            Offset = offset,
            Size = size,
            Mapped = block.Mapped == IntPtr.Zero ? IntPtr.Zero : block.Mapped + (int)offset,
            Block = block,
        };

    public void Free(in MemoryAllocation allocation)
    {
        MemoryBlock? block = allocation.Block;
        if (block == null) return;

        lock (_gate)
        {
            if (_disposed) return;

            block.Free(allocation.Offset, allocation.Size);

            if (block.Dedicated)
            {
                _dedicated.Remove(block);
                block.Dispose();
                return;
            }

            // An emptied block is kept if it is its pool's last one, so a pool
            // that is repeatedly drained and refilled - which chunk streaming
            // does - is not paying for an allocation each time.
            if (!block.IsEmpty) return;

            var key = (block.TypeIndex, block.Linear);
            if (!_pools.TryGetValue(key, out List<MemoryBlock>? pool) || pool.Count <= 1) return;

            pool.Remove(block);
            block.Dispose();
        }
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags properties)
    {
        for (uint i = 0; i < _memoryProperties.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0) continue;

            MemoryPropertyFlags flags = _memoryProperties.MemoryTypes[(int)i].PropertyFlags;
            if ((flags & properties) == properties) return i;
        }

        throw new InvalidOperationException($"no memory type with {properties}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (List<MemoryBlock> pool in _pools.Values)
            {
                foreach (MemoryBlock block in pool) block.Dispose();
            }
            _pools.Clear();

            foreach (MemoryBlock block in _dedicated) block.Dispose();
            _dedicated.Clear();
        }
    }
}
