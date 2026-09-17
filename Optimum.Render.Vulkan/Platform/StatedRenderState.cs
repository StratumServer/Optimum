using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// The fixed-function state the client stated through the platform's virtuals, with OpenGL's
/// semantics, owned by the platform (docs/vulkan-native-render-systems.md, decision 3: a native
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
    public void SetBlendEnabled(bool enabled) => BlendEnabled = enabled;

    /// <summary><c>glBlendEquationi</c> + <c>glBlendFuncSeparatei</c> with GL tokens.</summary>
    public void SetSlotBlend(int slot, int glEquation, int srcColor, int dstColor, int srcAlpha, int dstAlpha)
    {
        if ((uint)slot >= MaxColorAttachments) return;
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
        BlendOp op = GlEnums.BlendOpFrom(glEquation);
        _blend[slot].ColorOp = op;
        _blend[slot].AlphaOp = op;
    }

    /// <summary><c>glBlendFuncSeparatei</c>: one attachment's factors, its equation kept.</summary>
    public void SetSlotFunc(int slot, int srcColor, int dstColor, int srcAlpha, int dstAlpha)
    {
        if ((uint)slot >= MaxColorAttachments) return;
        _blend[slot].SrcColor = GlEnums.BlendFactorFrom(srcColor);
        _blend[slot].DstColor = GlEnums.BlendFactorFrom(dstColor);
        _blend[slot].SrcAlpha = GlEnums.BlendFactorFrom(srcAlpha);
        _blend[slot].DstAlpha = GlEnums.BlendFactorFrom(dstAlpha);
    }

    public void SetColorMask(bool r, bool g, bool b, bool a)
    {
        ColorMask = (r ? ColorComponentFlags.RBit : 0) | (g ? ColorComponentFlags.GBit : 0) |
                    (b ? ColorComponentFlags.BBit : 0) | (a ? ColorComponentFlags.ABit : 0);
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
    /// World/UI separation: the UI image's target while the platform's UI scope is open, 0 otherwise
    /// (VulkanClientPlatform.UiSeparation.cs). While it is set, Default means that image.
    /// </summary>
    public int UiImageFramebuffer;

    /// <summary>Whether a draw into <paramref name="framebufferId" /> lands in the UI image.</summary>
    public bool IsUiImage(int framebufferId) =>
        UiImageFramebuffer > 0 &&
        (framebufferId == Graph.PassDeclaration.DefaultFramebuffer || framebufferId == UiImageFramebuffer);

    public void SetDrawBuffers(int framebufferId, uint mask) => _drawBuffers[framebufferId] = mask;

    public uint DrawBuffers(int framebufferId) =>
        _drawBuffers.TryGetValue(framebufferId, out uint mask) ? mask : 1u;

    public void ForgetFramebuffer(int framebufferId) => _drawBuffers.Remove(framebufferId);

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
