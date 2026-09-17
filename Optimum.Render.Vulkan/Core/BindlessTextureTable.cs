using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Set 1 of plan decision 9: one descriptor set of combined-image-sampler arrays,
/// one per GLSL sampled type (<see cref="TextureKind" />), every binding
/// PARTIALLY_BOUND | UPDATE_AFTER_BIND, allocated once from its own
/// update-after-bind pool. Shaders index the arrays with slot numbers from push
/// constants.
///
/// A slot holds one physical texture under one effective sampler state and
/// layout (<see cref="BindlessSlotKey" />); <see cref="BindlessSlotBook" /> decides
/// which. Slot 0 of every array is that kind's placeholder, and every other slot
/// holds the placeholder until it is allocated and again once it is freed, so a
/// stale or out-of-date index samples a defined value instead of undefined memory:
/// opaque black for colour kinds, as OpenGL reads an unbound texture (magenta under
/// poison mode, where an undefined read is meant to be loud), a far-plane depth for
/// shadow kinds, a fixed texel for integer kinds.
///
/// Writes are queued and applied by <see cref="Flush" /> in one
/// vkUpdateDescriptorSets: at frame start (<see cref="BeginFrame" />, which first
/// writes placeholders back into slots whose retirement completed) and before
/// every submission of the frame, so a slot first resolved while recording is
/// written before the command buffer naming it is submitted. Update-after-bind
/// makes both legal while the set is bound (docs/research/vulkan-bindless.md,
/// sections 2 and 3). Render thread, except <see cref="Release" />.
/// </summary>
internal sealed unsafe class BindlessTextureTable : IDisposable
{
    /// <summary>
    /// Placeholder colour for colour arrays: opaque black, what OpenGL samples from an unbound
    /// texture and what the per-program placeholders read before the shared layout.
    /// </summary>
    private static readonly byte[] OpaqueBlack = { 0, 0, 0, 255 };

    /// <summary>The colour placeholder under poison mode, where an undefined read is meant to be loud.</summary>
    private static readonly byte[] Magenta = { 255, 0, 255, 255 };

    private readonly record struct PendingWrite(TextureKind Kind, uint Slot, ImageView View, Sampler Sampler, ImageLayout Layout);

    private readonly VulkanContext _context;
    private readonly TextureManager _textures;
    private readonly BindlessSlotBook _book;
    private readonly object _lock = new();
    private readonly int[] _placeholders = new int[BindlessKinds.Count];
    private readonly List<PendingWrite> _pending = new();
    // (kind, slot) -> index in _pending: a later write to the same slot replaces the earlier one.
    private readonly Dictionary<(TextureKind, uint), int> _pendingIndex = new();
    private readonly List<(TextureKind Kind, uint Slot)> _freed = new();
    private DescriptorPool _pool;
    private DescriptorSetLayout _layout;
    private bool _disposed;

    public DescriptorSetLayout Layout => _layout;

    /// <summary>The one set. Bound at <see cref="Shaders.SetConvention.TextureSet" />.</summary>
    public DescriptorSet Set { get; }

    /// <summary>Lookups that resolved to a placeholder slot.</summary>
    public long PlaceholderResolutions { get; private set; }

    /// <summary>Slot writes applied since creation (the initial placeholder fill excluded).</summary>
    public long WritesFlushed { get; private set; }

    /// <summary>Writes applied by the most recent <see cref="Flush" /> that had any.</summary>
    public int LastFlushWrites { get; private set; }

    public BindlessTextureTable(VulkanContext context, TextureManager textures, ITimelineClock clock)
    {
        _context = context;
        _textures = textures;
        uint[] capacities = BindlessKinds.ClampCapacities(context.Capabilities.DescriptorIndexing,
            DescriptorIndexingFloor.FrameTextures);
        _book = new BindlessSlotBook(clock, capacities);

        try
        {
            _layout = CreateSetLayout(context, capacities);
            _pool = CreatePool(capacities);
            Set = AllocateSet();
            CreatePlaceholders();
            FillWithPlaceholders(capacities);
        }
        catch
        {
            DestroyObjects();
            throw;
        }
    }

    public uint CapacityOf(TextureKind kind) => _book.CapacityOf(kind);

    public int LiveSlots(TextureKind kind)
    {
        lock (_lock) return _book.LiveSlots(kind);
    }

    public int PendingRetirements
    {
        get { lock (_lock) return _book.PendingRetirements; }
    }

    public int PendingWrites
    {
        get { lock (_lock) return _pending.Count; }
    }

    /// <summary>The texture id of a kind's placeholder. Tests and diagnostics.</summary>
    public int PlaceholderTextureId(TextureKind kind) => _placeholders[(int)kind];

    /// <summary><see cref="Resolve(VulkanTexture?, TextureKind, SamplerState, ImageLayout)" /> for what a texture id resolves to now, aliasing included.</summary>
    public uint Resolve(int textureId, TextureKind kind, SamplerState state,
        ImageLayout layout = ImageLayout.ShaderReadOnlyOptimal) =>
        Resolve(_textures.Get(textureId), kind, state, layout);

    /// <summary>
    /// The slot a shader samples <paramref name="texture" /> through as
    /// <paramref name="kind" /> with <paramref name="state" /> in <paramref name="layout" />.
    /// A new slot's write is queued; the caller records draws with the index and the
    /// write lands before their submission. 0 (the placeholder) for no texture, a
    /// texture that cannot sit behind the kind, or a full array.
    /// </summary>
    public uint Resolve(VulkanTexture? texture, TextureKind kind, SamplerState state,
        ImageLayout layout = ImageLayout.ShaderReadOnlyOptimal)
    {
        if (texture == null || !BindlessKinds.Suits(TextureShape.Of(texture), kind))
        {
            NotePlaceholder();
            return 0;
        }

        SamplerState effective = BindlessKinds.EffectiveState(state, kind);
        lock (_lock)
        {
            // Read under the lock Release takes: a delete that set the flag after this
            // read blocks in Release until the slot below exists, and then retires it.
            if (texture.Released)
            {
                NotePlaceholder();
                return 0;
            }
            uint slot = _book.Acquire(new BindlessSlotKey(texture.Id, kind, effective, layout), out bool created);
            if (slot == 0)
            {
                NotePlaceholder();
                return 0;
            }
            if (created) Queue(new PendingWrite(kind, slot, texture.View, _textures.Samplers.Get(effective), layout));
            return slot;
        }
    }

    private void NotePlaceholder()
    {
        lock (_lock) PlaceholderResolutions++;
        VulkanStats.NoteBindlessPlaceholderResolution();
    }

    /// <summary>
    /// Retires every slot of a deleted physical texture against the Frame value
    /// recorded now. Any thread (<see cref="TextureManager.Deleted" />).
    /// </summary>
    public void Release(ulong textureId)
    {
        lock (_lock) _book.Release(textureId);
    }

    /// <summary>
    /// Frame start, after the ring's wait and collection: slots whose retirement
    /// completed get their placeholder back and return to the free lists, then
    /// every queued write is applied.
    /// </summary>
    public int BeginFrame()
    {
        lock (_lock)
        {
            _freed.Clear();
            _book.Collect(_freed);
            foreach ((TextureKind kind, uint slot) in _freed) QueuePlaceholder(kind, slot);
            return Flush();
        }
    }

    /// <summary>Applies every queued write in one vkUpdateDescriptorSets. Returns how many.</summary>
    public int Flush()
    {
        lock (_lock)
        {
            int count = _pending.Count;
            if (count == 0) return 0;

            var images = new DescriptorImageInfo[count];
            var writes = new WriteDescriptorSet[count];
            fixed (DescriptorImageInfo* imagesPtr = images)
            fixed (WriteDescriptorSet* writesPtr = writes)
            {
                for (int i = 0; i < count; i++)
                {
                    PendingWrite write = _pending[i];
                    imagesPtr[i] = new DescriptorImageInfo(write.Sampler, write.View, write.Layout);
                    writesPtr[i] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = Set,
                        DstBinding = BindlessKinds.BindingOf(write.Kind),
                        DstArrayElement = write.Slot,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.CombinedImageSampler,
                        PImageInfo = imagesPtr + i,
                    };
                }
                _context.Api.UpdateDescriptorSets(_context.Device, (uint)count, writesPtr, 0, null);
            }

            _pending.Clear();
            _pendingIndex.Clear();
            WritesFlushed += count;
            LastFlushWrites = count;
            VulkanStats.NoteBindlessFlush(count);
            return count;
        }
    }

    private void Queue(in PendingWrite write)
    {
        if (_pendingIndex.TryGetValue((write.Kind, write.Slot), out int index))
        {
            _pending[index] = write;
            return;
        }
        _pendingIndex.Add((write.Kind, write.Slot), _pending.Count);
        _pending.Add(write);
    }

    private void QueuePlaceholder(TextureKind kind, uint slot)
    {
        (ImageView view, Sampler sampler) = PlaceholderDescriptor(kind);
        Queue(new PendingWrite(kind, slot, view, sampler, ImageLayout.ShaderReadOnlyOptimal));
    }

    private (ImageView View, Sampler Sampler) PlaceholderDescriptor(TextureKind kind)
    {
        VulkanTexture placeholder = _textures.Get(_placeholders[(int)kind])
            ?? throw new InvalidOperationException("bindless placeholder for " + kind + " is gone");
        return (placeholder.View, _textures.Samplers.Get(BindlessKinds.EffectiveState(SamplerState.Default, kind)));
    }

    // ---------------------------------------------------------------- creation

    /// <summary>Set 1's layout for the given per-kind capacities. The table's own, and a standalone shared layout's.</summary>
    internal static DescriptorSetLayout CreateSetLayout(VulkanContext context, uint[] capacities)
    {
        var bindings = new DescriptorSetLayoutBinding[BindlessKinds.Count];
        var flags = new DescriptorBindingFlags[BindlessKinds.Count];
        for (int i = 0; i < bindings.Length; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = BindlessKinds.BindingOf((TextureKind)i),
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = capacities[i],
                StageFlags = SharedPipelineLayout.Stages,
            };
            flags[i] = DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit;
        }

        fixed (DescriptorSetLayoutBinding* bindingsPtr = bindings)
        fixed (DescriptorBindingFlags* flagsPtr = flags)
        {
            var bindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = (uint)flags.Length,
                PBindingFlags = flagsPtr,
            };
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                PNext = &bindingFlags,
                Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingsPtr,
            };
            DescriptorSetLayout layout;
            VulkanResult.Check(context.Api.CreateDescriptorSetLayout(context.Device, &info, null, &layout),
                "vkCreateDescriptorSetLayout for the bindless texture set");
            return layout;
        }
    }

    private DescriptorPool CreatePool(uint[] capacities)
    {
        uint total = 0;
        foreach (uint capacity in capacities) total += capacity;
        var size = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, total);
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };
        DescriptorPool pool;
        VulkanResult.Check(_context.Api.CreateDescriptorPool(_context.Device, &info, null, &pool),
            "vkCreateDescriptorPool for the bindless texture set");
        return pool;
    }

    private DescriptorSet AllocateSet()
    {
        DescriptorSetLayout layout = _layout;
        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        DescriptorSet set;
        VulkanResult.Check(_context.Api.AllocateDescriptorSets(_context.Device, &info, &set),
            "vkAllocateDescriptorSets for the bindless texture set");
        return set;
    }

    /// <summary>
    /// One-texel textures of each kind's view type and format class. Their pixels
    /// ride the upload batch, which runs before the first frame that can sample them.
    /// </summary>
    private void CreatePlaceholders()
    {
        fixed (byte* magenta = _context.PoisonFreshResources ? Magenta : OpaqueBlack)
        {
            _placeholders[(int)TextureKind.Texture2D] = Colour(_textures.Create(1, 1, Format.R8G8B8A8Unorm), 1, magenta);
            // A single-layer texture gets a 2D view; an array view needs two layers.
            _placeholders[(int)TextureKind.Texture2DArray] =
                Colour(_textures.Create(1, 1, Format.R8G8B8A8Unorm, layers: 2), 2, magenta);
            _placeholders[(int)TextureKind.TextureCube] =
                Colour(_textures.Create(1, 1, Format.R8G8B8A8Unorm, layers: 6, cube: true), 6, magenta);
            _placeholders[(int)TextureKind.Texture3D] = Colour(_textures.CreateVolume(1, 1, 1, Format.R8G8B8A8Unorm), 1, magenta);
            _placeholders[(int)TextureKind.UnsignedTexture2D] = Colour(_textures.Create(1, 1, Format.R8G8B8A8Uint), 1, magenta);
        }

        byte[] signed = { 127, 0, 127, 127 };
        fixed (byte* texel = signed)
        {
            _placeholders[(int)TextureKind.SignedTexture2D] = Colour(_textures.Create(1, 1, Format.R8G8B8A8Sint), 1, texel);
        }

        // Depth at the far plane: every comparison against it passes, so nothing is
        // shadowed, as with a missing shadow map on GL.
        _placeholders[(int)TextureKind.Shadow2D] = Depth(_textures.Create(1, 1, Format.D32Sfloat), 1);
        _placeholders[(int)TextureKind.Shadow2DArray] = Depth(_textures.Create(1, 1, Format.D32Sfloat, layers: 2), 2);
        _placeholders[(int)TextureKind.ShadowCube] = Depth(_textures.Create(1, 1, Format.D32Sfloat, layers: 6, cube: true), 6);

        for (int i = 0; i < BindlessKinds.Count; i++)
        {
            VulkanTexture placeholder = _textures.Get(_placeholders[i])!;
            if (!BindlessKinds.Suits(TextureShape.Of(placeholder), (TextureKind)i))
            {
                throw new InvalidOperationException("bindless placeholder does not suit " + (TextureKind)i);
            }
        }
    }

    private int Colour(int id, uint layers, byte* texel)
    {
        for (uint layer = 0; layer < layers; layer++) _textures.Upload(id, 0, 0, 0, 1, 1, (IntPtr)texel, 4, layer);
        return id;
    }

    private int Depth(int id, uint layers)
    {
        float far = 1f;
        for (uint layer = 0; layer < layers; layer++) _textures.Upload(id, 0, 0, 0, 1, 1, (IntPtr)(&far), 4, layer);
        VulkanTexture texture = _textures.Get(id)!;
        texture.State = texture.State with { CompareEnable = true };
        return id;
    }

    /// <summary>Writes each binding's placeholder into every element, one descriptor write per binding.</summary>
    private void FillWithPlaceholders(uint[] capacities)
    {
        var infos = new DescriptorImageInfo[BindlessKinds.Count][];
        var handles = new System.Runtime.InteropServices.GCHandle[BindlessKinds.Count];
        var writes = new WriteDescriptorSet[BindlessKinds.Count];
        try
        {
            for (int i = 0; i < BindlessKinds.Count; i++)
            {
                (ImageView view, Sampler sampler) = PlaceholderDescriptor((TextureKind)i);
                infos[i] = new DescriptorImageInfo[capacities[i]];
                Array.Fill(infos[i], new DescriptorImageInfo(sampler, view, ImageLayout.ShaderReadOnlyOptimal));
                handles[i] = System.Runtime.InteropServices.GCHandle.Alloc(infos[i],
                    System.Runtime.InteropServices.GCHandleType.Pinned);
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = Set,
                    DstBinding = BindlessKinds.BindingOf((TextureKind)i),
                    DstArrayElement = 0,
                    DescriptorCount = capacities[i],
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = (DescriptorImageInfo*)handles[i].AddrOfPinnedObject(),
                };
            }
            fixed (WriteDescriptorSet* writesPtr = writes)
            {
                _context.Api.UpdateDescriptorSets(_context.Device, (uint)writes.Length, writesPtr, 0, null);
            }
        }
        finally
        {
            foreach (System.Runtime.InteropServices.GCHandle handle in handles)
            {
                if (handle.IsAllocated) handle.Free();
            }
        }
    }

    private void DestroyObjects()
    {
        Vk api = _context.Api;
        // Destroying the pool frees the set. The placeholders belong to the texture manager.
        if (_pool.Handle != 0) api.DestroyDescriptorPool(_context.Device, _pool, null);
        if (_layout.Handle != 0) api.DestroyDescriptorSetLayout(_context.Device, _layout, null);
        _pool = default;
        _layout = default;
    }

    /// <summary>Teardown, after the device-idle wait and after every pipeline layout naming <see cref="Layout" />.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyObjects();
    }
}
