using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using Optimum.Render.Vulkan.Shaders;

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
/// makes both legal while the set is bound (docs/vulkan.md#bindless-descriptors,
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

/// <summary>
/// The GLSL sampled type a bindless slot serves: one set-1 array per kind, in the
/// order of <see cref="SetConvention.TextureArrays" /> (the enum value is the index
/// into that table, not necessarily the binding number; see <see cref="BindlessKinds.BindingOf" />).
/// </summary>
internal enum TextureKind
{
    Texture2D = 0,
    Texture2DArray = 1,
    TextureCube = 2,
    Texture3D = 3,
    UnsignedTexture2D = 4,
    SignedTexture2D = 5,
    Shadow2D = 6,
    Shadow2DArray = 7,
    ShadowCube = 8,
}

/// <summary>What of a texture decides which kinds it may sit behind.</summary>
internal readonly record struct TextureShape(Format Format, uint Layers, bool Cube, bool Volume)
{
    public static TextureShape Of(VulkanTexture texture) =>
        new(texture.Format, texture.Layers, texture.Cube, texture.Volume);
}

/// <summary>
/// The rules that keep a descriptor legal for the array it is written into.
/// The validation layers check none of them for a partially bound array: a view
/// type that does not match the declaration, a Dref sample through a sampler with
/// compareEnable off (or the reverse), or an integer format behind a float sampler
/// all give undefined or poison texels with no message (docs/vulkan.md#bindless-descriptors,
/// section 1, "Hard rules"). They are enforced here, when a slot is created.
/// </summary>
internal static class BindlessKinds
{
    public const int Count = 9;

    private static readonly Dictionary<string, TextureKind> ByGlslType = BuildGlslTypes();

    private static Dictionary<string, TextureKind> BuildGlslTypes()
    {
        var types = new Dictionary<string, TextureKind>(StringComparer.Ordinal);
        for (int i = 0; i < SetConvention.TextureArrays.Length; i++)
        {
            types.Add(SetConvention.TextureArrays[i].GlslType, (TextureKind)i);
        }
        return types;
    }

    /// <summary>The kind whose array a GLSL sampler of <paramref name="glslType" /> reads; false for types set 1 has no array for.</summary>
    public static bool TryFromGlslType(string glslType, out TextureKind kind) => ByGlslType.TryGetValue(glslType, out kind);

    /// <summary>The set-1 binding number of the kind's array.</summary>
    public static uint BindingOf(TextureKind kind) => (uint)SetConvention.TextureArrays[(int)kind].Value;

    /// <summary>The convention's starting size of the kind's array.</summary>
    public static uint ConventionCapacity(TextureKind kind) => SetConvention.TextureArrays[(int)kind].Capacity;

    public static bool IsShadow(TextureKind kind) =>
        kind is TextureKind.Shadow2D or TextureKind.Shadow2DArray or TextureKind.ShadowCube;

    public static bool IsInteger(TextureKind kind) =>
        kind is TextureKind.UnsignedTexture2D or TextureKind.SignedTexture2D;

    /// <summary>
    /// Whether a texture of <paramref name="shape" /> can legally sit behind
    /// <paramref name="kind" />. The dimensionality rules are the ones
    /// the draw path applies to every sampler (a GL texture target
    /// cannot change): 2D kinds need one layer, array kinds more than one, cube
    /// kinds a cube, 3D a volume. Shadow kinds need a depth format, integer kinds
    /// the matching signedness, and float kinds a non-integer format (depth reads
    /// through <c>sampler2D</c> as it does on GL).
    /// </summary>
    public static bool Suits(TextureShape shape, TextureKind kind)
    {
        bool plain = !shape.Cube && !shape.Volume;
        bool dimensions = kind switch
        {
            TextureKind.Texture2D or TextureKind.UnsignedTexture2D or TextureKind.SignedTexture2D
                or TextureKind.Shadow2D => plain && shape.Layers == 1,
            TextureKind.Texture2DArray or TextureKind.Shadow2DArray => plain && shape.Layers > 1,
            TextureKind.TextureCube or TextureKind.ShadowCube => shape.Cube,
            TextureKind.Texture3D => shape.Volume,
            _ => false,
        };
        if (!dimensions) return false;

        string name = shape.Format.ToString();
        bool unsigned = name.Contains("Uint", StringComparison.Ordinal);
        bool signed = name.Contains("Sint", StringComparison.Ordinal);
        return kind switch
        {
            _ when IsShadow(kind) => TextureManager.IsDepthFormat(shape.Format),
            TextureKind.UnsignedTexture2D => unsigned,
            TextureKind.SignedTexture2D => signed,
            _ => !unsigned && !signed,
        };
    }

    /// <summary>
    /// The sampler state a slot of <paramref name="kind" /> is written with. GL keeps
    /// compare mode on the texture and leaves a mismatch with the sampler declaration
    /// undefined; Vulkan turns it into a poison texel the layers never report. The
    /// declaration is what the shader samples through, so it decides: shadow kinds
    /// compare, the others do not. Integer formats cannot be filtered linearly or
    /// anisotropically, so integer kinds sample nearest.
    /// </summary>
    public static SamplerState EffectiveState(SamplerState state, TextureKind kind)
    {
        state = state with { CompareEnable = IsShadow(kind) };
        if (IsInteger(kind))
        {
            state = state with
            {
                MagFilter = Filter.Nearest,
                MinFilter = Filter.Nearest,
                MipmapMode = SamplerMipmapMode.Nearest,
                MaxAnisotropy = 1f,
            };
        }
        return state;
    }

    /// <summary>
    /// Per-kind array sizes: the convention's starting sizes when the device's
    /// update-after-bind limits hold them all plus <paramref name="reservedSampledImages" />
    /// (set 0's textures, which count against the same per-stage limits); otherwise
    /// each size scaled down in proportion, never below two (the placeholder and one
    /// slot). Devices meeting <see cref="DescriptorIndexingFloor" /> keep the starting sizes.
    /// </summary>
    public static uint[] ClampCapacities(in DescriptorIndexingSupport support, uint reservedSampledImages)
    {
        ulong limit = Math.Min(Math.Min(support.MaxPerStageDescriptorUpdateAfterBindSampledImages,
                support.MaxPerStageDescriptorUpdateAfterBindSamplers),
            Math.Min(support.MaxDescriptorSetUpdateAfterBindSampledImages,
                support.MaxDescriptorSetUpdateAfterBindSamplers));
        ulong budget = limit > reservedSampledImages ? limit - reservedSampledImages : 0;

        var capacities = new uint[Count];
        ulong total = 0;
        for (int i = 0; i < Count; i++)
        {
            capacities[i] = ConventionCapacity((TextureKind)i);
            total += capacities[i];
        }
        if (total <= budget) return capacities;

        for (int i = 0; i < Count; i++)
        {
            capacities[i] = (uint)Math.Max(2UL, capacities[i] * budget / total);
        }
        return capacities;
    }
}

/// <summary>
/// The slots of one set-1 array: a LIFO free list over <c>1..Capacity-1</c>. Slot 0
/// holds the array's placeholder and is never handed out, so 0 always means
/// "nothing to sample" to a shader.
/// </summary>
internal sealed class BindlessSlotAllocator
{
    private readonly Stack<uint> _free = new();
    private uint _next = 1;

    public BindlessSlotAllocator(uint capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    public uint Capacity { get; }

    /// <summary>Slots handed out and not freed.</summary>
    public int Live { get; private set; }

    /// <summary>The most recently freed slot, else the lowest never used; false when the array is full.</summary>
    public bool TryAllocate(out uint slot)
    {
        if (_free.Count > 0)
        {
            slot = _free.Pop();
        }
        else if (_next < Capacity)
        {
            slot = _next++;
        }
        else
        {
            slot = 0;
            return false;
        }
        Live++;
        return true;
    }

    public void Free(uint slot)
    {
        if (slot == 0 || slot >= _next) throw new ArgumentOutOfRangeException(nameof(slot), slot, "not an allocated slot");
        _free.Push(slot);
        Live--;
    }
}

/// <summary>
/// What a slot holds: a combined image sampler of one physical texture
/// (<see cref="VulkanTexture.Id" />, never reused, so a GL id handed to a new texture
/// or aliased to a transient image cannot reach an old slot), under one effective
/// sampler state and image layout, in one kind's array.
/// </summary>
internal readonly record struct BindlessSlotKey(ulong TextureId, TextureKind Kind, SamplerState State, ImageLayout Layout);

/// <summary>
/// The bookkeeping of the bindless table, without a device: which key owns which
/// slot, and when a retired slot may be handed out again.
///
/// A live slot is never rewritten in place: a draw recorded against it may still
/// be executing. A texture's slot retires when the texture is deleted, or when the
/// texture has more than <see cref="MaxVariantsPerTexture" /> keys (a changed
/// glTexParameter or a unit-level sampler override each make a new key; the least
/// recently used one retires, so a texture alternating between two states does not
/// churn slots every draw). A retired slot keeps its descriptor until the Frame
/// timeline value recorded at retirement has completed; then the caller writes the
/// placeholder into it and it returns to the free list.
/// </summary>
internal sealed class BindlessSlotBook
{
    public const int MaxVariantsPerTexture = 4;

    private readonly record struct Retired(TextureKind Kind, uint Slot, ulong Frame);

    private readonly ITimelineClock _clock;
    private readonly BindlessSlotAllocator[] _allocators;
    private readonly Dictionary<BindlessSlotKey, uint> _slots = new();
    // Each texture's keys, least recently used first.
    private readonly Dictionary<ulong, List<BindlessSlotKey>> _byTexture = new();
    private readonly List<Retired> _retired = new();

    public BindlessSlotBook(ITimelineClock clock, IReadOnlyList<uint> capacities)
    {
        if (capacities.Count != BindlessKinds.Count) throw new ArgumentException("one capacity per kind", nameof(capacities));
        _clock = clock;
        _allocators = new BindlessSlotAllocator[BindlessKinds.Count];
        for (int i = 0; i < _allocators.Length; i++) _allocators[i] = new BindlessSlotAllocator(capacities[i]);
    }

    public uint CapacityOf(TextureKind kind) => _allocators[(int)kind].Capacity;

    public int LiveSlots(TextureKind kind) => _allocators[(int)kind].Live;

    /// <summary>Retired slots whose Frame value has not completed yet.</summary>
    public int PendingRetirements => _retired.Count;

    /// <summary>Acquisitions that found the kind's array full and got the placeholder.</summary>
    public long Exhausted { get; private set; }

    /// <summary>
    /// The slot holding <paramref name="key" />, allocating one when there is none
    /// (<paramref name="created" />: the caller writes the descriptor). 0 when the
    /// array is full.
    /// </summary>
    public uint Acquire(in BindlessSlotKey key, out bool created)
    {
        if (_slots.TryGetValue(key, out uint existing))
        {
            List<BindlessSlotKey> keys = _byTexture[key.TextureId];
            int index = keys.IndexOf(key);
            if (index != keys.Count - 1)
            {
                keys.RemoveAt(index);
                keys.Add(key);
            }
            created = false;
            return existing;
        }

        created = false;
        if (!_allocators[(int)key.Kind].TryAllocate(out uint slot))
        {
            Exhausted++;
            return 0;
        }

        _slots.Add(key, slot);
        if (!_byTexture.TryGetValue(key.TextureId, out List<BindlessSlotKey>? variants))
        {
            variants = new List<BindlessSlotKey>(2);
            _byTexture.Add(key.TextureId, variants);
        }
        variants.Add(key);
        if (variants.Count > MaxVariantsPerTexture)
        {
            BindlessSlotKey oldest = variants[0];
            variants.RemoveAt(0);
            Retire(oldest);
        }

        created = true;
        return slot;
    }

    /// <summary>Retires every slot of a deleted texture. Returns how many.</summary>
    public int Release(ulong textureId)
    {
        if (!_byTexture.Remove(textureId, out List<BindlessSlotKey>? keys)) return 0;
        foreach (BindlessSlotKey key in keys) Retire(key);
        return keys.Count;
    }

    private void Retire(in BindlessSlotKey key)
    {
        uint slot = _slots[key];
        _slots.Remove(key);
        // Every command that could still name the slot carries this value or an older one.
        _retired.Add(new Retired(key.Kind, slot, _clock.FrameRecorded));
    }

    /// <summary>
    /// Frees every retired slot whose Frame value has completed, appending it to
    /// <paramref name="freed" /> so the caller writes the placeholder back before the
    /// slot can be allocated and written again. Returns how many.
    /// </summary>
    public int Collect(List<(TextureKind Kind, uint Slot)> freed)
    {
        if (_retired.Count == 0) return 0;

        ulong completed = _clock.FrameCompleted;
        int kept = 0;
        int count = 0;
        for (int i = 0; i < _retired.Count; i++)
        {
            Retired entry = _retired[i];
            if (entry.Frame <= completed)
            {
                _allocators[(int)entry.Kind].Free(entry.Slot);
                freed.Add((entry.Kind, entry.Slot));
                count++;
            }
            else
            {
                _retired[kept++] = entry;
            }
        }
        _retired.RemoveRange(kept, _retired.Count - kept);
        return count;
    }
}
