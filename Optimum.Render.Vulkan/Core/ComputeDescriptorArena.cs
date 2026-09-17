using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>One image descriptor of a compute pass set.</summary>
internal readonly record struct ComputeImageWrite(uint Binding, DescriptorType Type, ImageView View, Sampler Sampler,
    ImageLayout Layout);

/// <summary>
/// One frame slot's descriptor sets for compute passes, reset wholesale when the slot
/// begins its next frame (after the Frame timeline says its previous frame finished).
///
/// A compute pass set names per-level views whose layouts change from pass to pass
/// (storage in one, sampled in the next), so the sets live exactly one frame, like
/// <see cref="DescriptorArena" />'s; the pools carry the storage image type the
/// draw pools do not.
/// </summary>
internal sealed unsafe class ComputeDescriptorArena : IDisposable
{
    public const uint SetsPerPool = 64;
    public const uint ImagesPerSet = 8;

    private readonly VulkanContext _context;
    private readonly List<DescriptorPool> _pools = new();
    private int _poolIndex;
    private bool _disposed;

    public ComputeDescriptorArena(VulkanContext context) => _context = context;

    public int PoolCount => _pools.Count;
    public long Allocations { get; private set; }

    public void Reset()
    {
        foreach (DescriptorPool pool in _pools) _context.Api.ResetDescriptorPool(_context.Device, pool, 0);
        _poolIndex = 0;
    }

    /// <summary>Allocates a set of <paramref name="layout" /> and writes <paramref name="writes" /> into it.</summary>
    public DescriptorSet Get(DescriptorSetLayout layout, ReadOnlySpan<ComputeImageWrite> writes)
    {
        DescriptorSet set = Allocate(layout);
        Allocations++;
        if (writes.Length == 0) return set;

        var images = new DescriptorImageInfo[writes.Length];
        var descriptorWrites = new WriteDescriptorSet[writes.Length];
        fixed (DescriptorImageInfo* imagesPtr = images)
        fixed (WriteDescriptorSet* writesPtr = descriptorWrites)
        {
            for (int i = 0; i < writes.Length; i++)
            {
                ComputeImageWrite write = writes[i];
                images[i] = new DescriptorImageInfo
                {
                    ImageView = write.View,
                    Sampler = write.Type == DescriptorType.CombinedImageSampler ? write.Sampler : default,
                    ImageLayout = write.Layout,
                };
                descriptorWrites[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = write.Binding,
                    DescriptorCount = 1,
                    DescriptorType = write.Type,
                    PImageInfo = imagesPtr + i,
                };
            }
            _context.Api.UpdateDescriptorSets(_context.Device, (uint)writes.Length, writesPtr, 0, null);
        }
        return set;
    }

    private DescriptorSet Allocate(DescriptorSetLayout layout)
    {
        while (true)
        {
            bool freshPool = _poolIndex == _pools.Count;
            if (freshPool) _pools.Add(CreatePool());

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
                throw new InvalidOperationException("vkAllocateDescriptorSets failed in the compute arena: " + result);
            }
            if (freshPool)
            {
                throw new InvalidOperationException("a compute descriptor set does not fit an empty arena pool: " + result);
            }
            _poolIndex++;
        }
    }

    private DescriptorPool CreatePool()
    {
        var sizes = stackalloc DescriptorPoolSize[2]
        {
            new DescriptorPoolSize(DescriptorType.StorageImage, SetsPerPool * ImagesPerSet),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, SetsPerPool * ImagesPerSet),
        };
        var createInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
            MaxSets = SetsPerPool,
        };
        DescriptorPool pool;
        VulkanResult.Check(_context.Api.CreateDescriptorPool(_context.Device, &createInfo, null, &pool),
            "vkCreateDescriptorPool for the compute arena");
        return pool;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (DescriptorPool pool in _pools) _context.Api.DestroyDescriptorPool(_context.Device, pool, null);
        _pools.Clear();
    }
}
