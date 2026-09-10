using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;

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

/// <summary>A set of per-attachment blend states, interned as a unit.</summary>
internal sealed class BlendSignature : IEquatable<BlendSignature>
{
    private readonly uint[] _packed;
    private readonly int _hash;

    public BlendSignature(ReadOnlySpan<AttachmentBlend> attachments)
    {
        _packed = new uint[attachments.Length];
        var hash = new HashCode();
        for (int i = 0; i < attachments.Length; i++)
        {
            _packed[i] = attachments[i].Pack();
            hash.Add(_packed[i]);
        }
        _hash = hash.ToHashCode();
    }

    public bool Equals(BlendSignature? other)
    {
        if (other is null || other._hash != _hash || other._packed.Length != _packed.Length) return false;
        for (int i = 0; i < _packed.Length; i++)
        {
            if (_packed[i] != other._packed[i]) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as BlendSignature);
    public override int GetHashCode() => _hash;
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
/// The emulated OpenGL state machine.
///
/// The game and its mods drive rendering the way GL asks them to: set a piece of
/// state, set another, bind a texture to a unit, draw. Reproducing that protocol
/// is what lets every render system and every mod keep working unchanged, so this
/// class records state rather than executing it, and a draw resolves the record
/// into a pipeline key plus a handful of dynamic-state commands.
///
/// It is the same approach Zink and ANGLE take, narrowed to the state this one
/// game actually touches.
/// </summary>
internal sealed class GlStateTracker
{
    public const int MaxColorAttachments = 8;
    public const int MaxTextureUnits = 16;

    private readonly AttachmentBlend[] _blend = new AttachmentBlend[MaxColorAttachments];
    private readonly Interner<BlendSignature> _blendSignatures = new();
    private readonly Interner<RenderTargetFormats> _targetFormats = new();

    private int _cachedBlendId = -1;
    private int _cachedBlendCount = -1;
    private ColorComponentFlags _colorWriteMask =
        ColorComponentFlags.RBit | ColorComponentFlags.GBit
        | ColorComponentFlags.BBit | ColorComponentFlags.ABit;

    public GlStateTracker()
    {
        for (int i = 0; i < _blend.Length; i++) _blend[i] = AttachmentBlend.Default;
    }

    // ------------------------------------------------------------ dynamic state

    public Rect2D Viewport { get; private set; }
    public Rect2D Scissor { get; private set; }
    public bool ScissorEnabled { get; private set; }

    public bool DepthTest { get; private set; }
    public bool DepthWrite { get; private set; } = true;
    public CompareOp DepthCompare { get; private set; } = CompareOp.Less;

    public bool CullEnabled { get; private set; }
    public CullModeFlags CullMode { get; private set; } = CullModeFlags.BackBit;

    public bool StencilTest { get; private set; }
    public uint StencilWriteMask { get; private set; } = 0xFF;
    public uint StencilCompareMask { get; private set; } = 0xFF;
    public uint StencilReference { get; private set; }
    public CompareOp StencilCompare { get; private set; } = CompareOp.Always;
    public StencilOp StencilFail { get; private set; } = StencilOp.Keep;
    public StencilOp StencilDepthFail { get; private set; } = StencilOp.Keep;
    public StencilOp StencilPass { get; private set; } = StencilOp.Keep;

    public float LineWidth { get; private set; } = 1.0f;
    public PrimitiveTopology Topology { get; private set; } = PrimitiveTopology.TriangleList;

    // ------------------------------------------------------------- pipeline state

    public PolygonMode PolygonMode { get; private set; } = PolygonMode.Fill;
    public int CurrentProgram { get; private set; }

    /// <summary>
    /// The front face is a constant, not a setting. GL's counter-clockwise
    /// winding, read in a Vulkan framebuffer with no Y flip, is clockwise. The
    /// game never calls glFrontFace, so nothing varies it.
    /// </summary>
    public const FrontFace FrontFace = Silk.NET.Vulkan.FrontFace.Clockwise;

    // -------------------------------------------------------------------- setters

    public void SetViewport(int x, int y, int width, int height) =>
        Viewport = new Rect2D(new Offset2D(x, y), new Extent2D((uint)Math.Max(0, width), (uint)Math.Max(0, height)));

    /// <summary>
    /// Records the scissor rectangle, clipped to the positive quadrant.
    ///
    /// glScissor takes a signed origin and the game passes negative ones - a
    /// dialog that extends past the top of the screen produces y = -72. GL clips
    /// the rectangle against the framebuffer and keeps the visible remainder;
    /// Vulkan rejects a negative offset outright. Moving the origin back to zero
    /// and taking the same amount off the extent leaves the identical region.
    /// </summary>
    public void SetScissor(int x, int y, int width, int height)
    {
        int clippedX = Math.Max(0, x);
        int clippedY = Math.Max(0, y);
        width -= clippedX - x;
        height -= clippedY - y;

        Scissor = new Rect2D(
            new Offset2D(clippedX, clippedY),
            new Extent2D((uint)Math.Max(0, width), (uint)Math.Max(0, height)));
    }

    public void SetScissorEnabled(bool enabled) => ScissorEnabled = enabled;

    public void SetDepthTest(bool enabled) => DepthTest = enabled;
    public void SetDepthWrite(bool enabled) => DepthWrite = enabled;
    public void SetDepthFunc(int glFunc) => DepthCompare = GlEnums.CompareOpFrom(glFunc);

    public void SetCullEnabled(bool enabled) => CullEnabled = enabled;
    public void SetCullBack(bool back) => CullMode = back ? CullModeFlags.BackBit : CullModeFlags.FrontBit;

    public void SetStencilTest(bool enabled) => StencilTest = enabled;
    public void SetStencilMask(int mask) => StencilWriteMask = (uint)mask;

    public void SetStencilFunc(int func, int reference, int mask)
    {
        StencilCompare = GlEnums.CompareOpFrom(func);
        StencilReference = (uint)reference;
        StencilCompareMask = (uint)mask;
    }

    public void SetStencilOp(int fail, int depthFail, int pass)
    {
        StencilFail = GlEnums.StencilOpFrom(fail);
        StencilDepthFail = GlEnums.StencilOpFrom(depthFail);
        StencilPass = GlEnums.StencilOpFrom(pass);
    }

    public void SetLineWidth(float width) => LineWidth = width;
    public void SetTopology(EnumDrawMode mode) => Topology = GlEnums.TopologyFrom(mode);
    public void SetWireframe(bool enabled) => PolygonMode = enabled ? PolygonMode.Line : PolygonMode.Fill;
    public void SetProgram(int programId) => CurrentProgram = programId;

    /// <summary>
    /// GL's colour mask is global; Vulkan's is per attachment. Setting it here
    /// replicates it across all of them, which is what the GL behaviour means.
    /// </summary>
    public void SetColorMask(bool r, bool g, bool b, bool a)
    {
        ColorComponentFlags mask = 0;
        if (r) mask |= ColorComponentFlags.RBit;
        if (g) mask |= ColorComponentFlags.GBit;
        if (b) mask |= ColorComponentFlags.BBit;
        if (a) mask |= ColorComponentFlags.ABit;

        if (mask == _colorWriteMask) return;
        _colorWriteMask = mask;

        for (int i = 0; i < _blend.Length; i++) _blend[i].WriteMask = mask;
        _cachedBlendId = -1;
        _cachedBlendCount = -1;
    }

    /// <summary>
    /// Applies one of the game's named blend modes to every attachment, matching
    /// the factor pairs ClientPlatformWindows.GlToggleBlend selects.
    /// </summary>
    public void SetBlend(bool enabled, EnumBlendMode mode)
    {
        (BlendFactor srcColor, BlendFactor dstColor, BlendFactor srcAlpha, BlendFactor dstAlpha) = mode switch
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

        for (int i = 0; i < _blend.Length; i++)
        {
            _blend[i].Enabled = enabled;
            _blend[i].SrcColor = srcColor;
            _blend[i].DstColor = dstColor;
            _blend[i].ColorOp = BlendOp.Add;
            _blend[i].SrcAlpha = srcAlpha;
            _blend[i].DstAlpha = dstAlpha;
            _blend[i].AlphaOp = BlendOp.Add;
        }
        _cachedBlendId = -1;
        _cachedBlendCount = -1;
    }

    /// <summary>
    /// Per-attachment blend, which the OIT and SSAO passes use through
    /// <c>glBlendFunci</c> and <c>glBlendEquationi</c>.
    /// </summary>
    public void SetAttachmentBlendFunc(int attachment, int srcColor, int dstColor, int srcAlpha, int dstAlpha)
    {
        if ((uint)attachment >= MaxColorAttachments) return;

        _blend[attachment].SrcColor = GlEnums.BlendFactorFrom(srcColor);
        _blend[attachment].DstColor = GlEnums.BlendFactorFrom(dstColor);
        _blend[attachment].SrcAlpha = GlEnums.BlendFactorFrom(srcAlpha);
        _blend[attachment].DstAlpha = GlEnums.BlendFactorFrom(dstAlpha);
        _cachedBlendId = -1;
        _cachedBlendCount = -1;
    }

    public void SetAttachmentBlendEquation(int attachment, int equation)
    {
        if ((uint)attachment >= MaxColorAttachments) return;

        BlendOp op = GlEnums.BlendOpFrom(equation);
        _blend[attachment].ColorOp = op;
        _blend[attachment].AlphaOp = op;
        _cachedBlendId = -1;
        _cachedBlendCount = -1;
    }

    // ---------------------------------------------------------------------- keys

    /// <summary>
    /// Interns the current blend set. The id is cached and only recomputed after
    /// a blend change, so a run of draws sharing state pays nothing.
    /// </summary>
    public int BlendId(int attachmentCount)
    {
        int count = Math.Clamp(attachmentCount, 0, MaxColorAttachments);
        if (_cachedBlendId >= 0 && _cachedBlendCount == count) return _cachedBlendId;

        _cachedBlendCount = count;
        _cachedBlendId = _blendSignatures.Intern(new BlendSignature(_blend.AsSpan(0, count)));
        return _cachedBlendId;
    }

    public int InternTargetFormats(RenderTargetFormats formats) => _targetFormats.Intern(formats);
    public RenderTargetFormats TargetFormats(int id) => _targetFormats.Get(id);

    /// <summary>Blend state for one attachment, for pipeline creation.</summary>
    public AttachmentBlend BlendFor(int attachment) => _blend[attachment];

    public PipelineKey BuildKey(int vertexLayoutId, int targetFormatsId, int attachmentCount) => new(
        ProgramId: CurrentProgram,
        VertexLayoutId: vertexLayoutId,
        TargetFormatsId: targetFormatsId,
        BlendId: BlendId(attachmentCount),
        PolygonMode: PolygonMode,
        TopologyClass: GlEnums.TopologyClassOf(Topology));

    /// <summary>
    /// Restores the defaults a fresh GL context would have. Called when the
    /// device is created and whenever the client resets its own state wholesale.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _blend.Length; i++) _blend[i] = AttachmentBlend.Default;
        _cachedBlendId = -1;
        _cachedBlendCount = -1;
        _colorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
            | ColorComponentFlags.BBit | ColorComponentFlags.ABit;

        DepthTest = false;
        DepthWrite = true;
        DepthCompare = CompareOp.Less;
        CullEnabled = false;
        CullMode = CullModeFlags.BackBit;
        ScissorEnabled = false;
        StencilTest = false;
        StencilWriteMask = 0xFF;
        StencilCompareMask = 0xFF;
        StencilReference = 0;
        StencilCompare = CompareOp.Always;
        StencilFail = StencilDepthFail = StencilPass = StencilOp.Keep;
        LineWidth = 1.0f;
        Topology = PrimitiveTopology.TriangleList;
        PolygonMode = PolygonMode.Fill;
        CurrentProgram = 0;
    }
}
