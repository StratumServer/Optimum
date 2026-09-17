using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

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
/// all give undefined or poison texels with no message (docs/research/vulkan-bindless.md,
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
