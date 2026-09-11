using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// What a piece of memory is for. Each class pools separately with its own block
/// size, so a long-lived static mesh never shares a block with per-frame data and
/// the cap on ReBAR can be enforced by class rather than guessed from flags.
/// </summary>
internal enum MemoryPoolClass
{
    /// <summary>Sampled textures and attachments: optimally tiled images, 128 MiB blocks.</summary>
    DeviceImages = 0,

    /// <summary>Long-lived buffers (static meshes device-local, dynamic meshes host-visible): 64 MiB blocks.</summary>
    DeviceBuffers = 1,

    /// <summary>Host-side transfer memory (staging, readback arenas, a ReBAR miss's fall-through): 32 MiB blocks.</summary>
    Staging = 2,

    /// <summary>
    /// Device-local and host-visible memory for per-frame dynamic data only
    /// (uniform ring, indirect ring): 16 MiB blocks, capped at
    /// min(192 MiB, budget x 0.25). A miss falls through to <see cref="Staging" />.
    /// </summary>
    ReBar = 3,

    /// <summary>Frame-graph transient attachments (reserved for Phase 2): 64 MiB blocks.</summary>
    Transient = 4,

    /// <summary>
    /// One allocation per resource: the driver asked for it
    /// (VkMemoryDedicatedRequirements) or the resource is at least a quarter of
    /// its class's block size.
    /// </summary>
    Dedicated = 5,
}

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

    /// <summary>The heap the block's memory type draws from.</summary>
    public uint HeapIndex { get; }

    /// <summary>
    /// The pool class the block belongs to. A dedicated block keeps the class of
    /// the resource it backs, so a dedicated ReBAR resource still counts against
    /// the ReBAR cap.
    /// </summary>
    public MemoryPoolClass Class { get; }

    /// <summary>
    /// Whether this block holds linear resources (buffers) or optimally tiled
    /// ones (images). They are never mixed, which is what makes
    /// bufferImageGranularity irrelevant here: the spec only requires padding
    /// between the two kinds, and there is never a boundary between them.
    /// </summary>
    public bool Linear { get; }

    /// <summary>Set when the block backs exactly one resource.</summary>
    public bool Dedicated { get; }

    /// <summary>Base host pointer when the memory type is host visible.</summary>
    public IntPtr Mapped { get; private set; }

    public ulong Used { get; private set; }

    public bool IsEmpty => Used == 0;

    /// <summary>
    /// The allocator frame at which a pooled block last became empty, or -1 while
    /// it holds anything. Empty blocks are freed after
    /// <see cref="VulkanAllocator.EmptyBlockFrames" /> frames.
    /// </summary>
    internal long EmptySinceFrame = -1;

    public MemoryBlock(
        VulkanContext context, ulong size, uint typeIndex, bool linear, bool dedicated, bool hostVisible)
        : this(context, size, typeIndex, 0, MemoryPoolClass.DeviceBuffers, linear, dedicated, hostVisible,
            default, default)
    {
    }

    public MemoryBlock(
        VulkanContext context, ulong size, uint typeIndex, uint heapIndex, MemoryPoolClass poolClass,
        bool linear, bool dedicated, bool hostVisible, Buffer dedicatedBuffer, Image dedicatedImage)
    {
        _context = context;
        Size = size;
        TypeIndex = typeIndex;
        HeapIndex = heapIndex;
        Class = poolClass;
        Linear = linear;
        Dedicated = dedicated;

        // A dedicated block names its resource, which lets the driver place it
        // (and is mandatory when the resource reported requiresDedicatedAllocation).
        var dedicatedInfo = new MemoryDedicatedAllocateInfo
        {
            SType = StructureType.MemoryDedicatedAllocateInfo,
            Buffer = dedicatedBuffer,
            Image = dedicatedImage,
        };
        bool namesResource = dedicated && (dedicatedBuffer.Handle != 0 || dedicatedImage.Handle != 0);

        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = namesResource ? &dedicatedInfo : null,
            AllocationSize = size,
            MemoryTypeIndex = typeIndex,
        };

        Memory = VulkanMemory.Allocate(context, allocateInfo,
            $"a {size} byte {(dedicated ? "dedicated" : "pooled")} {poolClass} memory block");

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

/// <summary>The allocator's state at one moment, for the <c>stats.memory</c> line and tests.</summary>
internal readonly record struct MemorySnapshot(
    int Blocks,
    int DedicatedBlocks,
    ulong ReBarUsed,
    ulong ReBarCap,
    long ReBarMisses,
    long EmptyBlocksFreed,
    bool BudgetExtension,
    ulong[] ClassBytes,
    ulong[] HeapUsed,
    ulong[] HeapBudget);

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
/// Phase 1B step 5: memory is pooled per <see cref="MemoryPoolClass" /> and
/// memory type. Buffers and images are kept in separate blocks so
/// bufferImageGranularity never applies; anything the driver wants dedicated, or
/// large enough to waste a quarter of its class's block, gets its own. Heap
/// budgets come from VK_EXT_memory_budget when the device has it (heap x 0.7
/// otherwise). Empty blocks are freed after <see cref="EmptyBlockFrames" />
/// frames, or at once when a heap is over its budget.
/// </summary>
internal sealed unsafe class VulkanAllocator : IDisposable
{
    public const int PoolClassCount = 6;

    private const ulong MiB = 1024UL * 1024;

    /// <summary>Frames a pooled block stays empty before it is freed.</summary>
    public const int EmptyBlockFrames = 120;

    /// <summary>The ReBAR cap's ceiling; the cap is min(this, budget x 0.25).</summary>
    public const ulong ReBarCapCeiling = 192 * MiB;

    /// <summary>Budget as a share of heap size when VK_EXT_memory_budget is absent.</summary>
    public const double FallbackBudgetShare = 0.7;

    /// <summary>ReBAR misses reported through <see cref="Log" />; the counter keeps counting past it.</summary>
    private const int LoggedMissLimit = 32;

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

    /// <summary>OPTIMUM_VULKAN_NO_REBAR=1 forces every ReBAR request down the fall-through path.</summary>
    private static readonly bool ReBarDisabled =
        Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_NO_REBAR") == "1";

    private readonly VulkanContext _context;
    private readonly object _gate = new();
    private readonly Dictionary<(MemoryPoolClass Class, uint TypeIndex, bool Linear), List<MemoryBlock>> _pools = new();
    private readonly List<MemoryBlock> _dedicated = new();
    private readonly PhysicalDeviceMemoryProperties _memoryProperties;
    private readonly ulong[] _heapUsed;
    private readonly ulong[] _heapBudget;
    private readonly ulong[] _classBytes = new ulong[PoolClassCount];
    private ulong _reBarUsed;
    private long _reBarMisses;
    private long _emptyBlocksFreed;
    private long _frame;
    private int _emptyBlocks;
    private bool _disposed;

    public VulkanAllocator(VulkanContext context)
    {
        _context = context;
        context.Api.GetPhysicalDeviceMemoryProperties(context.PhysicalDevice, out _memoryProperties);
        _heapUsed = new ulong[_memoryProperties.MemoryHeapCount];
        _heapBudget = new ulong[_memoryProperties.MemoryHeapCount];
        BudgetExtension = context.MemoryBudgetAvailable;
        RefreshBudgetLocked();
    }

    /// <summary>
    /// Receives a line for each logged event (ReBAR misses). The device points it
    /// at the validation mirror; never at GetError, since a miss is not an error.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <summary>Whether heap budgets come from VK_EXT_memory_budget.</summary>
    public bool BudgetExtension { get; }

    /// <summary>Replaces the ReBAR cap. Tests only.</summary>
    internal ulong? ReBarCapOverrideForTests { get; set; }

    /// <summary>Replaces every heap's budget. Tests only.</summary>
    internal ulong? HeapBudgetOverrideForTests { get; set; }

    /// <summary>Blocks currently held, which is the real vkAllocateMemory count.</summary>
    public int BlockCount
    {
        get
        {
            lock (_gate)
            {
                return BlockCountLocked();
            }
        }
    }

    private int BlockCountLocked()
    {
        int count = _dedicated.Count;
        foreach (List<MemoryBlock> blocks in _pools.Values) count += blocks.Count;
        return count;
    }

    public long ReBarMisses
    {
        get
        {
            lock (_gate) return _reBarMisses;
        }
    }

    /// <summary>Block bytes of the given class on ReBAR memory types, dedicated ones included.</summary>
    public ulong ReBarUsed
    {
        get
        {
            lock (_gate) return _reBarUsed;
        }
    }

    public static ulong BlockSizeOf(MemoryPoolClass poolClass) => poolClass switch
    {
        MemoryPoolClass.DeviceImages => 128 * MiB,
        MemoryPoolClass.DeviceBuffers => 64 * MiB,
        MemoryPoolClass.Staging => 32 * MiB,
        MemoryPoolClass.ReBar => 16 * MiB,
        MemoryPoolClass.Transient => 64 * MiB,
        _ => 64 * MiB,
    };

    /// <summary>
    /// The class a request lands in when the caller does not say: images are
    /// DeviceImages; a buffer asking for device-local and host-visible memory is
    /// ReBar; every other buffer is DeviceBuffers.
    /// </summary>
    public static MemoryPoolClass InferClass(MemoryPropertyFlags properties, bool linear)
    {
        if (!linear) return MemoryPoolClass.DeviceImages;
        const MemoryPropertyFlags reBar = MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit;
        return (properties & reBar) == reBar ? MemoryPoolClass.ReBar : MemoryPoolClass.DeviceBuffers;
    }

    public MemoryAllocation Allocate(
        MemoryRequirements requirements, MemoryPropertyFlags properties, bool linear, string what) =>
        Allocate(requirements, properties, linear, what, InferClass(properties, linear), false, default, default);

    /// <summary>
    /// Allocates for one resource. <paramref name="requiresDedicated" /> is the
    /// resource's VkMemoryDedicatedRequirements (required or preferred), and the
    /// buffer or image handle, when given, is named in a dedicated allocation.
    /// </summary>
    public MemoryAllocation Allocate(
        MemoryRequirements requirements, MemoryPropertyFlags properties, bool linear, string what,
        MemoryPoolClass poolClass, bool requiresDedicated, Buffer buffer, Image image)
    {
        if (poolClass == MemoryPoolClass.Dedicated)
        {
            requiresDedicated = true;
            poolClass = InferClass(properties, linear);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (poolClass == MemoryPoolClass.ReBar)
            {
                return AllocateReBarLocked(requirements, properties, linear, what, requiresDedicated, buffer, image);
            }

            uint typeIndex = FindMemoryType(requirements.MemoryTypeBits, properties, Avoided(properties));
            return AllocateLocked(requirements, typeIndex, poolClass, linear, what, requiresDedicated, buffer, image);
        }
    }

    /// <summary>
    /// ReBAR holds only per-frame dynamic data and is capped. A request that finds
    /// no ReBAR type, or would take the class past its cap, is counted, logged and
    /// served from host-visible staging memory instead.
    /// </summary>
    private MemoryAllocation AllocateReBarLocked(
        MemoryRequirements requirements, MemoryPropertyFlags properties, bool linear, string what,
        bool requiresDedicated, Buffer buffer, Image image)
    {
        MemoryPropertyFlags wanted = properties | MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit;
        string? miss = null;

        if (ReBarDisabled)
        {
            miss = "OPTIMUM_VULKAN_NO_REBAR=1";
        }
        else if (!TryFindMemoryType(requirements.MemoryTypeBits, wanted, 0, out uint typeIndex))
        {
            miss = "no device-local host-visible memory type";
        }
        else
        {
            bool dedicated = AlwaysDedicated || requiresDedicated
                || requirements.Size >= BlockSizeOf(MemoryPoolClass.ReBar) / 4;
            var key = (MemoryPoolClass.ReBar, typeIndex, linear);

            if (!dedicated && _pools.TryGetValue(key, out List<MemoryBlock>? pool))
            {
                foreach (MemoryBlock candidate in pool)
                {
                    if (candidate.TryAllocate(requirements.Size, requirements.Alignment, out ulong offset))
                    {
                        NoteFilled(candidate);
                        return Describe(candidate, offset, requirements.Size);
                    }
                }
            }

            ulong growth = dedicated ? requirements.Size : BlockSizeOf(MemoryPoolClass.ReBar);
            ulong cap = ReBarCapLocked(typeIndex);
            if (_reBarUsed + growth > cap)
            {
                miss = "cap " + cap + " bytes reached (" + _reBarUsed + " used, " + growth + " more needed)";
            }
            else
            {
                return AllocateLocked(requirements, typeIndex, MemoryPoolClass.ReBar, linear, what,
                    requiresDedicated, buffer, image);
            }
        }

        _reBarMisses++;
        VulkanStats.NoteRebarFallback();
        if (_reBarMisses <= LoggedMissLimit)
        {
            string line = "[Optimum] ReBAR miss for " + what + ": " + miss +
                "; falling through to host-visible staging memory (miss " + _reBarMisses + ")";
            Log?.Invoke(line);
            if (RenderTrace.Enabled) RenderTrace.Write(line);
        }

        MemoryPropertyFlags host = (properties & ~MemoryPropertyFlags.DeviceLocalBit)
            | MemoryPropertyFlags.HostVisibleBit;
        uint hostType = FindMemoryType(requirements.MemoryTypeBits, host, MemoryPropertyFlags.DeviceLocalBit);
        return AllocateLocked(requirements, hostType, MemoryPoolClass.Staging, linear, what, requiresDedicated,
            buffer, image);
    }

    private MemoryAllocation AllocateLocked(
        MemoryRequirements requirements, uint typeIndex, MemoryPoolClass poolClass, bool linear, string what,
        bool requiresDedicated, Buffer buffer, Image image)
    {
        MemoryType type = _memoryProperties.MemoryTypes[(int)typeIndex];
        bool hostVisible = (type.PropertyFlags & MemoryPropertyFlags.HostVisibleBit) != 0;
        ulong blockSize = BlockSizeOf(poolClass);

        if (AlwaysDedicated || requiresDedicated || requirements.Size >= blockSize / 4)
        {
            var block = new MemoryBlock(
                _context, requirements.Size, typeIndex, type.HeapIndex, poolClass, linear, dedicated: true,
                hostVisible, buffer, image);
            _dedicated.Add(block);
            NoteBlockCreated(block);

            if (!block.TryAllocate(requirements.Size, requirements.Alignment, out ulong dedicatedOffset))
            {
                throw new InvalidOperationException("a dedicated block could not satisfy " + what);
            }
            return Describe(block, dedicatedOffset, requirements.Size);
        }

        var key = (poolClass, typeIndex, linear);
        if (!_pools.TryGetValue(key, out List<MemoryBlock>? pool))
        {
            pool = new List<MemoryBlock>();
            _pools[key] = pool;
        }

        foreach (MemoryBlock candidate in pool)
        {
            if (candidate.TryAllocate(requirements.Size, requirements.Alignment, out ulong offset))
            {
                NoteFilled(candidate);
                return Describe(candidate, offset, requirements.Size);
            }
        }

        var fresh = new MemoryBlock(
            _context, blockSize, typeIndex, type.HeapIndex, poolClass, linear, dedicated: false, hostVisible,
            default, default);
        pool.Add(fresh);
        NoteBlockCreated(fresh);

        if (!fresh.TryAllocate(requirements.Size, requirements.Alignment, out ulong freshOffset))
        {
            throw new InvalidOperationException("a fresh block could not satisfy " + what);
        }
        return Describe(fresh, freshOffset, requirements.Size);
    }

    private void NoteBlockCreated(MemoryBlock block)
    {
        _heapUsed[block.HeapIndex] += block.Size;
        _classBytes[(int)(block.Dedicated ? MemoryPoolClass.Dedicated : block.Class)] += block.Size;
        if (block.Class == MemoryPoolClass.ReBar) _reBarUsed += block.Size;
    }

    private void NoteBlockReleased(MemoryBlock block)
    {
        _heapUsed[block.HeapIndex] -= Math.Min(_heapUsed[block.HeapIndex], block.Size);
        int index = (int)(block.Dedicated ? MemoryPoolClass.Dedicated : block.Class);
        _classBytes[index] -= Math.Min(_classBytes[index], block.Size);
        if (block.Class == MemoryPoolClass.ReBar) _reBarUsed -= Math.Min(_reBarUsed, block.Size);
    }

    private void NoteFilled(MemoryBlock block)
    {
        if (block.EmptySinceFrame < 0) return;
        block.EmptySinceFrame = -1;
        _emptyBlocks--;
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
                NoteBlockReleased(block);
                block.Dispose();
                return;
            }

            // An emptied block is kept for EmptyBlockFrames frames, so a pool that
            // is repeatedly drained and refilled - which chunk streaming does - is
            // not paying for an allocation each time.
            if (!block.IsEmpty || block.EmptySinceFrame >= 0) return;
            block.EmptySinceFrame = _frame;
            _emptyBlocks++;
        }
    }

    /// <summary>
    /// One frame boundary (the frame ring calls it at BeginFrame): refreshes the
    /// heap budgets now and then, and frees pooled blocks that stayed empty for
    /// <see cref="EmptyBlockFrames" /> frames, or every empty block at once while a
    /// heap is over its budget.
    /// </summary>
    public void AdvanceFrame()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _frame++;

            if (_frame % 60 == 0) RefreshBudgetLocked();
            if (_emptyBlocks == 0) return;

            bool pressure = false;
            for (int heap = 0; heap < _heapUsed.Length; heap++)
            {
                if (_heapUsed[heap] > HeapBudgetLocked(heap)) pressure = true;
            }

            foreach (List<MemoryBlock> pool in _pools.Values)
            {
                for (int i = pool.Count - 1; i >= 0; i--)
                {
                    MemoryBlock block = pool[i];
                    if (block.EmptySinceFrame < 0 || !block.IsEmpty) continue;
                    if (!pressure && _frame - block.EmptySinceFrame < EmptyBlockFrames) continue;

                    pool.RemoveAt(i);
                    _emptyBlocks--;
                    _emptyBlocksFreed++;
                    NoteBlockReleased(block);
                    block.Dispose();
                }
            }
        }
    }

    private ulong HeapBudgetLocked(int heap) => HeapBudgetOverrideForTests ?? _heapBudget[heap];

    private ulong ReBarCapLocked(uint typeIndex)
    {
        if (ReBarCapOverrideForTests is { } forced) return forced;
        uint heap = _memoryProperties.MemoryTypes[(int)typeIndex].HeapIndex;
        return Math.Min(ReBarCapCeiling, HeapBudgetLocked((int)heap) / 4);
    }

    private void RefreshBudgetLocked()
    {
        int heaps = (int)_memoryProperties.MemoryHeapCount;
        if (BudgetExtension)
        {
            var budget = new PhysicalDeviceMemoryBudgetPropertiesEXT
            {
                SType = StructureType.PhysicalDeviceMemoryBudgetPropertiesExt,
            };
            var properties = new PhysicalDeviceMemoryProperties2
            {
                SType = StructureType.PhysicalDeviceMemoryProperties2,
                PNext = &budget,
            };
            _context.Api.GetPhysicalDeviceMemoryProperties2(_context.PhysicalDevice, &properties);
            for (int i = 0; i < heaps; i++)
            {
                ulong reported = budget.HeapBudget[i];
                _heapBudget[i] = reported > 0
                    ? reported
                    : (ulong)(_memoryProperties.MemoryHeaps[i].Size * FallbackBudgetShare);
            }
            return;
        }

        for (int i = 0; i < heaps; i++)
        {
            _heapBudget[i] = (ulong)(_memoryProperties.MemoryHeaps[i].Size * FallbackBudgetShare);
        }
    }

    public MemorySnapshot Snapshot()
    {
        lock (_gate)
        {
            var heapBudget = new ulong[_heapBudget.Length];
            for (int i = 0; i < heapBudget.Length; i++) heapBudget[i] = HeapBudgetLocked(i);

            ulong cap = 0;
            if (TryFindMemoryType(uint.MaxValue,
                    MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit, 0, out uint reBarType))
            {
                cap = ReBarCapLocked(reBarType);
            }

            return new MemorySnapshot(
                BlockCountLocked(), _dedicated.Count, _reBarUsed, cap, _reBarMisses, _emptyBlocksFreed,
                BudgetExtension, (ulong[])_classBytes.Clone(), (ulong[])_heapUsed.Clone(), heapBudget);
        }
    }

    /// <summary>
    /// Flags a request should avoid when it can: device-local-only memory keeps off
    /// host-visible types (so ReBAR is left for per-frame data), and host memory
    /// keeps off device-local types (so it does not eat the BAR either).
    /// </summary>
    private static MemoryPropertyFlags Avoided(MemoryPropertyFlags properties)
    {
        bool deviceLocal = (properties & MemoryPropertyFlags.DeviceLocalBit) != 0;
        bool hostVisible = (properties & MemoryPropertyFlags.HostVisibleBit) != 0;
        if (deviceLocal && !hostVisible) return MemoryPropertyFlags.HostVisibleBit;
        if (hostVisible && !deviceLocal) return MemoryPropertyFlags.DeviceLocalBit;
        return 0;
    }

    private bool TryFindMemoryType(uint typeBits, MemoryPropertyFlags properties, MemoryPropertyFlags avoid,
        out uint typeIndex)
    {
        for (uint i = 0; i < _memoryProperties.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0) continue;

            MemoryPropertyFlags flags = _memoryProperties.MemoryTypes[(int)i].PropertyFlags;
            if ((flags & properties) == properties && (flags & avoid) == 0)
            {
                typeIndex = i;
                return true;
            }
        }

        typeIndex = 0;
        return false;
    }

    /// <summary>
    /// The first type with every requested property, preferring one without the
    /// avoided flags; on a unified-memory device nothing can be avoided and the
    /// first match is taken.
    /// </summary>
    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags properties, MemoryPropertyFlags avoid)
    {
        if (avoid != 0 && TryFindMemoryType(typeBits, properties, avoid, out uint preferred)) return preferred;
        if (TryFindMemoryType(typeBits, properties, 0, out uint any)) return any;
        throw new InvalidOperationException($"no memory type with {properties}");
    }

    /// <summary>The property flags of a memory type. Tests and diagnostics.</summary>
    public MemoryPropertyFlags FlagsOf(uint typeIndex) => _memoryProperties.MemoryTypes[(int)typeIndex].PropertyFlags;

    /// <summary>Whether any type the mask allows has the properties and none of the avoided flags.</summary>
    public bool HasMemoryType(uint typeBits, MemoryPropertyFlags properties, MemoryPropertyFlags avoid)
    {
        lock (_gate) return TryFindMemoryType(typeBits, properties, avoid, out _);
    }

    /// <summary>A buffer's requirements plus whether the driver requires or prefers a dedicated allocation.</summary>
    public static MemoryRequirements BufferRequirements(VulkanContext context, Buffer buffer, out bool dedicated)
    {
        var dedicatedRequirements = new MemoryDedicatedRequirements
        {
            SType = StructureType.MemoryDedicatedRequirements,
        };
        var requirements = new MemoryRequirements2
        {
            SType = StructureType.MemoryRequirements2,
            PNext = &dedicatedRequirements,
        };
        var info = new BufferMemoryRequirementsInfo2
        {
            SType = StructureType.BufferMemoryRequirementsInfo2,
            Buffer = buffer,
        };
        context.Api.GetBufferMemoryRequirements2(context.Device, &info, &requirements);
        dedicated = dedicatedRequirements.RequiresDedicatedAllocation || dedicatedRequirements.PrefersDedicatedAllocation;
        return requirements.MemoryRequirements;
    }

    /// <summary>An image's requirements plus whether the driver requires or prefers a dedicated allocation.</summary>
    public static MemoryRequirements ImageRequirements(VulkanContext context, Image image, out bool dedicated)
    {
        var dedicatedRequirements = new MemoryDedicatedRequirements
        {
            SType = StructureType.MemoryDedicatedRequirements,
        };
        var requirements = new MemoryRequirements2
        {
            SType = StructureType.MemoryRequirements2,
            PNext = &dedicatedRequirements,
        };
        var info = new ImageMemoryRequirementsInfo2
        {
            SType = StructureType.ImageMemoryRequirementsInfo2,
            Image = image,
        };
        context.Api.GetImageMemoryRequirements2(context.Device, &info, &requirements);
        dedicated = dedicatedRequirements.RequiresDedicatedAllocation || dedicatedRequirements.PrefersDedicatedAllocation;
        return requirements.MemoryRequirements;
    }

    /// <summary>The <c>stats.memory</c> line: blocks, ReBAR use and misses, bytes per class, used/budget per heap.</summary>
    public static string FormatMemoryLine(MemorySnapshot snapshot)
    {
        var line = new StringBuilder("stats.memory");
        line.Append(" blocks=").Append(snapshot.Blocks.ToString(CultureInfo.InvariantCulture));
        line.Append(" dedicated=").Append(snapshot.DedicatedBlocks.ToString(CultureInfo.InvariantCulture));
        line.Append(" rebar_used=").Append(snapshot.ReBarUsed.ToString(CultureInfo.InvariantCulture));
        line.Append(" rebar_cap=").Append(snapshot.ReBarCap.ToString(CultureInfo.InvariantCulture));
        line.Append(" rebar_misses=").Append(snapshot.ReBarMisses.ToString(CultureInfo.InvariantCulture));
        line.Append(" empty_blocks_freed=").Append(snapshot.EmptyBlocksFreed.ToString(CultureInfo.InvariantCulture));
        line.Append(" budget_ext=").Append(snapshot.BudgetExtension ? '1' : '0');
        line.Append(" class_bytes=");
        for (int i = 0; i < PoolClassCount; i++)
        {
            if (i > 0) line.Append(',');
            ulong bytes = snapshot.ClassBytes != null && i < snapshot.ClassBytes.Length ? snapshot.ClassBytes[i] : 0;
            line.Append(bytes.ToString(CultureInfo.InvariantCulture));
        }
        line.Append(" heaps=");
        int heaps = snapshot.HeapUsed?.Length ?? 0;
        for (int i = 0; i < heaps; i++)
        {
            if (i > 0) line.Append(',');
            line.Append(snapshot.HeapUsed![i].ToString(CultureInfo.InvariantCulture)).Append('/');
            ulong budget = snapshot.HeapBudget != null && i < snapshot.HeapBudget.Length ? snapshot.HeapBudget[i] : 0;
            line.Append(budget.ToString(CultureInfo.InvariantCulture));
        }
        return line.ToString();
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
