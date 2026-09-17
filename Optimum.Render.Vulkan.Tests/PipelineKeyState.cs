using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The OpenGL-shaped state record the renderer used before every draw became native, kept for the
/// component tests that drive <see cref="GraphicsPipelineCache" />, <see cref="RenderTargetManager" />
/// and <see cref="MeshManager" /> directly: it builds their pipeline keys, blend sets and dynamic
/// state the way the removed emulated draw did. Nothing in the renderer uses it.
/// </summary>
internal sealed class PipelineKeyState
{
    public const int MaxColorAttachments = RenderLimits.MaxColorAttachments;
    public const int MaxTextureUnits = RenderLimits.MaxTextureUnits;

    private readonly AttachmentBlend[] _blend = new AttachmentBlend[MaxColorAttachments];
    private readonly Interner<BlendSignature> _blendSignatures = new();
    private readonly Interner<RenderTargetFormats> _targetFormats = new();

    private int _cachedBlendId = -1;
    private int _cachedBlendCount = -1;
    private ColorComponentFlags _colorWriteMask =
        ColorComponentFlags.RBit | ColorComponentFlags.GBit
        | ColorComponentFlags.BBit | ColorComponentFlags.ABit;

    public PipelineKeyState()
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

    public const FrontFace FrontFace = RenderLimits.FrontFace;

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
        InvalidateBlend();
    }

    /// <summary>
    /// Applies one of the game's named blend modes to every attachment, matching
    /// the factor pairs ClientPlatformWindows.GlToggleBlend selects.
    /// </summary>
    public void SetBlend(bool enabled, EnumBlendMode mode)
    {
        (BlendFactor srcColor, BlendFactor dstColor, BlendFactor srcAlpha, BlendFactor dstAlpha) =
            AttachmentBlend.FactorsFor(mode);

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
        InvalidateBlend();
    }

    /// <summary>glEnable/glDisable(GL_BLEND) preserve the indexed blend functions.</summary>
    public void SetBlendEnabled(bool enabled)
    {
        for (int i = 0; i < _blend.Length; i++) _blend[i].Enabled = enabled;
        InvalidateBlend();
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
        InvalidateBlend();
    }

    public void SetAttachmentBlendEquation(int attachment, int equation)
    {
        if ((uint)attachment >= MaxColorAttachments) return;

        BlendOp op = GlEnums.BlendOpFrom(equation);
        _blend[attachment].ColorOp = op;
        _blend[attachment].AlphaOp = op;
        InvalidateBlend();
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

    public PipelineKey BuildKey(int vertexLayoutId, int targetFormatsId, int attachmentCount) =>
        BuildKey(vertexLayoutId, targetFormatsId, attachmentCount, uint.MaxValue);

    /// <summary>The key under the current <see cref="ColorWriteTier" /> for a target with these draw buffers.</summary>
    public PipelineKey BuildKey(int vertexLayoutId, int targetFormatsId, int attachmentCount, uint drawBufferMask) => new(
        ProgramId: CurrentProgram,
        VertexLayoutId: vertexLayoutId,
        TargetFormatsId: targetFormatsId,
        BlendId: PipelineBlendId(attachmentCount, drawBufferMask),
        PolygonMode: PolygonMode,
        TopologyClass: GlEnums.TopologyClassOf(Topology));

    // ------------------------------------------------------- colour write masks

    private const ColorComponentFlags AllChannels =
        ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;

    private int _cachedPipelineBlendId = -1;
    private int _cachedPipelineBlendCount = -1;
    private uint _cachedPipelineDrawBuffers;

    /// <summary>
    /// How draw-buffer and colour-mask changes reach the GPU (Phase 2, C4). The
    /// device sets it from the context's selected tier; component tests keep the
    /// default, which bakes everything into the pipeline key.
    /// </summary>
    public ColorWriteTier ColorWriteTier
    {
        get => _colorWriteTier;
        set
        {
            _colorWriteTier = value;
            InvalidateBlend();
        }
    }

    private ColorWriteTier _colorWriteTier = ColorWriteTier.PipelineKey;
    private bool _dynamicBlend;

    /// <summary>With the mask tier: blend enable and equation are dynamic too, so the blend set leaves the key.</summary>
    public bool DynamicBlend
    {
        get => _dynamicBlend;
        set
        {
            _dynamicBlend = value;
            InvalidateBlend();
        }
    }

    /// <summary>The global glColorMask.</summary>
    public ColorComponentFlags ColorMask => _colorWriteMask;

    private void InvalidateBlend()
    {
        _cachedBlendId = -1;
        _cachedBlendCount = -1;
        _cachedPipelineBlendId = -1;
        _cachedPipelineBlendCount = -1;
    }

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

    /// <summary>
    /// The effective write mask of one attachment: <c>drawBufferEnabled ? colorMask : 0</c>,
    /// then masked by the outputs the program writes (an unwritten output keeps
    /// the attachment's contents, as GL does; Vulkan would store undefined values).
    /// </summary>
    public ColorComponentFlags EffectiveWriteMask(int attachment, uint drawBufferMask, uint writtenOutputs)
    {
        if ((uint)attachment >= MaxColorAttachments) return 0;
        if (((drawBufferMask >> attachment) & 1) == 0) return 0;
        if (((writtenOutputs >> attachment) & 1) == 0) return 0;
        return _colorWriteMask;
    }

    /// <summary>
    /// The blend state of one attachment as the pipeline bakes it under the tier:
    /// the draw-buffer-masked write mask in the key tier, glColorMask alone in the
    /// enable tier (draw buffers are the dynamic enable), and a canonical mask in
    /// the mask tier (the dynamic mask replaces it; with dynamic blend the whole
    /// attachment state is canonical).
    /// </summary>
    public AttachmentBlend PipelineBlendFor(int attachment, uint drawBufferMask)
    {
        AttachmentBlend blend = _blend[attachment];
        switch (ColorWriteTier)
        {
        case ColorWriteTier.PipelineKey:
            if (((drawBufferMask >> attachment) & 1) == 0) blend.WriteMask = 0;
            break;
        case ColorWriteTier.DynamicMask:
            if (DynamicBlend) blend = AttachmentBlend.Default;
            blend.WriteMask = AllChannels;
            break;
        }
        return blend;
    }

    /// <summary>
    /// The interned blend set a pipeline is keyed on under the tier. Equal to
    /// <see cref="BlendId" /> whenever the tier leaves the state unchanged, so the
    /// key tier with every draw buffer selected keys exactly as before.
    /// </summary>
    public int PipelineBlendId(int attachmentCount, uint drawBufferMask)
    {
        int count = Math.Clamp(attachmentCount, 0, MaxColorAttachments);
        uint selectable = count == 32 ? uint.MaxValue : (1u << count) - 1;
        uint relevant = drawBufferMask & selectable;

        if (ColorWriteTier == ColorWriteTier.DynamicEnable ||
            (ColorWriteTier == ColorWriteTier.PipelineKey && relevant == selectable))
        {
            return BlendId(count);
        }

        if (ColorWriteTier == ColorWriteTier.DynamicMask) relevant = 0;
        if (_cachedPipelineBlendId >= 0 && _cachedPipelineBlendCount == count && _cachedPipelineDrawBuffers == relevant)
        {
            return _cachedPipelineBlendId;
        }

        Span<AttachmentBlend> baked = stackalloc AttachmentBlend[count];
        for (int i = 0; i < count; i++) baked[i] = PipelineBlendFor(i, drawBufferMask);

        _cachedPipelineBlendCount = count;
        _cachedPipelineDrawBuffers = relevant;
        _cachedPipelineBlendId = _blendSignatures.Intern(new BlendSignature(baked));
        return _cachedPipelineBlendId;
    }

    /// <summary>
    /// Restores the defaults a fresh GL context would have. Called when the
    /// device is created and whenever the client resets its own state wholesale.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _blend.Length; i++) _blend[i] = AttachmentBlend.Default;
        InvalidateBlend();
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
