using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// One frame slot's descriptor sets for short-lived resources, reset wholesale
/// when the slot begins its next frame.
///
/// <see cref="DescriptorCache" /> is the right home for a set that is bound for
/// hundreds of frames; for a set naming a texture the GUI re-creates every few
/// frames it is pure churn: a write, an index entry, an eviction and a deferred
/// individual free. Here the set lives exactly one frame. Within the frame
/// identical contents share a set, so a text texture drawn twice is written
/// once; at the slot's next frame start (after the Frame timeline says the GPU
/// finished the slot's previous frame) every pool is reset in one call each.
///
/// No eviction is needed: a resource deleted during the frame is destroyed only
/// after that frame completes, and the sets naming it die at the next reset
/// without ever being bound again.
/// </summary>
internal sealed unsafe class DescriptorArena : IDisposable
{
    public const uint SetsPerPool = 256;

    private readonly VulkanContext _context;
    private readonly List<DescriptorPool> _pools = new();
    private readonly Dictionary<DescriptorSetContents, DescriptorSet> _sets = new();
    private int _poolIndex;
    private bool _disposed;

    public DescriptorArena(VulkanContext context) => _context = context;

    /// <summary>Distinct sets handed out since the last reset.</summary>
    public int SetsThisFrame => _sets.Count;

    public int PoolCount => _pools.Count;

    /// <summary>Lookups that found a set already written this frame.</summary>
    public long Hits { get; private set; }

    /// <summary>Sets allocated and written, over the arena's life.</summary>
    public long Allocations { get; private set; }

    public long Resets { get; private set; }

    /// <summary>
    /// Returns every set to the pools. Only once no submitted command buffer that
    /// bound one can still execute: the slot's previous frame has completed.
    /// </summary>
    public void Reset()
    {
        foreach (DescriptorPool pool in _pools)
        {
            _context.Api.ResetDescriptorPool(_context.Device, pool, 0);
        }
        _sets.Clear();
        _poolIndex = 0;
        Resets++;
    }

    public DescriptorSet Get(DescriptorSetContents contents, DescriptorSetLayout layout)
    {
        if (_sets.TryGetValue(contents, out DescriptorSet existing))
        {
            Hits++;
            return existing;
        }

        DescriptorSet set = Allocate(layout);
        DescriptorCache.Write(_context, set, contents);
        _sets[contents] = set;
        Allocations++;
        return set;
    }

    private DescriptorSet Allocate(DescriptorSetLayout layout)
    {
        // Pools fill in order; a pool that refuses (out of sets, or out of one
        // descriptor type) is left for the rest of the frame and the next one tried.
        while (true)
        {
            bool freshPool = _poolIndex == _pools.Count;
            if (freshPool)
            {
                _pools.Add(DescriptorCache.CreatePool(_context, SetsPerPool, 0));
            }

            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _pools[_poolIndex],
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };

            DescriptorSet set;
            Result result = _context.Api.AllocateDescriptorSets(_context.Device, &allocateInfo, &set);
            if (result == Result.Success) return set;

            if (result != Result.ErrorOutOfPoolMemory && result != Result.ErrorFragmentedPool)
            {
                throw new InvalidOperationException("vkAllocateDescriptorSets failed in the descriptor arena: " + result);
            }

            // A pool created for this very set that still refuses would loop forever.
            if (freshPool)
            {
                throw new InvalidOperationException("a descriptor set does not fit an empty arena pool: " + result);
            }
            _poolIndex++;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sets.Clear();
        foreach (DescriptorPool pool in _pools)
        {
            _context.Api.DestroyDescriptorPool(_context.Device, pool, null);
        }
        _pools.Clear();
    }
}
