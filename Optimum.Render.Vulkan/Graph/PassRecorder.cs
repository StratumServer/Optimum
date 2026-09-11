using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>
/// Opens the one rendering scope of a pass (Vulkan-native plan, Phase 2 step 2).
///
/// <see cref="RenderTargetManager" /> owns the bound target and decides when a scope
/// has to open; on the frame-graph path it hands the attachment set to
/// <see cref="Prepare" />, which, before <c>vkCmdBeginRendering</c> and outside any
/// scope:
/// <list type="number">
/// <item>records the standalone clears a read or a read-only depth attachment needs
/// (a clear promoted into an image that is read before any pass attaches it);</item>
/// <item>queues the pass-entry barriers: every declared read (or, for an
/// <see cref="PassFlags.OpenSampling" /> pass, every render-target texture outside
/// the pass) to SHADER_READ_ONLY, every attachment to its attachment usage;</item>
/// <item>records the pass signature with <see cref="FrameGraph" /> (a second scope
/// in one declared pass is a split, counted, not a new pass);</item>
/// <item>chooses each attachment's load op: CLEAR when a clear was promoted into
/// it, otherwise the plan's op on the pass's first scope, LOAD on a split.</item>
/// </list>
/// The caller flushes the batcher once and begins rendering, so every barrier of
/// the pass is one vkCmdPipelineBarrier2.
/// </summary>
internal sealed unsafe class PassRecorder
{
    private readonly VulkanContext _context;
    private readonly TextureManager _textures;
    private readonly BarrierBatcher _barriers;
    private readonly FrameGraph _graph;
    private readonly List<PendingClear> _clears = new();
    private readonly List<AttachmentUse> _uses = new();

    public PassRecorder(VulkanContext context, TextureManager textures, BarrierBatcher barriers, FrameGraph graph)
    {
        _context = context;
        _textures = textures;
        _barriers = barriers;
        _graph = graph;
    }

    public FrameGraph Graph => _graph;

    /// <summary>The declared pass, while one is current.</summary>
    public PassDeclaration? Declared { get; private set; }

    /// <summary>The target the declared pass was declared on.</summary>
    public VulkanFramebuffer? DeclaredOn { get; private set; }

    /// <summary>Whether the declared pass has opened its scope.</summary>
    public bool Opened { get; private set; }

    public void Declare(PassDeclaration declaration, VulkanFramebuffer framebuffer)
    {
        Declared = declaration;
        DeclaredOn = framebuffer;
        Opened = false;
    }

    public void ClearDeclaration()
    {
        Declared = null;
        DeclaredOn = null;
        Opened = false;
    }

    /// <summary>
    /// Records every clear pending on <paramref name="texture" /> (null: on every
    /// texture) as a clear-image command. No rendering scope may be open.
    /// </summary>
    public void FlushClears(CommandBuffer commandBuffer, VulkanTexture? texture)
    {
        if (!_graph.HasPendingClears) return;
        _clears.Clear();
        _graph.TakeStandalone(texture, _clears);
        foreach (PendingClear clear in _clears)
        {
            VulkanTexture target = clear.Texture;
            _textures.Require(_barriers, commandBuffer, target, ResourceUsage.TransferDst);
            _barriers.Flush(commandBuffer);
            if (clear.Depth)
            {
                var value = new ClearDepthStencilValue(clear.R, 0);
                var range = new ImageSubresourceRange(target.Aspect, 0, 1, 0, 1);
                _context.Api.CmdClearDepthStencilImage(commandBuffer, target.Image, ImageLayout.TransferDstOptimal,
                    &value, 1, &range);
            }
            else
            {
                var value = new ClearColorValue(clear.R, clear.G, clear.B, clear.A);
                var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, clear.Layer, 1);
                _context.Api.CmdClearColorImage(commandBuffer, target.Image, ImageLayout.TransferDstOptimal,
                    &value, 1, &range);
            }
            _graph.NoteStandaloneClear();
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write("standalone clear image=" + target.Image.Handle.ToString("x") +
                    (clear.Depth ? " depth=" + clear.R : " layer=" + clear.Layer + " rgba=" + clear.R + "," + clear.G +
                        "," + clear.B + "," + clear.A));
            }
        }
        _clears.Clear();
    }

    /// <summary>
    /// Queues the barriers and chooses the load ops of the scope about to open on
    /// <paramref name="framebuffer" />. <paramref name="colour" /> holds the texture
    /// of every slot in the scope (null for a slot left out) and is filled into
    /// <paramref name="attachments" />' load ops and clear values; the views and
    /// layouts are the caller's.
    /// </summary>
    public void Prepare(CommandBuffer commandBuffer, VulkanFramebuffer framebuffer, VulkanTexture?[] colour,
        VulkanTexture? depth, bool depthReadOnly, int formatsId, IReadOnlyList<VulkanFramebuffer?> framebuffers,
        RenderingAttachmentInfo[] attachments, ref RenderingAttachmentInfo depthAttachment)
    {
        PassDeclaration? declaration = ReferenceEquals(DeclaredOn, framebuffer) ? Declared : null;
        bool split = declaration != null && Opened;

        // 1. Clears promoted into images this pass reads: they must land before the read.
        if (_graph.HasPendingClears)
        {
            if (declaration != null)
            {
                foreach (int id in declaration.Reads)
                {
                    VulkanTexture? read = _textures.Get(id);
                    if (read != null && !InScope(read, colour, depth)) FlushClears(commandBuffer, read);
                }
                if ((declaration.Flags & PassFlags.OpenSampling) != 0)
                {
                    ForEachOpenSamplingCandidate(commandBuffer, framebuffers, colour, depth, flushClears: true);
                }
            }
            // A read-only depth attachment cannot take LOAD_OP_CLEAR.
            if (depth != null && depthReadOnly) FlushClears(commandBuffer, depth);
        }

        // 2. Pass-entry barriers for the reads.
        if (declaration != null)
        {
            foreach (int id in declaration.Reads)
            {
                VulkanTexture? read = _textures.Get(id);
                if (read == null || InScope(read, colour, depth)) continue;
                _textures.Require(_barriers, commandBuffer, read, ResourceUsage.SampleFragment);
            }
            if ((declaration.Flags & PassFlags.OpenSampling) != 0)
            {
                ForEachOpenSamplingCandidate(commandBuffer, framebuffers, colour, depth, flushClears: false);
            }
        }

        // 3. The signature, and the pass index the plan is consulted with.
        uint transient = declaration?.TransientSlots ?? 0;
        _uses.Clear();
        for (int i = 0; i < colour.Length; i++)
        {
            if (colour[i] == null) continue;
            bool isTransient = ((transient >> i) & 1) != 0;
            _uses.Add(new AttachmentUse(framebuffer.Color[i].TextureId,
                isTransient ? ResourceUsage.ColorWrite : ResourceUsage.ColorBlend, isTransient));
        }
        if (depth != null)
        {
            _uses.Add(new AttachmentUse(framebuffer.DepthTextureId,
                depthReadOnly ? ResourceUsage.DepthReadOnlySampled : ResourceUsage.DepthWrite, false));
        }

        int passIndex;
        if (split)
        {
            passIndex = -1;
            bool allowed = (declaration!.Flags & PassFlags.AllowSplit) != 0;
            _graph.NoteSplit(allowed);
            if (!allowed && RenderTrace.Enabled)
            {
                RenderTrace.Write("pass split: '" + declaration.Name + "' reopened its scope on framebuffer " +
                    framebuffer.Id);
            }
        }
        else
        {
            var signature = new PassSignature
            {
                NameId = _graph.NameId(declaration?.Name ?? "~implicit"),
                Attachments = _uses.ToArray(),
                Reads = declaration != null ? (int[])declaration.Reads.Clone() : Array.Empty<int>(),
                Width = (int)framebuffer.Width,
                Height = (int)framebuffer.Height,
                FormatsId = formatsId,
            };
            passIndex = _graph.OpenPass(signature, declaration != null);
            if (declaration != null) Opened = true;
        }

        // 4. Attachment barriers and load ops.
        int use = 0;
        for (int i = 0; i < colour.Length; i++)
        {
            VulkanTexture? texture = colour[i];
            if (texture == null) continue;

            AttachmentUse attachment = _uses[use];
            if (_graph.TakeForLoad(texture, framebuffer.Color[i].Layer, depth: false, out PendingClear clear))
            {
                attachments[i].LoadOp = AttachmentLoadOp.Clear;
                attachments[i].ClearValue = new ClearValue(new ClearColorValue(clear.R, clear.G, clear.B, clear.A));
            }
            else
            {
                attachments[i].LoadOp = passIndex >= 0 ? _graph.PlannedLoad(passIndex, use) : AttachmentLoadOp.Load;
            }
            // LOAD reads the attachment: a plain-write declaration only drops the read
            // access when the contents are not loaded (sync validation: read-after-write).
            ResourceUsage usage = attachment.Usage == ResourceUsage.ColorWrite && attachments[i].LoadOp == AttachmentLoadOp.Load
                ? ResourceUsage.ColorBlend
                : attachment.Usage;
            _textures.Require(_barriers, commandBuffer, texture, usage);
            use++;
        }

        if (depth != null)
        {
            _textures.Require(_barriers, commandBuffer, depth, _uses[use].Usage);
            if (!depthReadOnly && _graph.TakeForLoad(depth, 0, depth: true, out PendingClear clear))
            {
                depthAttachment.LoadOp = AttachmentLoadOp.Clear;
                depthAttachment.ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(clear.R, 0));
            }
            else
            {
                depthAttachment.LoadOp = passIndex >= 0 ? _graph.PlannedLoad(passIndex, use) : AttachmentLoadOp.Load;
            }
        }
    }

    private static bool InScope(VulkanTexture texture, VulkanTexture?[] colour, VulkanTexture? depth)
    {
        if (ReferenceEquals(texture, depth)) return true;
        for (int i = 0; i < colour.Length; i++)
        {
            if (ReferenceEquals(colour[i], texture)) return true;
        }
        return false;
    }

    /// <summary>
    /// Every render-target texture outside the scope that is not already shader-readable:
    /// what a mod-hosted stage might sample.
    /// </summary>
    private void ForEachOpenSamplingCandidate(CommandBuffer commandBuffer, IReadOnlyList<VulkanFramebuffer?> framebuffers,
        VulkanTexture?[] colour, VulkanTexture? depth, bool flushClears)
    {
        for (int f = 0; f < framebuffers.Count; f++)
        {
            VulkanFramebuffer? other = framebuffers[f];
            if (other == null) continue;
            for (int i = 0; i <= other.Color.Length; i++)
            {
                int id = i < other.Color.Length ? other.Color[i].TextureId : other.DepthTextureId;
                if (id <= 0) continue;
                VulkanTexture? texture = _textures.Get(id);
                if (texture == null || InScope(texture, colour, depth)) continue;
                if (flushClears)
                {
                    FlushClears(commandBuffer, texture);
                }
                else if (texture.Layout != ImageLayout.ShaderReadOnlyOptimal)
                {
                    _textures.Require(_barriers, commandBuffer, texture, ResourceUsage.SampleFragment);
                }
            }
        }
    }
}
