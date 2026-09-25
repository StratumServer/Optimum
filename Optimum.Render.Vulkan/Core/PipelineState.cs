using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using System.Numerics;

namespace Optimum.Render.Vulkan.Core;

/// <summary>Blend configuration for one colour attachment.</summary>
internal struct AttachmentBlend : IEquatable<AttachmentBlend>
{
    public bool Enabled;
    public BlendFactor SrcColor;
    public BlendFactor DstColor;
    public BlendOp ColorOp;
    public BlendFactor SrcAlpha;
    public BlendFactor DstAlpha;
    public BlendOp AlphaOp;
    public ColorComponentFlags WriteMask;

    public static AttachmentBlend Default => new()
    {
        Enabled = false,
        SrcColor = BlendFactor.SrcAlpha,
        DstColor = BlendFactor.OneMinusSrcAlpha,
        ColorOp = BlendOp.Add,
        SrcAlpha = BlendFactor.SrcAlpha,
        DstAlpha = BlendFactor.OneMinusSrcAlpha,
        AlphaOp = BlendOp.Add,
        WriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
            | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
    };

    /// <summary>
    /// The factor pairs one of the game's named blend modes means, which
    /// <c>ClientPlatformWindows.GlToggleBlend</c> selects and
    /// the platform's stated state (StatedRenderState) applies to every attachment.
    ///
    /// A native render system states its blend outright rather than reading the tracker's
    /// (docs/vulkan.md, decision 3), and its call site says "blend on,
    /// standard" the same way the OpenGL body does, so it builds the attachment through here
    /// instead of restating the factors and risking a pair that drifts from the table.
    /// </summary>
    public static AttachmentBlend For(bool enabled, EnumBlendMode mode)
    {
        (BlendFactor srcColor, BlendFactor dstColor, BlendFactor srcAlpha, BlendFactor dstAlpha) = FactorsFor(mode);
        AttachmentBlend blend = Default;
        blend.Enabled = enabled;
        blend.SrcColor = srcColor;
        blend.DstColor = dstColor;
        blend.ColorOp = BlendOp.Add;
        blend.SrcAlpha = srcAlpha;
        blend.DstAlpha = dstAlpha;
        blend.AlphaOp = BlendOp.Add;
        return blend;
    }

    /// <summary>
    /// World/UI separation: the blend a draw into the UI image uses in place of this one.
    ///
    /// gui.fsh writes straight alpha and the GUI draws under <see cref="EnumBlendMode.Standard" />,
    /// whose factors are not separate - (SRC_ALPHA, ONE_MINUS_SRC_ALPHA) on the alpha channel too.
    /// Onto the opaque window that is correct, which is why it always was; accumulated into an image
    /// that starts transparent it gives out_a = src_a * src_a + dst_a * (1 - src_a), roughly alpha
    /// squared per layer, instead of the over-operator's out_a = src_a + dst_a * (1 - src_a), and the
    /// compose then shows the world through every translucent panel. So Standard's exact factor set
    /// takes ONE for the source alpha factor here, and nothing else changes: the RGB factors already
    /// accumulate the premultiplied colour the compose blends back with (ONE, ONE_MINUS_SRC_ALPHA).
    ///
    /// Only Standard, deliberately. PremultipliedAlpha already has (ONE, ONE_MINUS_SRC_ALPHA) on both
    /// channels, and the destination-reading modes (Brighten, Multiply, Glow, Overlay) belong to world
    /// systems that draw before the blit, never into the UI image. Pinned by UiSeparationTests.
    /// </summary>
    public AttachmentBlend ForUiImage()
    {
        AttachmentBlend blend = this;
        if (blend.SrcColor == BlendFactor.SrcAlpha && blend.DstColor == BlendFactor.OneMinusSrcAlpha &&
            blend.SrcAlpha == BlendFactor.SrcAlpha && blend.DstAlpha == BlendFactor.OneMinusSrcAlpha)
        {
            blend.SrcAlpha = BlendFactor.One;
        }
        return blend;
    }

    /// <summary>The one table of factor pairs, shared by the stated state and by native systems.</summary>
    internal static (BlendFactor SrcColor, BlendFactor DstColor, BlendFactor SrcAlpha, BlendFactor DstAlpha)
        FactorsFor(EnumBlendMode mode) => mode switch
    {
        EnumBlendMode.Brighten => (BlendFactor.DstColor, BlendFactor.One,
            BlendFactor.DstColor, BlendFactor.One),
        EnumBlendMode.Multiply => (BlendFactor.Zero, BlendFactor.OneMinusSrcAlpha,
            BlendFactor.One, BlendFactor.OneMinusSrcAlpha),
        EnumBlendMode.PremultipliedAlpha => (BlendFactor.One, BlendFactor.OneMinusSrcAlpha,
            BlendFactor.One, BlendFactor.OneMinusSrcAlpha),
        EnumBlendMode.Glow => (BlendFactor.SrcAlpha, BlendFactor.One,
            BlendFactor.One, BlendFactor.Zero),
        EnumBlendMode.Overlay => (BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha,
            BlendFactor.One, BlendFactor.One),
        _ => (BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha,
            BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha),
    };

    /// <summary>
    /// Squeezes the whole attachment state into 32 bits so a set of eight hashes
    /// as cheaply as an array of ints. Every field is a small enum; the widest is
    /// a blend factor at 19 values.
    /// </summary>
    public readonly uint Pack()
    {
        uint packed = Enabled ? 1u : 0u;
        packed |= (uint)SrcColor << 1;
        packed |= (uint)DstColor << 6;
        packed |= (uint)ColorOp << 11;
        packed |= (uint)SrcAlpha << 14;
        packed |= (uint)DstAlpha << 19;
        packed |= (uint)AlphaOp << 24;
        packed |= (uint)WriteMask << 27;
        return packed;
    }

    public readonly bool Equals(AttachmentBlend other) => Pack() == other.Pack();
    public override readonly bool Equals(object? obj) => obj is AttachmentBlend other && Equals(other);
    public override readonly int GetHashCode() => (int)Pack();
}

/// <summary>
/// Interns a value so it can be compared as an int.
///
/// The pipeline key is looked up on every draw, so it has to be small and cheap
/// to hash. Interning the bulky parts - the blend set, the render target formats,
/// the vertex layout - turns each into one integer and leaves the key at six.
/// </summary>
internal sealed class Interner<T> where T : notnull
{
    private readonly Dictionary<T, int> _ids;
    private readonly List<T> _values = new();

    public Interner(IEqualityComparer<T>? comparer = null) => _ids = new Dictionary<T, int>(comparer);

    public int Intern(T value)
    {
        if (_ids.TryGetValue(value, out int id)) return id;

        id = _values.Count;
        _values.Add(value);
        _ids[value] = id;
        return id;
    }

    public T Get(int id) => _values[id];
    public int Count => _values.Count;
}

/// <summary>The attachment formats a pipeline renders into.</summary>
internal sealed class RenderTargetFormats : IEquatable<RenderTargetFormats>
{
    public Format[] ColorFormats { get; }
    public Format DepthFormat { get; }

    public RenderTargetFormats(Format[] colorFormats, Format depthFormat)
    {
        ColorFormats = colorFormats;
        DepthFormat = depthFormat;
    }

    public bool Equals(RenderTargetFormats? other)
    {
        if (other is null) return false;
        if (DepthFormat != other.DepthFormat) return false;
        if (ColorFormats.Length != other.ColorFormats.Length) return false;

        for (int i = 0; i < ColorFormats.Length; i++)
        {
            if (ColorFormats[i] != other.ColorFormats[i]) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as RenderTargetFormats);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DepthFormat);
        foreach (Format format in ColorFormats) hash.Add(format);
        return hash.ToHashCode();
    }
}

/// <summary>A set of per-attachment blend states, interned as a unit without heap storage.</summary>
internal readonly struct BlendSignature : IEquatable<BlendSignature>
{
    private readonly UInt128 _first;
    private readonly UInt128 _second;
    private readonly byte _count;

    public BlendSignature(ReadOnlySpan<AttachmentBlend> attachments) : this(attachments, attachments.Length) { }

    public BlendSignature(ReadOnlySpan<AttachmentBlend> attachments, int count)
    {
        if ((uint)count > RenderLimits.MaxColorAttachments)
            throw new ArgumentOutOfRangeException(nameof(count));

        _count = (byte)count;
        UInt128 first = 0;
        UInt128 second = 0;
        for (int i = 0; i < count; i++)
        {
            uint packed = (i < attachments.Length ? attachments[i] : AttachmentBlend.Default).Pack();
            if (i < 4) first |= (UInt128)packed << (i * 32);
            else second |= (UInt128)packed << ((i - 4) * 32);
        }
        _first = first;
        _second = second;
    }

    public bool Equals(BlendSignature other) =>
        _count == other._count && _first == other._first && _second == other._second;

    public override bool Equals(object? obj) => obj is BlendSignature other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_count, _first, _second);
}

/// <summary>
/// Everything a graphics pipeline is built from that Vulkan cannot change
/// dynamically.
///
/// Vulkan 1.3 makes viewport, scissor, cull mode, front face, depth test/write/
/// compare, stencil state and line width dynamic, so none of them appear here and
/// none of them cause a pipeline to be created. What is left is the shader
/// program, the vertex layout, the attachment formats, the blend set, the fill
/// mode and the topology class - and all but the last two are interned to an int.
/// </summary>
internal readonly record struct PipelineKey(
    int ProgramId,
    int VertexLayoutId,
    int TargetFormatsId,
    int BlendId,
    PolygonMode PolygonMode,
    int TopologyClass);

/// <summary>
/// The fixed limits every target and program of this renderer is built within, and the one
/// winding the game uses.
/// </summary>
internal static class RenderLimits
{
    public const int MaxColorAttachments = 8;
    public const int MaxTextureUnits = 16;

    /// <summary>
    /// The front face is a constant, not a setting. GL's counter-clockwise
    /// winding, read in a Vulkan framebuffer with no Y flip, is clockwise. The
    /// game never calls glFrontFace, so nothing varies it.
    /// </summary>
    public const FrontFace FrontFace = Silk.NET.Vulkan.FrontFace.Clockwise;

    /// <summary>Bit i set when the program statically writes fragment output i.</summary>
    public static uint OutputBits(HashSet<int> writtenOutputs)
    {
        uint bits = 0;
        for (int i = 0; i < MaxColorAttachments; i++)
        {
            if (writtenOutputs.Contains(i)) bits |= 1u << i;
        }
        return bits;
    }
}

/// <summary>One bit per dynamic-state command a draw may record.</summary>
[Flags]
internal enum DynamicStateDirty : ushort
{
    None = 0,
    Viewport = 1 << 0,
    Scissor = 1 << 1,
    CullMode = 1 << 2,
    FrontFace = 1 << 3,
    Topology = 1 << 4,
    DepthTestEnable = 1 << 5,
    DepthWriteEnable = 1 << 6,
    DepthCompareOp = 1 << 7,
    StencilTestEnable = 1 << 8,
    StencilOp = 1 << 9,
    StencilCompareMask = 1 << 10,
    StencilWriteMask = 1 << 11,
    StencilReference = 1 << 12,
    LineWidth = 1 << 13,
    /// <summary>The Vulkan 1.3 core set every pipeline declares dynamic.</summary>
    All = (1 << 14) - 1,
    /// <summary>vkCmdSetColorWriteEnableEXT or vkCmdSetColorWriteMaskEXT, per the colour write tier.</summary>
    ColorWrite = 1 << 14,
    /// <summary>vkCmdSetColorBlendEnableEXT + vkCmdSetColorBlendEquationEXT (mask tier with dynamic blend).</summary>
    ColorBlend = 1 << 15,
    /// <summary>What a fresh recording marks dirty; the device drops the bits its tier does not use.</summary>
    Everything = All | ColorWrite | ColorBlend,
}

/// <summary>The values a draw's dynamic state resolves to, already in Vulkan terms.</summary>
internal struct DynamicStateValues
{
    public Viewport Viewport;
    public Rect2D Scissor;
    public CullModeFlags CullMode;
    public FrontFace FrontFace;
    public PrimitiveTopology Topology;
    public bool DepthTest;
    public bool DepthWrite;
    public CompareOp DepthCompare;
    public bool StencilTest;
    public StencilOp StencilFail;
    public StencilOp StencilPass;
    public StencilOp StencilDepthFail;
    public CompareOp StencilCompare;
    public uint StencilCompareMask;
    public uint StencilWriteMask;
    public uint StencilReference;
    public float LineWidth;
    /// <summary>
    /// The colour write state the tier makes dynamic: enable bits (enable tier) or
    /// the effective masks packed four bits per attachment (mask tier); 0 otherwise.
    /// </summary>
    public uint ColorWrite;
    /// <summary>Interned id of the full per-attachment blend set (mask tier with dynamic blend); 0 otherwise.</summary>
    public int BlendStateId;
}

/// <summary>
/// What the command buffer being recorded already holds, so a draw emits only
/// the dynamic state that changed.
///
/// Dynamic state is command-buffer state: it survives rendering scopes and
/// pipeline binds (every pipeline declares all of these dynamic), and is
/// undefined again when a command buffer begins. The cache is keyed on the
/// recording serial of the command buffer (<see cref="FrameSlot.RecordingSerial" />),
/// so a new frame, a partial submission's continuation or a recycled handle
/// always starts from "everything dirty".
/// </summary>
internal sealed class DynamicStateCache
{
    private ulong _serial;
    private DynamicStateValues _last;

    /// <summary>False emits everything on every draw, as before masking. Tests compare the two.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Forgets what was recorded; the next draw emits everything.</summary>
    public void Invalidate() => _serial = 0;

    /// <summary>
    /// Returns the commands a draw recorded into the command buffer with
    /// <paramref name="serial" /> has to emit for <paramref name="next" />, and
    /// assumes the caller emits them. A serial of 0 is never trusted.
    /// </summary>
    public DynamicStateDirty Update(ulong serial, in DynamicStateValues next)
    {
        DynamicStateDirty dirty;
        if (!Enabled || serial == 0 || serial != _serial)
        {
            dirty = DynamicStateDirty.Everything;
        }
        else
        {
            dirty = DynamicStateDirty.None;
            if (!SameViewport(_last.Viewport, next.Viewport)) dirty |= DynamicStateDirty.Viewport;
            if (!SameRect(_last.Scissor, next.Scissor)) dirty |= DynamicStateDirty.Scissor;
            if (_last.CullMode != next.CullMode) dirty |= DynamicStateDirty.CullMode;
            if (_last.FrontFace != next.FrontFace) dirty |= DynamicStateDirty.FrontFace;
            if (_last.Topology != next.Topology) dirty |= DynamicStateDirty.Topology;
            if (_last.DepthTest != next.DepthTest) dirty |= DynamicStateDirty.DepthTestEnable;
            if (_last.DepthWrite != next.DepthWrite) dirty |= DynamicStateDirty.DepthWriteEnable;
            if (_last.DepthCompare != next.DepthCompare) dirty |= DynamicStateDirty.DepthCompareOp;
            if (_last.StencilTest != next.StencilTest) dirty |= DynamicStateDirty.StencilTestEnable;
            if (_last.StencilFail != next.StencilFail || _last.StencilPass != next.StencilPass ||
                _last.StencilDepthFail != next.StencilDepthFail || _last.StencilCompare != next.StencilCompare)
            {
                dirty |= DynamicStateDirty.StencilOp;
            }
            if (_last.StencilCompareMask != next.StencilCompareMask) dirty |= DynamicStateDirty.StencilCompareMask;
            if (_last.StencilWriteMask != next.StencilWriteMask) dirty |= DynamicStateDirty.StencilWriteMask;
            if (_last.StencilReference != next.StencilReference) dirty |= DynamicStateDirty.StencilReference;
            // Bitwise, so a NaN width is not "changed" forever.
            if (BitConverter.SingleToInt32Bits(_last.LineWidth) != BitConverter.SingleToInt32Bits(next.LineWidth))
            {
                dirty |= DynamicStateDirty.LineWidth;
            }
            if (_last.ColorWrite != next.ColorWrite) dirty |= DynamicStateDirty.ColorWrite;
            if (_last.BlendStateId != next.BlendStateId) dirty |= DynamicStateDirty.ColorBlend;
        }

        _serial = serial;
        _last = next;
        return dirty;
    }

    public static int CommandCount(DynamicStateDirty dirty) => BitOperations.PopCount((uint)dirty);

    private static bool SameViewport(in Viewport a, in Viewport b) =>
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height &&
        a.MinDepth == b.MinDepth && a.MaxDepth == b.MaxDepth;

    private static bool SameRect(in Rect2D a, in Rect2D b) =>
        a.Offset.X == b.Offset.X && a.Offset.Y == b.Offset.Y &&
        a.Extent.Width == b.Extent.Width && a.Extent.Height == b.Extent.Height;
}
