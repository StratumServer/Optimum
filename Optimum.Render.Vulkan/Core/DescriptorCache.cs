using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan.Core;

/// <summary>One combined image sampler binding.</summary>
internal readonly record struct SamplerBindingValue(uint Binding, ImageView View, Sampler Sampler);

/// <summary>One buffer binding.</summary>
internal readonly record struct BufferBindingValue(uint Binding, Buffer Buffer, ulong Offset, ulong Range);

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
        }
        foreach (BufferBindingValue buffer in buffers)
        {
            hash.Add(buffer.Binding);
            hash.Add(buffer.Buffer.Handle);
            hash.Add(buffer.Offset);
            hash.Add(buffer.Range);
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
/// Sets are allocated from pools that are never reset. They are immutable once
/// written, so a set can outlive any number of frames safely, and the working set
/// is bounded by how many distinct texture combinations the game actually uses -
/// a few hundred, not a few hundred thousand.
/// </summary>
internal sealed unsafe class DescriptorCache : IDisposable
{
    private const uint SetsPerPool = 512;

    private readonly VulkanContext _context;
    private readonly Dictionary<DescriptorSetContents, DescriptorSet> _sets = new();
    private readonly List<DescriptorPool> _pools = new();
    private DescriptorPool _current;
    private uint _remainingInCurrent;
    private bool _disposed;

    public int Count => _sets.Count;
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    public DescriptorCache(VulkanContext context) => _context = context;

    public DescriptorSet Get(DescriptorSetContents contents, DescriptorSetLayout layout)
    {
        if (_sets.TryGetValue(contents, out DescriptorSet existing))
        {
            Hits++;
            return existing;
        }

        Misses++;
        DescriptorSet set = Allocate(layout);
        Write(set, contents);
        _sets[contents] = set;
        return set;
    }

    private DescriptorSet Allocate(DescriptorSetLayout layout)
    {
        if (_remainingInCurrent == 0) GrowPool();

        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _current,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };

        DescriptorSet set;
        Result result = _context.Api.AllocateDescriptorSets(_context.Device, &allocateInfo, &set);

        // A pool can fail before its nominal capacity when one layout uses more
        // of a type than the pool budgeted. Growing and retrying once is the
        // documented way to handle that.
        if (result != Result.Success)
        {
            GrowPool();
            allocateInfo.DescriptorPool = _current;
            result = _context.Api.AllocateDescriptorSets(_context.Device, &allocateInfo, &set);
            if (result != Result.Success)
            {
                throw new InvalidOperationException("vkAllocateDescriptorSets failed: " + result);
            }
        }

        _remainingInCurrent--;
        return set;
    }

    private void GrowPool()
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
            PoolSizeCount = 4,
            PPoolSizes = sizes,
            MaxSets = SetsPerPool,
        };

        if (_context.Api.CreateDescriptorPool(_context.Device, &createInfo, null, out DescriptorPool pool)
            != Result.Success)
        {
            throw new InvalidOperationException("vkCreateDescriptorPool failed");
        }

        _pools.Add(pool);
        _current = pool;
        _remainingInCurrent = SetsPerPool;
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
        foreach (DescriptorPool pool in _pools)
        {
            _context.Api.DestroyDescriptorPool(_context.Device, pool, null);
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
