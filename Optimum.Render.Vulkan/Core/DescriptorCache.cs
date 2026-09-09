using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// One combined image sampler binding.
///
/// <paramref name="Resource" /> is the texture's lifetime id, which is what
/// separates this binding from a later texture that inherits the same view
/// handle. Zero means the resource is permanent and needs no tracking.
/// </summary>
internal readonly record struct SamplerBindingValue(
    uint Binding, ImageView View, Sampler Sampler, ulong Resource = 0);

/// <summary>One buffer binding. <paramref name="Resource" /> as for samplers.</summary>
internal readonly record struct BufferBindingValue(
    uint Binding, Buffer Buffer, ulong Offset, ulong Range, ulong Resource = 0);

/// <summary>
/// The contents of one descriptor set, used as a cache key.
///
/// A set is written once and never updated, so identical contents can always
/// share a handle. That immutability is what makes the cache safe: there is no
/// moment where a set the GPU is reading gets rewritten.
/// </summary>
internal sealed class DescriptorSetContents : IEquatable<DescriptorSetContents>
{
    public int ProgramId { get; }
    public int SetIndex { get; }
    public SamplerBindingValue[] Samplers { get; }
    public BufferBindingValue[] Buffers { get; }

    private readonly int _hash;

    public DescriptorSetContents(
        int programId, int setIndex, SamplerBindingValue[] samplers, BufferBindingValue[] buffers)
    {
        ProgramId = programId;
        SetIndex = setIndex;
        Samplers = samplers;
        Buffers = buffers;

        var hash = new HashCode();
        hash.Add(programId);
        hash.Add(setIndex);
        foreach (SamplerBindingValue sampler in samplers)
        {
            hash.Add(sampler.Binding);
            hash.Add(sampler.View.Handle);
            hash.Add(sampler.Sampler.Handle);
            hash.Add(sampler.Resource);
        }
        foreach (BufferBindingValue buffer in buffers)
        {
            hash.Add(buffer.Binding);
            hash.Add(buffer.Buffer.Handle);
            hash.Add(buffer.Offset);
            hash.Add(buffer.Range);
            hash.Add(buffer.Resource);
        }
        _hash = hash.ToHashCode();
    }

    public bool Equals(DescriptorSetContents? other)
    {
        if (other is null || other._hash != _hash) return false;
        if (ProgramId != other.ProgramId || SetIndex != other.SetIndex) return false;
        if (Samplers.Length != other.Samplers.Length) return false;
        if (Buffers.Length != other.Buffers.Length) return false;

        for (int i = 0; i < Samplers.Length; i++)
        {
            if (!Samplers[i].Equals(other.Samplers[i])) return false;
        }
        for (int i = 0; i < Buffers.Length; i++)
        {
            if (!Buffers[i].Equals(other.Buffers[i])) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as DescriptorSetContents);
    public override int GetHashCode() => _hash;
}

/// <summary>
/// Hands out descriptor sets, reusing them whenever the same bindings come back.
///
/// This is the single largest CPU win available in a Vulkan backend for a game
/// like this one. Chunk rendering binds the same terrain atlas thousands of times
/// per frame; without a cache each of those is a set allocation and a write, and
/// with one they are a dictionary lookup. Published measurements put descriptor
/// caching at roughly a third off frame time in CPU-heavy scenes.
///
/// Sets are immutable once written, so a set can outlive any number of frames
/// safely - as long as the resources it names do. A set that names a deleted
/// texture is the one thing this cache must never serve again: the driver may
/// give the next texture the same view handle, and a lookup by handle would then
/// hand a draw a set pointing at freed memory. That is why the key carries each
/// resource's lifetime id and why a deleted resource evicts its sets, with the
/// actual free deferred until no frame can still be reading them.
///
/// The working set is bounded by how many distinct texture combinations the
/// game actually uses at once - a few hundred, not a few hundred thousand - and
/// eviction keeps churn, such as the GUI's re-rendered text, from growing it.
/// </summary>
internal sealed unsafe class DescriptorCache : IDisposable
{
    private const uint SetsPerPool = 512;

    /// <summary>A pool and how many sets it can still hand out.</summary>
    private sealed class PoolSlot
    {
        public DescriptorPool Pool;
        public uint Remaining;
    }

    private readonly record struct CachedSet(DescriptorSet Set, PoolSlot Pool);

    private readonly VulkanContext _context;
    private readonly Dictionary<DescriptorSetContents, CachedSet> _sets = new();
    private readonly List<PoolSlot> _pools = new();
    private PoolSlot? _current;

    /// <summary>Every cached key that names a given resource, for eviction.</summary>
    private readonly Dictionary<ulong, List<DescriptorSetContents>> _byResource = new();

    /// <summary>Resources deleted since the last collection. Any thread may add.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<ulong> _pendingReleases = new();

    private bool _disposed;

    public int Count => _sets.Count;
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    public DescriptorCache(VulkanContext context) => _context = context;

    public DescriptorSet Get(DescriptorSetContents contents, DescriptorSetLayout layout)
    {
        if (_sets.TryGetValue(contents, out CachedSet existing))
        {
            Hits++;
            return existing.Set;
        }

        Misses++;
        CachedSet cached = Allocate(layout);
        Write(cached.Set, contents);
        _sets[contents] = cached;
        Index(contents);
        return cached.Set;
    }

    /// <summary>
    /// Notes that a resource is going away, so no set naming it is handed out
    /// again. Safe from any thread; the sets themselves are reclaimed on the
    /// render thread by <see cref="CollectReleases" />.
    /// </summary>
    public void Release(ulong resource)
    {
        if (resource != 0) _pendingReleases.Enqueue(resource);
    }

    /// <summary>
    /// Drops every set that names a released resource and returns the work of
    /// freeing them, or null when there is none.
    ///
    /// The sets leave the dictionary here, so no draw recorded from now on can
    /// bind them. A frame still executing may be reading one, though, so the
    /// caller hands the result to the frame ring and the free itself happens
    /// once that frame's fence has signalled. Render thread only.
    /// </summary>
    public IDisposable? CollectReleases()
    {
        List<CachedSet>? doomed = null;

        while (_pendingReleases.TryDequeue(out ulong resource))
        {
            if (!_byResource.Remove(resource, out List<DescriptorSetContents>? keys)) continue;

            foreach (DescriptorSetContents key in keys)
            {
                // A set naming the resource twice is listed twice, and the
                // second removal simply finds nothing.
                if (!_sets.Remove(key, out CachedSet cached)) continue;

                Unindex(key, resource);
                (doomed ??= new List<CachedSet>()).Add(cached);
            }
        }

        return doomed == null ? null : new FreedSets(this, doomed);
    }

    private void Index(DescriptorSetContents contents)
    {
        foreach (SamplerBindingValue sampler in contents.Samplers) IndexResource(sampler.Resource, contents);
        foreach (BufferBindingValue buffer in contents.Buffers) IndexResource(buffer.Resource, contents);
    }

    private void IndexResource(ulong resource, DescriptorSetContents contents)
    {
        if (resource == 0) return;

        if (!_byResource.TryGetValue(resource, out List<DescriptorSetContents>? keys))
        {
            keys = new List<DescriptorSetContents>(1);
            _byResource[resource] = keys;
        }
        keys.Add(contents);
    }

    /// <summary>
    /// Removes a key from the lists of every other resource it names, so a
    /// long-lived resource does not accumulate keys evicted on account of the
    /// short-lived ones sampled alongside it.
    /// </summary>
    private void Unindex(DescriptorSetContents key, ulong except)
    {
        foreach (SamplerBindingValue sampler in key.Samplers) UnindexResource(sampler.Resource, except, key);
        foreach (BufferBindingValue buffer in key.Buffers) UnindexResource(buffer.Resource, except, key);
    }

    private void UnindexResource(ulong resource, ulong except, DescriptorSetContents key)
    {
        if (resource == 0 || resource == except) return;
        if (!_byResource.TryGetValue(resource, out List<DescriptorSetContents>? keys)) return;

        keys.Remove(key);
        if (keys.Count == 0) _byResource.Remove(resource);
    }

    /// <summary>Frees a batch of sets back to their pools, once it is safe to.</summary>
    private sealed class FreedSets : IDisposable
    {
        private readonly DescriptorCache _cache;
        private readonly List<CachedSet> _sets;

        public FreedSets(DescriptorCache cache, List<CachedSet> sets)
        {
            _cache = cache;
            _sets = sets;
        }

        public void Dispose() => _cache.Free(_sets);
    }

    private void Free(List<CachedSet> sets)
    {
        // The ring can drain after the cache is gone, and the pools with it.
        if (_disposed) return;

        foreach (CachedSet cached in sets)
        {
            DescriptorSet set = cached.Set;
            _context.Api.FreeDescriptorSets(_context.Device, cached.Pool.Pool, 1, &set);
            cached.Pool.Remaining++;
        }
    }

    private CachedSet Allocate(DescriptorSetLayout layout)
    {
        // Freed sets hand capacity back to whichever pool they came from, so
        // any pool with room will do, not only the newest.
        PoolSlot? slot = _current is { Remaining: > 0 } ? _current : null;
        if (slot == null)
        {
            foreach (PoolSlot candidate in _pools)
            {
                if (candidate.Remaining > 0)
                {
                    slot = candidate;
                    break;
                }
            }
        }
        slot ??= GrowPool();

        Result result = AllocateFrom(slot, layout, out DescriptorSet set);

        // A pool can fail before its nominal capacity when one layout uses more
        // of a type than the pool budgeted, or when frees have fragmented it.
        // Growing and retrying once is the documented way to handle that; the
        // pool that refused is written off rather than asked again every miss.
        if (result != Result.Success)
        {
            slot.Remaining = 0;
            slot = GrowPool();
            result = AllocateFrom(slot, layout, out set);
            if (result != Result.Success)
            {
                throw new InvalidOperationException("vkAllocateDescriptorSets failed: " + result);
            }
        }

        slot.Remaining--;
        _current = slot;
        return new CachedSet(set, slot);
    }

    private Result AllocateFrom(PoolSlot slot, DescriptorSetLayout layout, out DescriptorSet set)
    {
        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = slot.Pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };

        DescriptorSet allocated;
        Result result = _context.Api.AllocateDescriptorSets(_context.Device, &allocateInfo, &allocated);
        set = allocated;
        return result;
    }

    private PoolSlot GrowPool()
    {
        // A pool can only satisfy the descriptor types it was sized for. The
        // generated block is a dynamic uniform buffer, but the game also declares
        // uniform blocks of its own - entityanimated's ElementTransforms is one -
        // and those are plain uniform buffers. Without a size for that type the
        // allocation fails, the set is never written, and the first draw that
        // uses it takes the device down.
        var sizes = stackalloc DescriptorPoolSize[4]
        {
            new DescriptorPoolSize(DescriptorType.UniformBufferDynamic, SetsPerPool),
            new DescriptorPoolSize(DescriptorType.UniformBuffer, SetsPerPool * 2),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, SetsPerPool * 8),
            new DescriptorPoolSize(DescriptorType.StorageBuffer, SetsPerPool * 2),
        };

        var createInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            // Evicted sets are freed individually, which a pool has to allow.
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            PoolSizeCount = 4,
            PPoolSizes = sizes,
            MaxSets = SetsPerPool,
        };

        if (_context.Api.CreateDescriptorPool(_context.Device, &createInfo, null, out DescriptorPool pool)
            != Result.Success)
        {
            throw new InvalidOperationException("vkCreateDescriptorPool failed");
        }

        var slot = new PoolSlot { Pool = pool, Remaining = SetsPerPool };
        _pools.Add(slot);
        _current = slot;
        return slot;
    }

    private void Write(DescriptorSet set, DescriptorSetContents contents)
    {
        int writeCount = contents.Samplers.Length + contents.Buffers.Length;
        if (writeCount == 0) return;

        var writes = new WriteDescriptorSet[writeCount];
        var imageInfos = new DescriptorImageInfo[contents.Samplers.Length];
        var bufferInfos = new DescriptorBufferInfo[contents.Buffers.Length];

        fixed (DescriptorImageInfo* imagePtr = imageInfos)
        fixed (DescriptorBufferInfo* bufferPtr = bufferInfos)
        {
            int index = 0;

            for (int i = 0; i < contents.Samplers.Length; i++)
            {
                SamplerBindingValue sampler = contents.Samplers[i];
                imageInfos[i] = new DescriptorImageInfo
                {
                    ImageView = sampler.View,
                    Sampler = sampler.Sampler,
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                };
                writes[index++] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = sampler.Binding,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = imagePtr + i,
                };
            }

            for (int i = 0; i < contents.Buffers.Length; i++)
            {
                BufferBindingValue buffer = contents.Buffers[i];
                bufferInfos[i] = new DescriptorBufferInfo
                {
                    Buffer = buffer.Buffer,
                    Offset = buffer.Offset,
                    Range = buffer.Range,
                };

                // Set 0 binding 0 is the generated uniform block, bound as a
                // dynamic descriptor so the per-draw ring offset travels
                // separately and the set itself never has to change.
                bool isDynamicUniform =
                    contents.SetIndex == ProgramInterfaceLayoutBindings.DefaultBlockSet
                    && buffer.Binding == ProgramInterfaceLayoutBindings.DefaultBlockBinding;

                writes[index++] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = buffer.Binding,
                    DescriptorCount = 1,
                    DescriptorType = contents.SetIndex == ProgramInterfaceLayoutBindings.StorageSet
                        ? DescriptorType.StorageBuffer
                        : isDynamicUniform
                            ? DescriptorType.UniformBufferDynamic
                            : DescriptorType.UniformBuffer,
                    PBufferInfo = bufferPtr + i,
                };
            }

            fixed (WriteDescriptorSet* writesPtr = writes)
            {
                _context.Api.UpdateDescriptorSets(_context.Device, (uint)writeCount, writesPtr, 0, null);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sets.Clear();
        _byResource.Clear();
        foreach (PoolSlot slot in _pools)
        {
            _context.Api.DestroyDescriptorPool(_context.Device, slot.Pool, null);
        }
        _pools.Clear();
    }
}

/// <summary>
/// The set and binding numbers the shader rewriter assigns.
///
/// Duplicated here as plain constants so the descriptor layer does not depend on
/// the shader translation types; the pair is checked against each other by test.
/// </summary>
internal static class ProgramInterfaceLayoutBindings
{
    public const int DefaultBlockSet = 0;
    public const int DefaultBlockBinding = 0;
    public const int SamplerSet = 1;
    public const int StorageSet = 2;
}
