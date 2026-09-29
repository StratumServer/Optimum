using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Optimum.Render.Vulkan.Graph;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// The fixed-function state the client stated through the platform's virtuals, with OpenGL's
/// semantics, owned by the platform (docs/vulkan.md, decision 3: a native
/// system reads client state, never the device's). The generic native draw
/// (VulkanClientPlatform.NativeStated.cs) builds its pipeline, pass and textures from this alone.
///
/// Semantics that differ from a naive record, each as the OpenGL body does it:
/// - blend: <c>glBlendFunc</c> sets every draw buffer, <c>glBlendFunci</c> one; <c>glDisable(GL_BLEND)</c>
///   keeps the functions (<see cref="SetBlendMode" />, <see cref="SetSlotBlend" />, <see cref="SetBlendEnabled" />);
/// - colour mask: <c>glColorMask</c> is global (<see cref="ColorMask" />);
/// - draw buffers: per framebuffer, and a framebuffer nobody selected for writes attachment 0 only,
///   GL's default for a framebuffer object (<see cref="DrawBuffers" />);
/// - texture units: one texture per unit, whatever its dimensionality, as the device's table has it
///   (a cube and a 2D bind to the same unit replace each other there too);
/// - viewport: a framebuffer bind does not change it; the platform's own bind states the full target.
/// Stencil is recorded but never applied: no framebuffer of this client has a stencil attachment,
/// so a stencil test passes on either path.
/// </summary>
internal sealed class StatedRenderState
{
    public const int MaxColorAttachments = RenderLimits.MaxColorAttachments;
    public const int MaxTextureUnits = RenderLimits.MaxTextureUnits;

    private readonly AttachmentBlend[] _blend = new AttachmentBlend[MaxColorAttachments];
    private readonly Dictionary<(int FramebufferId, int Count), AttachmentBlend[]> _blendSnapshots = new();
    private readonly Dictionary<int, uint> _drawBuffers = new();
    private readonly int[] _unitTextures = new int[MaxTextureUnits];
    private readonly int[] _unitSamplers = new int[MaxTextureUnits];

    public StatedRenderState()
    {
        for (int i = 0; i < _blend.Length; i++) _blend[i] = AttachmentBlend.Default;
    }

    public bool BlendEnabled { get; private set; }

    /// <summary>The channels <c>glColorMask</c> left writable, applied to every attachment.</summary>
    public ColorComponentFlags ColorMask { get; private set; } =
        ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;

    public bool DepthTest { get; set; }
    public bool DepthWrite { get; set; } = true;
    public CompareOp DepthCompare { get; set; } = CompareOp.Less;
    public bool CullEnabled { get; set; }
    public bool CullBack { get; set; } = true;
    public float LineWidth { get; set; } = 1f;
    public bool Wireframe { get; set; }
    public bool ScissorEnabled { get; set; }
    public Rect2D Scissor { get; set; }
    public Rect2D Viewport { get; set; }
    public bool StencilTest { get; set; }

    public CullModeFlags CullMode => CullEnabled ? (CullBack ? CullModeFlags.BackBit : CullModeFlags.FrontBit) : CullModeFlags.None;

    /// <summary><c>glBlendFunc</c>/<c>glBlendFuncSeparate</c> of a named mode: every attachment's factors, add equations.</summary>
    public void SetBlendMode(EnumBlendMode mode)
    {
        _blendSnapshots.Clear();
        (BlendFactor srcColor, BlendFactor dstColor, BlendFactor srcAlpha, BlendFactor dstAlpha) = AttachmentBlend.FactorsFor(mode);
        for (int i = 0; i < _blend.Length; i++)
        {
            _blend[i].SrcColor = srcColor;
            _blend[i].DstColor = dstColor;
            _blend[i].SrcAlpha = srcAlpha;
            _blend[i].DstAlpha = dstAlpha;
            _blend[i].ColorOp = BlendOp.Add;
            _blend[i].AlphaOp = BlendOp.Add;
        }
    }

    /// <summary><c>glEnable/glDisable(GL_BLEND)</c>: the functions stay.</summary>
    public void SetBlendEnabled(bool enabled)
    {
        if (BlendEnabled == enabled) return;
        BlendEnabled = enabled;
        _blendSnapshots.Clear();
    }

    /// <summary><c>glBlendEquationi</c> + <c>glBlendFuncSeparatei</c> with GL tokens.</summary>
    public void SetSlotBlend(int slot, int glEquation, int srcColor, int dstColor, int srcAlpha, int dstAlpha)
    {
        if ((uint)slot >= MaxColorAttachments) return;
        _blendSnapshots.Clear();
        BlendOp op = GlEnums.BlendOpFrom(glEquation);
        _blend[slot].ColorOp = op;
        _blend[slot].AlphaOp = op;
        _blend[slot].SrcColor = GlEnums.BlendFactorFrom(srcColor);
        _blend[slot].DstColor = GlEnums.BlendFactorFrom(dstColor);
        _blend[slot].SrcAlpha = GlEnums.BlendFactorFrom(srcAlpha);
        _blend[slot].DstAlpha = GlEnums.BlendFactorFrom(dstAlpha);
    }

    /// <summary><c>glBlendEquationi</c>: one attachment's equation, its factors kept.</summary>
    public void SetSlotEquation(int slot, int glEquation)
    {
        if ((uint)slot >= MaxColorAttachments) return;
        _blendSnapshots.Clear();
        BlendOp op = GlEnums.BlendOpFrom(glEquation);
        _blend[slot].ColorOp = op;
        _blend[slot].AlphaOp = op;
    }

    /// <summary><c>glBlendFuncSeparatei</c>: one attachment's factors, its equation kept.</summary>
    public void SetSlotFunc(int slot, int srcColor, int dstColor, int srcAlpha, int dstAlpha)
    {
        if ((uint)slot >= MaxColorAttachments) return;
        _blendSnapshots.Clear();
        _blend[slot].SrcColor = GlEnums.BlendFactorFrom(srcColor);
        _blend[slot].DstColor = GlEnums.BlendFactorFrom(dstColor);
        _blend[slot].SrcAlpha = GlEnums.BlendFactorFrom(srcAlpha);
        _blend[slot].DstAlpha = GlEnums.BlendFactorFrom(dstAlpha);
    }

    public void SetColorMask(bool r, bool g, bool b, bool a)
    {
        ColorComponentFlags next = (r ? ColorComponentFlags.RBit : 0) | (g ? ColorComponentFlags.GBit : 0) |
                    (b ? ColorComponentFlags.BBit : 0) | (a ? ColorComponentFlags.ABit : 0);
        if (ColorMask == next) return;
        ColorMask = next;
        _blendSnapshots.Clear();
    }

    /// <summary>
    /// One attachment as a draw into <paramref name="framebufferId" /> applies it: the stated
    /// functions and enable, written only when the attachment is a selected draw buffer and
    /// the colour mask allows it.
    /// </summary>
    public AttachmentBlend AttachmentFor(int framebufferId, int slot)
    {
        if ((uint)slot >= MaxColorAttachments) return new AttachmentBlend { WriteMask = 0 };
        AttachmentBlend blend = _blend[slot];
        blend.Enabled = BlendEnabled;
        blend.WriteMask = ((DrawBuffers(framebufferId) >> slot) & 1) != 0 ? ColorMask : 0;
        if (IsUiImage(framebufferId)) blend = blend.ForUiImage();
        return blend;
    }

    /// <summary>
    /// An immutable snapshot for a pipeline description. Pipeline entries retain
    /// the array, so invalidation drops this cache's reference without changing
    /// descriptions already recorded for earlier draws.
    /// </summary>
    public AttachmentBlend[] BlendFor(int framebufferId, int count)
    {
        var key = (framebufferId, count);
        if (_blendSnapshots.TryGetValue(key, out AttachmentBlend[]? snapshot))
            return snapshot;

        snapshot = new AttachmentBlend[count];
        for (int i = 0; i < count; i++) snapshot[i] = AttachmentFor(framebufferId, i);
        _blendSnapshots.Add(key, snapshot);
        return snapshot;
    }

    /// <summary>
    /// World/UI separation: the UI image's target while the platform's UI scope is open, 0 otherwise
    /// (VulkanClientPlatform.UiSeparation.cs). While it is set, Default means that image.
    /// </summary>
    private int _uiImageFramebuffer;
    public int UiImageFramebuffer
    {
        get => _uiImageFramebuffer;
        set
        {
            if (_uiImageFramebuffer == value) return;
            _uiImageFramebuffer = value;
            _blendSnapshots.Clear();
        }
    }

    /// <summary>Whether a draw into <paramref name="framebufferId" /> lands in the UI image.</summary>
    public bool IsUiImage(int framebufferId) =>
        UiImageFramebuffer > 0 &&
        (framebufferId == Graph.PassDeclaration.DefaultFramebuffer || framebufferId == UiImageFramebuffer);

    public void SetDrawBuffers(int framebufferId, uint mask)
    {
        if (_drawBuffers.TryGetValue(framebufferId, out uint current) && current == mask) return;
        _drawBuffers[framebufferId] = mask;
        _blendSnapshots.Clear();
    }

    public uint DrawBuffers(int framebufferId) =>
        _drawBuffers.TryGetValue(framebufferId, out uint mask) ? mask : 1u;

    public void ForgetFramebuffer(int framebufferId)
    {
        _drawBuffers.Remove(framebufferId);
        _blendSnapshots.Clear();
    }

    public void BindTexture(int unit, int textureId)
    {
        if ((uint)unit < MaxTextureUnits) _unitTextures[unit] = textureId;
    }

    public int TextureAt(int unit) => (uint)unit < MaxTextureUnits ? _unitTextures[unit] : 0;

    public void BindSampler(int unit, int samplerId)
    {
        if ((uint)unit < MaxTextureUnits) _unitSamplers[unit] = samplerId;
    }

    public int SamplerAt(int unit) => (uint)unit < MaxTextureUnits ? _unitSamplers[unit] : 0;
}

/// <summary>
/// One draw of a program recorded natively from a <see cref="StatedRenderState" /> into an
/// explicit target: the generic native draw (VulkanClientPlatform.NativeStated.cs) and the GPU
/// tests' GL-shaped helpers both record through here, so the tests exercise the route the client's
/// unrecognised draws take.
///
/// What it states, and from where:
/// - target: <paramref name="framebufferId" />; every colour slot attached to it on the device is in
///   the pass (not the FrameBufferRef's own list: the OIT accumulation targets are attached to
///   Transparent at slots 3-5 without being in its ColorTextureIds, and a pass without them drops the
///   accumulated colour - 2026-09-17, water drew black until the slots came from the attachments);
///   the draw buffers stated for that target become per-attachment write masks (decision 4);
/// - blend, colour mask, depth, cull, line width, polygon mode, viewport and scissor: the stated state;
/// - textures: per sampler, the texture on the unit the program points it at (its SetSamplerUnit
///   mapping, else the sampler's declaration order), with the unit's standalone sampler if one is bound;
/// - the depth attachment of the target sampled with depth writes off is read in the read-only
///   layout (SamplesBoundDepth); sampled while written, the draw is refused.
/// </summary>
internal static class StatedDraw
{
    /// <summary>
    /// Records the draw. <paramref name="meshId" /> 0 is the fullscreen triangle;
    /// <paramref name="starts" /> is a pool's multi-draw. False with a reason: nothing was recorded.
    /// <paramref name="declared" /> names the pass the draw belongs to (its name, slots, reads and
    /// flags); without one the draw opens "Stated/&lt;target&gt;" over every attached slot.
    /// </summary>
    internal static bool Record(VulkanDevice device, StatedRenderState stated, int programId, int framebufferId,
        int meshId, int instances, int[]? starts, int[]? sizes, int groupCount, out string? refusal,
        PassDeclaration? declared = null)
    {
        refusal = null;
        RenderTargetFormats? all = device.NativeTargetFormats(framebufferId, uint.MaxValue);
        if (all == null) return Refused("framebuffer " + framebufferId + " does not exist", out refusal);
        int attached = all.ColorFormats.Length;
        uint slots = attached >= 32 ? uint.MaxValue : (1u << attached) - 1u;
        if (declared != null) slots &= declared.ColorSlots;

        int layoutId = meshId > 0 ? device.NativeMeshLayoutId(meshId) : MeshManager.EmptyLayoutId;
        if (layoutId < 0) return Refused("the mesh has no layout", out refusal);

        // Every sampler the program declares, from the unit it points at.
        string[] names = device.SamplerNamesOf(programId);
        int[] samplerUnits = device.NativeSamplerUnitsOf(programId);
        int depthTexture = device.NativeFramebufferDepthTexture(framebufferId);
        bool samplesBoundDepth = false;
        var reads = new int[names.Length];
        Span<int> units = stackalloc int[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            units[i] = i < samplerUnits.Length ? samplerUnits[i] : -1;
            reads[i] = stated.TextureAt(units[i]);
            if (reads[i] != 0 && reads[i] == depthTexture)
            {
                if (stated.DepthWrite && stated.DepthTest)
                {
                    return Refused("it samples the depth attachment it writes", out refusal);
                }
                samplesBoundDepth = true;
            }
        }

        // A colour slot the draw samples while its draw buffer is off leaves the pass: GL reads it as
        // any texture (the composition writes Primary 0 and reads Primary 1). With its draw buffer on
        // it stays, and the device samples a copy of it (feedback).
        uint drawBuffers = stated.DrawBuffers(framebufferId);
        for (int slot = 0; slot < attached && slot < 32; slot++)
        {
            if (((drawBuffers >> slot) & 1) != 0 || ((slots >> slot) & 1) == 0) continue;
            int attachment = device.NativeFramebufferColorTexture(framebufferId, slot);
            if (attachment != 0 && Array.IndexOf(reads, attachment) >= 0) slots &= ~(1u << slot);
        }
        RenderTargetFormats? formats = device.NativeTargetFormats(framebufferId, slots);
        if (formats == null) return Refused("no formats for framebuffer " + framebufferId, out refusal);

        AttachmentBlend[] blend = stated.BlendFor(framebufferId, Math.Max(formats.ColorFormats.Length, 1));

        var description = new NativePipelineDescription
        {
            ProgramId = programId,
            Blend = blend,
            DepthTest = stated.DepthTest,
            DepthWrite = stated.DepthWrite && !samplesBoundDepth,
            DepthCompare = stated.DepthCompare,
            Cull = stated.CullMode,
            Topology = meshId > 0 ? device.NativeMeshTopology(meshId) : PrimitiveTopology.TriangleList,
            PolygonMode = stated.Wireframe ? PolygonMode.Line : PolygonMode.Fill,
            LineWidth = stated.LineWidth,
            VertexLayoutId = layoutId,
            SamplesBoundDepth = samplesBoundDepth,
            Targets = formats,
        };
        NativePipeline? pipeline = device.RequestNativePipeline(description, out string error);
        if (pipeline == null) return Refused(error, out refusal);

        var textures = new NativeTexture[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int sampler = stated.SamplerAt(units[i]);
            textures[i] = new NativeTexture(pipeline.SamplerAt(i), reads[i],
                sampler != 0 ? device.NativeStandaloneSampler(sampler) : null);
        }

        int[] passReads = reads;
        if (declared != null && declared.Reads.Length > 0)
            passReads = MergeReads(declared.Reads, reads);
        Rect2D viewport = stated.Viewport;
        bool drawn = false;
        if (device.BeginNativePass(new NativePassDescription
        {
            Name = declared?.Name ?? "Stated/" + framebufferId,
            FramebufferId = framebufferId,
            ColorSlots = slots,
            Reads = passReads,
            TransientSlots = declared?.TransientSlots ?? 0,
            Flags = declared?.Flags ?? PassFlags.AllowSplit,
            Generic = true,
            ViewportX = viewport.Offset.X,
            ViewportY = viewport.Offset.Y,
            ViewportWidth = (int)viewport.Extent.Width,
            ViewportHeight = (int)viewport.Extent.Height,
            Scissor = stated.ScissorEnabled ? stated.Scissor : null,
        }))
        {
            drawn = meshId <= 0
                ? device.DrawNativeFullscreen(pipeline, textures)
                : starts != null
                    ? device.DrawNativeMeshMulti(pipeline, meshId, starts, sizes!, groupCount, textures)
                    : device.DrawNativeMeshInstanced(pipeline, meshId, instances, textures);
        }
        // The scope stays open: the next stated draw on the same target and slots coalesces into
        // this pass instead of ending the rendering scope and starting another; anything else
        // declares its own pass, which ends this one.
        device.EndNativePass(keepScope: true);
        return drawn;
    }

    private static bool Refused(string reason, out string? refusal)
    {
        refusal = reason;
        return false;
    }

    private static int[] MergeReads(int[] declared, int[] sampled)
    {
        int extra = 0;
        for (int i = 0; i < sampled.Length; i++)
        {
            int read = sampled[i];
            if (Array.IndexOf(declared, read) < 0 && Array.IndexOf(sampled, read, 0, i) < 0)
                extra++;
        }

        var merged = new int[declared.Length + extra];
        Array.Copy(declared, merged, declared.Length);
        int next = declared.Length;
        foreach (int read in sampled)
        {
            if (Array.IndexOf(merged, read, 0, next) < 0)
                merged[next++] = read;
        }
        return merged;
    }
}
