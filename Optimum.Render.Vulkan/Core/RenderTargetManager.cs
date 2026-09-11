using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Graph;
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

    /// <summary>
    /// Bound colour slots left out of the rendering scope because a draw samples
    /// them while their draw buffer is off (the composition pass writes Primary 0
    /// and reads Primary 1). Only ever a subset of the cleared draw-buffer bits;
    /// reset when the framebuffer is bound again.
    /// </summary>
    public uint SampledExclusion;

    /// <summary>
    /// Bound colour slots the declared frame-graph pass leaves out of its scope (the
    /// final composition writes Primary 0 and samples Primary 1). Cleared when the pass ends.
    /// </summary>
    public uint PassExclusion;
}

/// <summary>
/// Owns framebuffers and drives dynamic rendering scopes.
///
/// The subtle part is <c>glDrawBuffers</c>. Since Phase 2 (contract C4) it is a
/// write mask, never a scope restart: the scope carries every bound colour slot,
/// and a cleared draw-buffer bit only zeroes that attachment's effective write
/// mask (dynamic enable, dynamic mask or pipeline key, per
/// <see cref="ColorWriteTier" />). The TAA motion windows toggle the motion
/// attachment that way inside one scope.
///
/// The exception is a read: the final composition pass renders into the primary
/// framebuffer's attachment 0 while sampling its attachment 1, which Vulkan only
/// allows with attachment 1 out of the scope. A draw that samples a bound slot
/// whose draw buffer is off therefore leaves that slot out (a null attachment,
/// keeping fragment output N aimed at slot N) until its draw buffer is selected
/// again or the framebuffer is rebound; those restarts are feedback splits.
/// </summary>
internal sealed unsafe class RenderTargetManager : IDisposable
{
    private readonly VulkanContext _context;
    private readonly TextureManager _textures;
    private readonly GlStateTracker _state;

    /// <summary>Every attachment of a scope moves in one barrier command before vkCmdBeginRendering.</summary>
    private readonly BarrierBatcher _barriers;

    private readonly List<VulkanFramebuffer?> _framebuffers = new();
    private readonly Stack<int> _freeIds = new();

    private VulkanFramebuffer? _bound;
    private bool _renderingActive;
    private bool _disposed;

    /// <summary>How many rendering scopes have been opened, for diagnostics.</summary>
    public long ScopesOpened { get; private set; }

    /// <summary>
    /// Restarts that reopened exactly the attachment set they closed (views and
    /// layouts). Draw-buffer and colour-mask changes never restart, so this stays 0.
    /// </summary>
    public long MaskRestarts { get; private set; }

    /// <summary>Restarts that left a sampled, draw-buffer-excluded slot out of the scope or let it rejoin.</summary>
    public long FeedbackSplits { get; private set; }

    // What the open scope was begun with, to recognise a restart that changed nothing.
    private readonly ImageView[] _openViews = new ImageView[GlStateTracker.MaxColorAttachments];
    private int _openCount = -1;
    private ImageView _openDepthView;
    private ImageLayout _openDepthLayout;

    /// <summary>
    /// Runs right after <c>vkCmdBeginRendering</c>, inside the new scope. The
    /// occlusion query ring resumes a query the previous scope's end suspended:
    /// GL counts samples across framebuffer changes, Vulkan only within a scope.
    /// </summary>
    public Action<CommandBuffer>? ScopeOpened;

    /// <summary>Runs right before <c>vkCmdEndRendering</c>, still inside the scope (ends a running query).</summary>
    public Action<CommandBuffer>? ScopeClosing;

    /// <summary>Runs right after <c>vkCmdEndRendering</c>, outside any scope (where a query pool may be reset).</summary>
    public Action<CommandBuffer>? ScopeClosed;

    /// <summary>The frame graph: declared passes, the plan and promoted clears (Phase 2 step 2).</summary>
    private readonly FrameGraph _graph;

    /// <summary>Opens the one scope of each pass on the frame-graph path.</summary>
    private readonly PassRecorder _recorder;

    public RenderTargetManager(VulkanContext context, TextureManager textures, GlStateTracker state,
        FrameGraph? graph = null)
    {
        _context = context;
        _textures = textures;
        _state = state;
        _barriers = textures.CreateBatcher();
        _graph = graph ?? new FrameGraph { Enabled = false };
        _recorder = new PassRecorder(context, textures, _barriers, _graph);

        // Index 0 is the default framebuffer, installed separately.
        _framebuffers.Add(null);
    }

    public VulkanFramebuffer? Bound => _bound;

    public FrameGraph Graph => _graph;

    /// <summary>The declared pass, while one is current (frame-graph path only).</summary>
    public PassDeclaration? DeclaredPass => _recorder.Declared;
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
            AttachmentSlot previous = framebuffer.Color[attachmentIndex];
            if (previous.TextureId == textureId && previous.Layer == layer) return;
            framebuffer.Color[attachmentIndex] = new AttachmentSlot { TextureId = textureId, Layer = layer };
            framebuffer.SampledExclusion &= ~(1u << attachmentIndex);
        }

        framebuffer.FormatsId = -1;

        // GL attaches to the bound framebuffer, so an open scope no longer
        // describes the target: the next draw must reopen on the new views.
        if (_bound == framebuffer) _needsRestart = true;
    }

    /// <summary>
    /// Records glDrawBuffers. The scope keeps its attachments: only the effective
    /// write masks change, which the next draw emits (C4). The one restart is a
    /// slot a sampling draw left out whose draw buffer is selected again: it has
    /// to rejoin the scope before anything can be written into it.
    /// </summary>
    public void SetDrawBuffers(int framebufferId, uint mask)
    {
        VulkanFramebuffer? framebuffer = Get(framebufferId);
        if (framebuffer == null || framebuffer.DrawBufferMask == mask) return;

        framebuffer.DrawBufferMask = mask;

        uint rejoining = framebuffer.SampledExclusion & mask;
        if (rejoining == 0) return;

        framebuffer.SampledExclusion &= ~rejoining;
        framebuffer.FormatsId = -1;
        if (_bound == framebuffer && _renderingActive)
        {
            _needsRestart = true;
            NoteFeedbackSplit();
        }
    }

    /// <summary>
    /// A draw is about to sample <paramref name="textureId" />. If that texture is a
    /// bound colour slot of the bound framebuffer whose draw buffer is off, the slot
    /// leaves the scope (closing an open one that holds it) so the caller can move
    /// it to a shader-readable layout. Slots whose draw buffer is on are feedback
    /// the caller resolves with a snapshot instead (<see cref="IsAttachmentOfBound" />).
    /// </summary>
    public void ExcludeSampledAttachment(CommandBuffer commandBuffer, int textureId)
    {
        VulkanFramebuffer? framebuffer = _bound;
        if (framebuffer == null || textureId <= 0) return;

        uint slots = 0;
        for (int i = 0; i < framebuffer.Color.Length; i++)
        {
            if (framebuffer.Color[i].TextureId != textureId) continue;
            if (((framebuffer.DrawBufferMask >> i) & 1) != 0) continue;
            slots |= 1u << i;
        }

        // A slot the declared pass already leaves out is not in the scope: nothing to exclude, no split.
        uint newlyExcluded = slots & ~(framebuffer.SampledExclusion | framebuffer.PassExclusion);
        if (newlyExcluded == 0) return;

        framebuffer.SampledExclusion |= newlyExcluded;
        framebuffer.FormatsId = -1;
        if (_renderingActive)
        {
            EndRendering(commandBuffer);
            NoteFeedbackSplit();
        }
    }

    private void NoteFeedbackSplit()
    {
        FeedbackSplits++;
        VulkanStats.NoteFeedbackSplit();
    }

    /// <summary>Whether colour slot <paramref name="index" /> is part of the scope the framebuffer opens.</summary>
    private static bool InScope(VulkanFramebuffer framebuffer, int index) =>
        framebuffer.Color[index].IsBound &&
        (((framebuffer.SampledExclusion | framebuffer.PassExclusion) >> index) & 1) == 0;

    private bool _needsRestart;

    /// <summary>
    /// Whether the scope holds its depth attachment in the read-only layout.
    ///
    /// GL lets a pass sample the depth buffer it is drawing against as long as
    /// depth writes are off - the liquid pass reads scene depth that way to fade
    /// water at its edges. Vulkan allows the same only if the attachment is in
    /// DEPTH_READ_ONLY_OPTIMAL for both the attachment and the descriptor, so
    /// the scope switches layout for such draws and back for the next that
    /// writes depth.
    /// </summary>
    public bool DepthReadOnly { get; private set; }

    public void SetDepthReadOnly(bool readOnly)
    {
        if (DepthReadOnly == readOnly) return;
        DepthReadOnly = readOnly;
        if (_renderingActive) _needsRestart = true;
    }

    /// <summary>Whether a texture is the bound framebuffer's depth attachment.</summary>
    public bool IsBoundDepth(int textureId) =>
        _bound != null && textureId > 0 && _bound.DepthTextureId == textureId;

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
            // Left out of the declared pass's scope: sampled directly, not feedback.
            if (((_bound.PassExclusion >> i) & 1) != 0) continue;
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
        // A pass is declared on one target; binding another ends it.
        if (_recorder.Declared != null) ClearPassDeclaration();
        _bound = framebuffer;
        _needsRestart = false;

        // A new bind starts a new use of the target: every bound slot is back in.
        if (framebuffer != null && framebuffer.SampledExclusion != 0)
        {
            framebuffer.SampledExclusion = 0;
            framebuffer.FormatsId = -1;
        }
    }

    public void Delete(int framebufferId)
    {
        VulkanFramebuffer? framebuffer = Get(framebufferId);
        if (framebuffer == null) return;

        if (ReferenceEquals(framebuffer, _bound)) _bound = null;
        if (ReferenceEquals(framebuffer, _recorder.DeclaredOn)) ClearPassDeclaration();
        _framebuffers[framebufferId] = null;
        _freeIds.Push(framebufferId);
    }

    // ------------------------------------------------------------------- passes

    /// <summary>
    /// Declares a frame-graph pass on <paramref name="framebufferId" /> (0: the bound
    /// target), binding it. The pass's scope opens lazily at its first draw or in-pass
    /// clear, exactly once unless something forces a split. Re-declaring the current
    /// pass (same name, target and slots) changes nothing. With the frame graph off this
    /// only binds, so the same frame code drives both paths.
    /// </summary>
    public void DeclarePass(CommandBuffer commandBuffer, PassDeclaration declaration, int framebufferId)
    {
        if (!_graph.Enabled)
        {
            if (framebufferId > 0) Bind(commandBuffer, framebufferId);
            return;
        }

        VulkanFramebuffer? target = framebufferId > 0 ? Get(framebufferId) : _bound;
        if (target == null)
        {
            EndPass(commandBuffer);
            return;
        }

        PassDeclaration? current = _recorder.Declared;
        if (current != null && ReferenceEquals(_recorder.DeclaredOn, target) && ReferenceEquals(_bound, target) &&
            current.Name == declaration.Name && current.ColorSlots == declaration.ColorSlots)
        {
            return;
        }

        EndPass(commandBuffer);
        Bind(commandBuffer, target.Id);

        // A new pass is a new use of the target: every bound slot is back in, except
        // the slots the pass leaves out so they can be sampled.
        uint exclusion = 0;
        for (int i = 0; i < target.Color.Length; i++)
        {
            if (target.Color[i].IsBound && ((declaration.ColorSlots >> i) & 1) == 0) exclusion |= 1u << i;
        }
        if (target.SampledExclusion != 0 || target.PassExclusion != exclusion)
        {
            target.SampledExclusion = 0;
            target.PassExclusion = exclusion;
            target.FormatsId = -1;
        }
        _recorder.Declare(declaration, target);
    }

    /// <summary>Ends the current pass, declared or not: closes its scope. No-op with the frame graph off.</summary>
    public void EndPass(CommandBuffer commandBuffer)
    {
        if (!_graph.Enabled) return;
        EndRendering(commandBuffer);
        ClearPassDeclaration();
    }

    private void ClearPassDeclaration()
    {
        VulkanFramebuffer? target = _recorder.DeclaredOn;
        if (target != null && target.PassExclusion != 0)
        {
            target.PassExclusion = 0;
            target.FormatsId = -1;
            if (ReferenceEquals(target, _bound) && _renderingActive) _needsRestart = true;
        }
        _recorder.ClearDeclaration();
    }

    /// <summary>
    /// Records the clears promoted into <paramref name="texture" /> as clear-image commands,
    /// closing an open scope first: the texture is about to be used some other way (sampled,
    /// copied, read back, uploaded to) before any pass attached it.
    /// </summary>
    public void FlushPendingClears(CommandBuffer commandBuffer, VulkanTexture texture)
    {
        if (!_graph.HasPendingClears || !_graph.HasPendingClear(texture)) return;
        EndRendering(commandBuffer);
        _recorder.FlushClears(commandBuffer, texture);
    }

    /// <summary>Every clear still pending at the end of the frame lands as a clear-image command.</summary>
    public void FlushAllPendingClears(CommandBuffer commandBuffer)
    {
        if (!_graph.HasPendingClears) return;
        EndRendering(commandBuffer);
        _recorder.FlushClears(commandBuffer, null);
    }

    /// <summary>A deleted texture's pending clears are dropped.</summary>
    public void DropPendingClears(VulkanTexture texture) => _graph.Drop(texture);

    // ------------------------------------------------------------------- scopes

    /// <summary>
    /// Opens a rendering scope if one is not already open, transitioning every
    /// participating attachment into its attachment layout. Every bound colour
    /// slot participates, whatever its draw buffer, unless a sampling draw left
    /// it out (<see cref="ExcludeSampledAttachment" />); that caller moves it to
    /// a shader-readable layout itself.
    /// </summary>
    public void EnsureRendering(CommandBuffer commandBuffer)
    {
        if (_renderingActive && !_needsRestart) return;
        if (_bound == null) return;

        bool restarting = _renderingActive;
        if (_renderingActive) EndRendering(commandBuffer);

        VulkanFramebuffer framebuffer = _bound;
        int highest = HighestScopeAttachment(framebuffer);
        int count = highest + 1;
        bool graph = _graph.Enabled;

        var attachments = new RenderingAttachmentInfo[Math.Max(count, 0)];
        // Frame-graph path: the pass recorder queues the barriers and picks the load ops.
        VulkanTexture?[]? scopeColour = graph ? new VulkanTexture?[attachments.Length] : null;
        VulkanTexture? scopeDepth = null;

        for (int i = 0; i < count; i++)
        {
            AttachmentSlot slot = framebuffer.Color[i];

            if (!InScope(framebuffer, i))
            {
                // A null view keeps fragment output i pointed at slot i: an
                // unbound slot, or one a draw samples while its draw buffer is off.
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

            // Blend state can change inside the scope, so the attachment is
            // declared for the widest colour use (read and write).
            if (graph) scopeColour![i] = texture;
            else _textures.Require(_barriers, commandBuffer, texture, ResourceUsage.ColorBlend);

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
                ImageLayout depthLayout = DepthReadOnly
                    ? ImageLayout.DepthReadOnlyOptimal
                    : ImageLayout.DepthAttachmentOptimal;
                // Read-only depth may be sampled by the draws of this scope.
                if (graph) scopeDepth = depth;
                else _textures.Require(_barriers, commandBuffer, depth,
                    DepthReadOnly ? ResourceUsage.DepthReadOnlySampled : ResourceUsage.DepthWrite);
                depthAttachment = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = depth.View,
                    ImageLayout = depthLayout,
                    LoadOp = AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                };
                hasDepth = true;
            }
        }

        if (graph)
        {
            _recorder.Prepare(commandBuffer, framebuffer, scopeColour!, scopeDepth, DepthReadOnly,
                FormatsIdOf(framebuffer), _framebuffers, attachments, ref depthAttachment);
        }

        _barriers.Flush(commandBuffer);

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

        // A restart that reopened the very set it closed bought nothing: with
        // write masks carrying draw buffers and motion windows this never happens.
        ImageView depthView = hasDepth ? depthAttachment.ImageView : default;
        ImageLayout depthLayoutOpened = hasDepth ? depthAttachment.ImageLayout : ImageLayout.Undefined;
        bool unchanged = restarting && count == _openCount && depthView.Handle == _openDepthView.Handle
            && depthLayoutOpened == _openDepthLayout;
        for (int i = 0; unchanged && i < count; i++)
        {
            unchanged = attachments[i].ImageView.Handle == _openViews[i].Handle;
        }
        if (unchanged)
        {
            MaskRestarts++;
            VulkanStats.NoteMaskRestart();
        }
        _openCount = count;
        _openDepthView = depthView;
        _openDepthLayout = depthLayoutOpened;
        for (int i = 0; i < count; i++) _openViews[i] = attachments[i].ImageView;

        _renderingActive = true;
        _needsRestart = false;
        ScopesOpened++;
        VulkanStats.NoteScopeOpened();
        ScopeOpened?.Invoke(commandBuffer);
    }

    /// <summary>
    /// Closes the scope. Uploads and layout transitions have to happen outside
    /// one, so this is called before them and the scope reopens on the next draw.
    /// </summary>
    public void EndRendering(CommandBuffer commandBuffer)
    {
        if (!_renderingActive) return;
        ScopeClosing?.Invoke(commandBuffer);
        _context.Api.CmdEndRendering(commandBuffer);
        _renderingActive = false;
        ScopeClosed?.Invoke(commandBuffer);
    }

    // ------------------------------------------------------------------- clears

    public void ClearColor(CommandBuffer commandBuffer, int attachment, float r, float g, float b, float a)
    {
        if (_bound == null) return;

        // glClearBuffer names a draw buffer, and one that glDrawBuffers left out
        // is simply not cleared - the game clears attachments 2 and 3 of the
        // primary target while only 0 and 1 are selected. Nor does GL clear
        // through an all-false glColorMask. A clear on an attachment whose
        // effective write mask is zero is a no-op on every path (CLAUDE.md rule 9).
        if ((uint)attachment >= (uint)_bound.Color.Length) return;
        if (!_bound.Color[attachment].IsBound) return;
        if ((_bound.DrawBufferMask & (1u << attachment)) == 0) return;
        if (_state.ColorMask == 0) return;

        if (_graph.Enabled && !ClearColorOnGraph(commandBuffer, attachment, r, g, b, a)) return;
        if (!_graph.Enabled) EnsureRendering(commandBuffer);
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

    /// <summary>
    /// The frame-graph half of a colour clear. A slot left out by the declared pass is not in
    /// the scope, but its draw buffer is on, so its texture is cleared through a promoted clear.
    /// Inside an open pass the clear stays vkCmdClearAttachments and is counted (returns
    /// true with the scope open). With no pass open a full-mask clear is promoted into the
    /// next scope attaching the image (returns false); a partial glColorMask clear opens
    /// the scope and clears in it, as before.
    /// </summary>
    private bool ClearColorOnGraph(CommandBuffer commandBuffer, int attachment, float r, float g, float b, float a)
    {
        VulkanFramebuffer target = _bound!;
        if (!InScope(target, attachment))
        {
            // Left out by the declared pass while its draw buffer is on: GL clears the
            // texture, so the clear is promoted and lands before the texture's next use.
            if (((target.PassExclusion >> attachment) & 1) != 0)
            {
                VulkanTexture? excluded = _textures.Get(target.Color[attachment].TextureId);
                if (excluded != null)
                {
                    _graph.PromoteColorClear(excluded, target.Color[attachment].Layer, r, g, b, a);
                }
            }
            return false;
        }

        if (!_renderingActive || _needsRestart)
        {
            const ColorComponentFlags all = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                            ColorComponentFlags.BBit | ColorComponentFlags.ABit;
            if (_state.ColorMask == all)
            {
                VulkanTexture? texture = _textures.Get(target.Color[attachment].TextureId);
                if (texture == null) return false;
                EndRendering(commandBuffer);
                _graph.PromoteColorClear(texture, target.Color[attachment].Layer, r, g, b, a);
                return false;
            }
            EnsureRendering(commandBuffer);
            if (!_renderingActive) return false;
        }

        _graph.NoteInPassClear();
        return true;
    }

    public void ClearDepth(CommandBuffer commandBuffer, float depth)
    {
        if (_bound == null || _bound.DepthTextureId <= 0) return;

        if (_graph.Enabled)
        {
            VulkanTexture? texture = _textures.Get(_bound.DepthTextureId);
            if (texture == null) return;
            if (!_renderingActive || _needsRestart || DepthReadOnly)
            {
                // No pass open (or the scope is about to change): LOAD_OP_CLEAR on the next scope.
                EndRendering(commandBuffer);
                SetDepthReadOnly(false);
                _graph.PromoteDepthClear(texture, depth);
                return;
            }
            _graph.NoteInPassClear();
        }

        // A read-only depth attachment cannot be cleared; a clear is a write.
        SetDepthReadOnly(false);
        if (!_graph.Enabled) EnsureRendering(commandBuffer);
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
    /// The attachment formats of the bound target, for the pipeline key. They
    /// follow the scope, not the draw buffers, so a mask toggle keeps the id: an
    /// unbound or sample-excluded slot reports <see cref="Format.Undefined" />,
    /// agreeing with the null attachment the scope was opened with.
    /// </summary>
    public int FormatsIdOf(VulkanFramebuffer framebuffer)
    {
        if (framebuffer.FormatsId >= 0) return framebuffer.FormatsId;

        int count = HighestScopeAttachment(framebuffer) + 1;
        var colorFormats = new Format[Math.Max(count, 0)];

        for (int i = 0; i < count; i++)
        {
            AttachmentSlot slot = framebuffer.Color[i];
            VulkanTexture? texture = InScope(framebuffer, i) ? _textures.Get(slot.TextureId) : null;
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

    /// <summary>Colour attachments of the scope the framebuffer opens (highest participating slot + 1).</summary>
    public int EnabledAttachmentCount(VulkanFramebuffer framebuffer) =>
        HighestScopeAttachment(framebuffer) + 1;

    private static int HighestScopeAttachment(VulkanFramebuffer framebuffer)
    {
        int highest = -1;
        for (int i = 0; i < GlStateTracker.MaxColorAttachments; i++)
        {
            if (InScope(framebuffer, i)) highest = i;
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
