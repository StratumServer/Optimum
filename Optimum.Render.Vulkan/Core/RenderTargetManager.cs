using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>One attachment slot of a framebuffer.</summary>
internal struct AttachmentSlot
{
    public int TextureId;
    public uint Layer;

    public readonly bool IsBound => TextureId > 0;
}

/// <summary>
/// A render target: colour attachments in GL's positional slots, an optional
/// depth attachment, and the draw-buffer mask.
/// </summary>
internal sealed class VulkanFramebuffer
{
    public int Id;
    public uint Width;
    public uint Height;

    public AttachmentSlot[] Color = new AttachmentSlot[GlStateTracker.MaxColorAttachments];
    public int DepthTextureId;

    /// <summary>
    /// Bit i set means fragment output i is written. GL's glDrawBuffers selects
    /// a subset of attachments rather than merely masking writes, so a cleared
    /// bit means the attachment is not part of the rendering scope at all.
    /// </summary>
    public uint DrawBufferMask = 1;

    /// <summary>Cached interned id of the attachment formats, or -1 when stale.</summary>
    public int FormatsId = -1;
}

/// <summary>
/// Owns framebuffers and drives dynamic rendering scopes.
///
/// The subtle part is <c>glDrawBuffers</c>. It does not mask writes - it selects
/// which attachments participate - and the game depends on that: the final
/// composition pass renders into the primary framebuffer's attachment 0 while
/// sampling its attachment 1, which is only legal because attachment 1 is not
/// part of the draw. Vulkan agrees, as long as the excluded attachments are left
/// out of <c>vkCmdBeginRendering</c> and moved to a shader-readable layout, so
/// the mask is honoured positionally: a disabled slot becomes a null attachment,
/// keeping fragment output N aimed at slot N.
/// </summary>
internal sealed unsafe class RenderTargetManager : IDisposable
{
    private readonly VulkanContext _context;
    private readonly TextureManager _textures;
    private readonly GlStateTracker _state;

    private readonly List<VulkanFramebuffer?> _framebuffers = new();
    private readonly Stack<int> _freeIds = new();

    private VulkanFramebuffer? _bound;
    private bool _renderingActive;
    private bool _disposed;

    /// <summary>How many rendering scopes have been opened, for diagnostics.</summary>
    public long ScopesOpened { get; private set; }

    public RenderTargetManager(VulkanContext context, TextureManager textures, GlStateTracker state)
    {
        _context = context;
        _textures = textures;
        _state = state;

        // Index 0 is the default framebuffer, installed separately.
        _framebuffers.Add(null);
    }

    public VulkanFramebuffer? Bound => _bound;
    public bool RenderingActive => _renderingActive;

    public VulkanFramebuffer? Get(int id) =>
        id > 0 && id < _framebuffers.Count ? _framebuffers[id] : null;

    public int Create(uint width, uint height)
    {
        var framebuffer = new VulkanFramebuffer { Width = width, Height = height };

        if (_freeIds.Count > 0)
        {
            int reused = _freeIds.Pop();
            framebuffer.Id = reused;
            _framebuffers[reused] = framebuffer;
            return reused;
        }

        _framebuffers.Add(framebuffer);
        framebuffer.Id = _framebuffers.Count - 1;
        return framebuffer.Id;
    }

    public void Attach(int framebufferId, int attachmentIndex, int textureId, uint layer = 0)
    {
        VulkanFramebuffer? framebuffer = Get(framebufferId);
        if (framebuffer == null) return;

        if (attachmentIndex < 0)
        {
            framebuffer.DepthTextureId = textureId;
        }
        else if (attachmentIndex < GlStateTracker.MaxColorAttachments)
        {
            framebuffer.Color[attachmentIndex] = new AttachmentSlot { TextureId = textureId, Layer = layer };
        }

        framebuffer.FormatsId = -1;
    }

    public void SetDrawBuffers(int framebufferId, uint mask)
    {
        VulkanFramebuffer? framebuffer = Get(framebufferId);
        if (framebuffer == null || framebuffer.DrawBufferMask == mask) return;

        framebuffer.DrawBufferMask = mask;
        framebuffer.FormatsId = -1;

        // The set of attachments changed, so the current scope no longer
        // describes what is being rendered into.
        if (_bound == framebuffer) _needsRestart = true;
    }

    private bool _needsRestart;

    /// <summary>
    /// Whether a texture takes part in the rendering scope the bound framebuffer
    /// is about to open, and so has to keep its attachment layout.
    ///
    /// Only slots the draw actually writes count. A colour attachment masked out
    /// of glDrawBuffers is not part of the scope at all, and the composition pass
    /// samples exactly such a slot - so it has to stay transitionable, or it is
    /// read in the colour-attachment layout it was left in.
    /// </summary>
    public bool IsAttachmentOfBound(int textureId)
    {
        if (_bound == null || textureId <= 0) return false;
        if (_bound.DepthTextureId == textureId) return true;

        for (int i = 0; i < _bound.Color.Length; i++)
        {
            if (_bound.Color[i].TextureId != textureId) continue;
            if ((_bound.DrawBufferMask & (1u << i)) != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Binds a framebuffer. Nothing is recorded here: GL lets a bind be followed
    /// by more state changes before anything is drawn, so the scope opens lazily
    /// at the first draw or clear.
    /// </summary>
    public void Bind(CommandBuffer commandBuffer, int framebufferId)
    {
        VulkanFramebuffer? framebuffer = Get(framebufferId);
        if (ReferenceEquals(framebuffer, _bound)) return;

        EndRendering(commandBuffer);
        _bound = framebuffer;
        _needsRestart = false;
    }

    public void Delete(int framebufferId)
    {
        VulkanFramebuffer? framebuffer = Get(framebufferId);
        if (framebuffer == null) return;

        if (ReferenceEquals(framebuffer, _bound)) _bound = null;
        _framebuffers[framebufferId] = null;
        _freeIds.Push(framebufferId);
    }

    // ------------------------------------------------------------------- scopes

    /// <summary>
    /// Opens a rendering scope if one is not already open, transitioning every
    /// participating attachment into its attachment layout and every excluded
    /// one into a shader-readable layout.
    /// </summary>
    public void EnsureRendering(CommandBuffer commandBuffer)
    {
        if (_renderingActive && !_needsRestart) return;
        if (_bound == null) return;

        if (_renderingActive) EndRendering(commandBuffer);

        VulkanFramebuffer framebuffer = _bound;
        int highest = HighestEnabledAttachment(framebuffer);
        int count = highest + 1;

        var attachments = new RenderingAttachmentInfo[Math.Max(count, 0)];

        // Every slot the draw does not write may be sampled instead, so it has to
        // be readable. This runs over all of them, not just the ones below the
        // highest enabled index: the composition pass renders into attachment 0
        // while sampling attachment 1, and a loop bounded by the attachment count
        // would never reach the slot it samples.
        for (int i = 0; i < framebuffer.Color.Length; i++)
        {
            AttachmentSlot unused = framebuffer.Color[i];
            if (!unused.IsBound) continue;
            if ((framebuffer.DrawBufferMask & (1u << i)) != 0) continue;

            VulkanTexture? excluded = _textures.Get(unused.TextureId);
            if (excluded != null)
            {
                _textures.TransitionTexture(commandBuffer, excluded, ImageLayout.ShaderReadOnlyOptimal);
            }
        }

        for (int i = 0; i < count; i++)
        {
            bool enabled = (framebuffer.DrawBufferMask & (1u << i)) != 0;
            AttachmentSlot slot = framebuffer.Color[i];

            if (!enabled || !slot.IsBound)
            {
                // A null view keeps fragment output i pointed at slot i while
                // discarding its writes, which is what a cleared draw-buffer bit
                // means in GL.
                attachments[i] = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = default,
                    ImageLayout = ImageLayout.Undefined,
                    LoadOp = AttachmentLoadOp.DontCare,
                    StoreOp = AttachmentStoreOp.DontCare,
                };
                continue;
            }

            VulkanTexture? texture = _textures.Get(slot.TextureId);
            if (texture == null)
            {
                attachments[i] = new RenderingAttachmentInfo { SType = StructureType.RenderingAttachmentInfo };
                continue;
            }

            _textures.TransitionTexture(commandBuffer, texture, ImageLayout.ColorAttachmentOptimal);

            attachments[i] = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                // The slot's layer, not the whole image: an array attached once
                // per layer must reach a different layer each time.
                ImageView = texture.ViewOfLayer(slot.Layer),
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                // LOAD preserves what is already there, which is GL's model: a
                // framebuffer keeps its contents until something clears it.
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
            };
        }

        RenderingAttachmentInfo depthAttachment = default;
        bool hasDepth = false;
        if (framebuffer.DepthTextureId > 0)
        {
            VulkanTexture? depth = _textures.Get(framebuffer.DepthTextureId);
            if (depth != null)
            {
                _textures.TransitionTexture(commandBuffer, depth, ImageLayout.DepthAttachmentOptimal);
                depthAttachment = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = depth.View,
                    ImageLayout = ImageLayout.DepthAttachmentOptimal,
                    LoadOp = AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                };
                hasDepth = true;
            }
        }

        fixed (RenderingAttachmentInfo* attachmentsPtr = attachments)
        {
            var rendering = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(framebuffer.Width, framebuffer.Height)),
                LayerCount = 1,
                ColorAttachmentCount = (uint)attachments.Length,
                PColorAttachments = attachments.Length == 0 ? null : attachmentsPtr,
                PDepthAttachment = hasDepth ? &depthAttachment : null,
            };

            _context.Api.CmdBeginRendering(commandBuffer, &rendering);
        }

        _renderingActive = true;
        _needsRestart = false;
        ScopesOpened++;
    }

    /// <summary>
    /// Closes the scope. Uploads and layout transitions have to happen outside
    /// one, so this is called before them and the scope reopens on the next draw.
    /// </summary>
    public void EndRendering(CommandBuffer commandBuffer)
    {
        if (!_renderingActive) return;
        _context.Api.CmdEndRendering(commandBuffer);
        _renderingActive = false;
    }

    // ------------------------------------------------------------------- clears

    public void ClearColor(CommandBuffer commandBuffer, int attachment, float r, float g, float b, float a)
    {
        if (_bound == null) return;
        EnsureRendering(commandBuffer);
        if (!_renderingActive) return;

        var clear = new ClearAttachment
        {
            AspectMask = ImageAspectFlags.ColorBit,
            ColorAttachment = (uint)attachment,
            ClearValue = new ClearValue(new ClearColorValue(r, g, b, a)),
        };
        var rect = new ClearRect
        {
            Rect = new Rect2D(new Offset2D(0, 0), new Extent2D(_bound.Width, _bound.Height)),
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        _context.Api.CmdClearAttachments(commandBuffer, 1, &clear, 1, &rect);
    }

    public void ClearDepth(CommandBuffer commandBuffer, float depth)
    {
        if (_bound == null || _bound.DepthTextureId <= 0) return;
        EnsureRendering(commandBuffer);
        if (!_renderingActive) return;

        var clear = new ClearAttachment
        {
            AspectMask = ImageAspectFlags.DepthBit,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(depth, 0)),
        };
        var rect = new ClearRect
        {
            Rect = new Rect2D(new Offset2D(0, 0), new Extent2D(_bound.Width, _bound.Height)),
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        _context.Api.CmdClearAttachments(commandBuffer, 1, &clear, 1, &rect);
    }

    // ------------------------------------------------------------------ formats

    /// <summary>
    /// The attachment formats of the bound target, for the pipeline key. Disabled
    /// slots report <see cref="Format.Undefined" /> so the pipeline agrees with
    /// the null attachments the scope was opened with.
    /// </summary>
    public int FormatsIdOf(VulkanFramebuffer framebuffer)
    {
        if (framebuffer.FormatsId >= 0) return framebuffer.FormatsId;

        int count = HighestEnabledAttachment(framebuffer) + 1;
        var colorFormats = new Format[Math.Max(count, 0)];

        for (int i = 0; i < count; i++)
        {
            bool enabled = (framebuffer.DrawBufferMask & (1u << i)) != 0;
            AttachmentSlot slot = framebuffer.Color[i];
            VulkanTexture? texture = enabled && slot.IsBound ? _textures.Get(slot.TextureId) : null;
            colorFormats[i] = texture?.Format ?? Format.Undefined;
        }

        Format depthFormat = Format.Undefined;
        if (framebuffer.DepthTextureId > 0)
        {
            depthFormat = _textures.Get(framebuffer.DepthTextureId)?.Format ?? Format.Undefined;
        }

        framebuffer.FormatsId = _state.InternTargetFormats(new RenderTargetFormats(colorFormats, depthFormat));
        return framebuffer.FormatsId;
    }

    public int EnabledAttachmentCount(VulkanFramebuffer framebuffer) =>
        HighestEnabledAttachment(framebuffer) + 1;

    private static int HighestEnabledAttachment(VulkanFramebuffer framebuffer)
    {
        int highest = -1;
        for (int i = 0; i < GlStateTracker.MaxColorAttachments; i++)
        {
            if ((framebuffer.DrawBufferMask & (1u << i)) != 0 && framebuffer.Color[i].IsBound)
            {
                highest = i;
            }
        }
        return highest;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _framebuffers.Clear();
    }
}
