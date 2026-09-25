using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>
/// The synchronization state of one subresource (mip level, array layer).
/// </summary>
/// <param name="Layout">The layout the subresource is in.</param>
/// <param name="WriteStage">The stage of the last write, or none.</param>
/// <param name="WriteAccess">The access of that write.</param>
/// <param name="VisibleStages">The stages the current contents were made visible to by a barrier.</param>
/// <param name="ReadStages">The stages that read since the last barrier or write.</param>
/// <param name="ReadAccess">The accesses of those reads.</param>
internal readonly record struct SubresourceState(
    ImageLayout Layout,
    PipelineStageFlags2 WriteStage,
    AccessFlags2 WriteAccess,
    PipelineStageFlags2 VisibleStages,
    PipelineStageFlags2 ReadStages,
    AccessFlags2 ReadAccess)
{
    public static SubresourceState Undefined => new(ImageLayout.Undefined,
        PipelineStageFlags2.None, AccessFlags2.None, PipelineStageFlags2.None, PipelineStageFlags2.None, AccessFlags2.None);
}

/// <summary>The two sides of one barrier, without its subresource range.</summary>
internal readonly record struct BarrierSides(
    ImageLayout OldLayout,
    ImageLayout NewLayout,
    PipelineStageFlags2 SrcStage,
    AccessFlags2 SrcAccess,
    PipelineStageFlags2 DstStage,
    AccessFlags2 DstAccess);

/// <summary>One barrier over a rectangle of subresources.</summary>
internal readonly record struct ImageTransition(uint BaseMip, uint MipCount, uint BaseLayer, uint LayerCount, BarrierSides Sides);

/// <summary>
/// Per-subresource layout, last write stage and access, the stages that write is
/// visible to, and the read stages since, for one image. Derives the barriers a
/// new use needs:
/// <list type="bullet">
/// <item>a layout change always needs one; its source side names the last write
/// and the reads since (so the write is made available and the reads complete),
/// its destination side the new use;</item>
/// <item>read after write (RAW) in the same layout needs one only when the
/// reader's stage is not among the stages the write is visible to;</item>
/// <item>write after read (WAR) and write after write (WAW) in the same layout
/// need ordering even when the stages match;</item>
/// <item>read after read needs no barrier while layout and visibility agree.</item>
/// </list>
/// One entry covers the whole image while every subresource agrees; a use of a
/// sub-range splits it into per-subresource entries, and a use that makes them
/// agree again merges them back. Not thread-safe: <see cref="BarrierBatcher" />
/// locks the tracker around each call.
/// </summary>
internal sealed class ResourceStateTracker
{
    private SubresourceState _whole = SubresourceState.Undefined;
    private SubresourceState[]? _split;

    public ResourceStateTracker(uint mipLevels, uint layers, bool depth)
    {
        MipLevels = Math.Max(1, mipLevels);
        Layers = Math.Max(1, layers);
        Depth = depth;
    }

    public uint MipLevels { get; }
    public uint Layers { get; }
    public bool Depth { get; }

    /// <summary>Whether sub-ranges currently differ (one entry per subresource).</summary>
    public bool IsSplit => _split != null;

    /// <summary>The layout of the whole image, or UNDEFINED while sub-ranges differ.</summary>
    public ImageLayout Layout => _split == null ? _whole.Layout : ImageLayout.Undefined;

    public SubresourceState StateOf(uint mip, uint layer) =>
        _split == null ? _whole : _split[mip * Layers + layer];

    /// <summary>Forgets everything: the image's contents are undefined (a swapchain image before its frame).</summary>
    public void Reset() => Reset(PipelineStageFlags2.None);

    /// <summary>
    /// Forgets the contents, keeping one prior use: <paramref name="priorStage" /> is
    /// where something outside the command buffer last touched the image. A
    /// swapchain image was accessed by vkAcquireNextImageKHR, and the submission
    /// waits on the acquire semaphore at a stage, so the first barrier must name
    /// that stage on its source side or synchronization validation reports
    /// write-after-read against the acquire.
    /// </summary>
    public void Reset(PipelineStageFlags2 priorStage)
    {
        _whole = SubresourceState.Undefined with { VisibleStages = priorStage };
        _split = null;
    }

    /// <summary>
    /// The contents stop mattering (an aliased transient starts a new lifetime): the
    /// next use of every subresource transitions from UNDEFINED, even into the layout
    /// it is already in. The uses recorded so far stay, so that barrier's source side
    /// still names them.
    /// </summary>
    public void Discard()
    {
        if (_split == null)
        {
            _whole = _whole with { Layout = ImageLayout.Undefined };
            return;
        }
        for (int i = 0; i < _split.Length; i++) _split[i] = _split[i] with { Layout = ImageLayout.Undefined };
    }

    /// <summary>
    /// Records a use of a subresource range and appends the barriers it needs to
    /// <paramref name="output" />, as few rectangles as the state allows.
    /// <paramref name="discard" /> says the contents do not matter, so a layout
    /// change starts from UNDEFINED. Returns how many were appended.
    /// </summary>
    public int Require(uint baseMip, uint mipCount, uint baseLayer, uint layerCount, ResourceUsage usage,
        bool discard, List<ImageTransition> output)
    {
        if (baseMip >= MipLevels || baseLayer >= Layers) return 0;
        mipCount = Math.Min(mipCount, MipLevels - baseMip);
        layerCount = Math.Min(layerCount, Layers - baseLayer);
        if (mipCount == 0 || layerCount == 0) return 0;

        UsageState target = UsageState.For(usage, Depth);
        (PipelineStageFlags2 writeStage, AccessFlags2 writeAccess) = UsageState.WriteOf(usage, Depth);

        bool whole = baseMip == 0 && mipCount == MipLevels && baseLayer == 0 && layerCount == Layers;
        if (_split == null && whole)
        {
            if (!Advance(ref _whole, target, writeStage, writeAccess, discard, out BarrierSides sides)) return 0;
            output.Add(new ImageTransition(0, MipLevels, 0, Layers, sides));
            return 1;
        }

        if (_split == null)
        {
            _split = new SubresourceState[MipLevels * Layers];
            Array.Fill(_split, _whole);
        }

        int before = output.Count;
        int firstRectangle = output.Count;
        for (uint mip = baseMip; mip < baseMip + mipCount; mip++)
        {
            uint runStart = 0;
            BarrierSides runSides = default;
            bool inRun = false;
            for (uint layer = baseLayer; layer < baseLayer + layerCount; layer++)
            {
                ref SubresourceState state = ref _split[mip * Layers + layer];
                bool needed = Advance(ref state, target, writeStage, writeAccess, discard, out BarrierSides sides);
                if (inRun && (!needed || sides != runSides))
                {
                    AddRectangle(output, firstRectangle, mip, runStart, layer - runStart, runSides);
                    inRun = false;
                }
                if (needed && !inRun)
                {
                    runStart = layer;
                    runSides = sides;
                    inRun = true;
                }
            }
            if (inRun) AddRectangle(output, firstRectangle, mip, runStart, baseLayer + layerCount - runStart, runSides);
        }

        TryMerge();
        return output.Count - before;
    }

    /// <summary>
    /// Adds a one-mip rectangle, extending the rectangle of the previous mip with
    /// the same layer span and sides when there is one.
    /// </summary>
    private static void AddRectangle(List<ImageTransition> output, int first, uint mip, uint baseLayer,
        uint layerCount, BarrierSides sides)
    {
        for (int i = first; i < output.Count; i++)
        {
            ImageTransition candidate = output[i];
            if (candidate.BaseLayer == baseLayer && candidate.LayerCount == layerCount &&
                candidate.BaseMip + candidate.MipCount == mip && candidate.Sides == sides)
            {
                output[i] = candidate with { MipCount = candidate.MipCount + 1 };
                return;
            }
        }
        output.Add(new ImageTransition(mip, 1, baseLayer, layerCount, sides));
    }

    private void TryMerge()
    {
        if (_split == null) return;
        SubresourceState first = _split[0];
        for (int i = 1; i < _split.Length; i++)
        {
            if (_split[i] != first) return;
        }
        _whole = first;
        _split = null;
    }

    /// <summary>
    /// Applies one use to one subresource. Returns whether a barrier is needed
    /// before it, and that barrier's sides.
    /// </summary>
    internal static bool Advance(ref SubresourceState state, UsageState target,
        PipelineStageFlags2 writeStage, AccessFlags2 writeAccess, bool discard, out BarrierSides sides)
    {
        AccessFlags2 readAccess = target.Access & ~UsageState.WriteAccessMask;
        PipelineStageFlags2 readStage = readAccess != AccessFlags2.None || writeAccess == AccessFlags2.None
            ? target.Stage
            : PipelineStageFlags2.None;
        bool writes = writeAccess != AccessFlags2.None;

        bool needed;
        if (state.Layout != target.Layout)
        {
            needed = true;
        }
        else if (writes)
        {
            // Separate uses can overlap even when they execute at the same stage.
            needed = state.ReadStages != PipelineStageFlags2.None ||
                     state.WriteStage != PipelineStageFlags2.None;
        }
        else
        {
            // RAW: the last write is not yet visible to this reader's stage.
            needed = state.WriteStage != PipelineStageFlags2.None &&
                     (target.Stage & ~state.VisibleStages) != PipelineStageFlags2.None;
        }

        sides = default;
        if (needed)
        {
            PipelineStageFlags2 srcStage = state.WriteStage | state.ReadStages;
            AccessFlags2 srcAccess = state.WriteAccess | state.ReadAccess;
            if (srcStage == PipelineStageFlags2.None)
            {
                // Nothing used it since the last barrier: that barrier is the prior use.
                srcStage = state.VisibleStages;
                srcAccess = AccessFlags2.None;
            }

            ImageLayout oldLayout = state.Layout;
            if (discard && state.Layout != target.Layout) oldLayout = ImageLayout.Undefined;
            sides = new BarrierSides(oldLayout, target.Layout, srcStage, srcAccess, target.Stage, target.Access);
            // Retain the last producer until another write replaces it: a later
            // reader at a different stage still needs that write made visible.
            PipelineStageFlags2 visible = state.Layout == target.Layout
                ? state.VisibleStages | target.Stage : target.Stage;
            state = new SubresourceState(target.Layout, state.WriteStage, state.WriteAccess,
                visible, PipelineStageFlags2.None, AccessFlags2.None);
        }

        if (writes)
        {
            state = new SubresourceState(target.Layout, writeStage, writeAccess,
                PipelineStageFlags2.None, readStage, readAccess);
        }
        else
        {
            state = state with
            {
                ReadStages = state.ReadStages | readStage,
                ReadAccess = state.ReadAccess | readAccess,
                VisibleStages = state.VisibleStages | target.Stage,
            };
        }
        return needed;
    }
}

/// <summary>
/// What a command does with an image. Layout, pipeline stage and access all
/// derive from this (<see cref="UsageState.For" />), never from the layout
/// alone: two uses can share a layout and still differ in stage (a depth
/// attachment read only by the depth test versus one also sampled by the
/// fragment shader).
/// </summary>
public enum ResourceUsage
{
    /// <summary>Colour attachment, written without reading the destination.</summary>
    ColorWrite,
    /// <summary>Colour attachment with blending: the destination is read and written.</summary>
    ColorBlend,
    /// <summary>Depth attachment with writes on.</summary>
    DepthWrite,
    /// <summary>Depth attachment with writes off, read by the depth test only.</summary>
    DepthReadOnly,
    /// <summary>Depth attachment with writes off, also sampled by the fragment shader.</summary>
    DepthReadOnlySampled,
    /// <summary>Sampled by a fragment shader.</summary>
    SampleFragment,
    /// <summary>Sampled by a vertex shader.</summary>
    SampleVertex,
    /// <summary>Read as a storage image.</summary>
    StorageRead,
    /// <summary>Sampled by a compute shader.</summary>
    SampleCompute,
    /// <summary>Read as a storage image by a compute shader, never written.</summary>
    StorageReadCompute,
    /// <summary>Written as a storage image by a compute shader without reading it first.</summary>
    StorageWrite,
    /// <summary>Read and written as a storage image by a compute shader.</summary>
    StorageReadWrite,
    /// <summary>Source of a copy or blit.</summary>
    TransferSrc,
    /// <summary>Destination of a copy, blit or clear.</summary>
    TransferDst,
    /// <summary>Handed to vkQueuePresentKHR.</summary>
    PresentSrc,
}

/// <summary>
/// The layout, stage and access of one <see cref="ResourceUsage" />: the
/// destination side of a barrier into that usage.
/// </summary>
internal readonly record struct UsageState(ImageLayout Layout, PipelineStageFlags2 Stage, AccessFlags2 Access)
{
    /// <summary>The fragment test stages a depth attachment is used at.</summary>
    public const PipelineStageFlags2 DepthTests =
        PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;

    /// <summary>Every access bit that writes.</summary>
    public const AccessFlags2 WriteAccessMask =
        AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentWriteBit |
        AccessFlags2.TransferWriteBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.MemoryWriteBit;

    /// <summary>
    /// The usage table. <paramref name="depth" /> is the image's aspect: an
    /// attachment usage on a depth image resolves to its depth form and a depth
    /// attachment usage on a colour image to its colour form, so a caller that
    /// only knows "attachment" gets the right one. Sampling keeps
    /// SHADER_READ_ONLY_OPTIMAL for both aspects, because that is the layout the
    /// descriptor writes name.
    /// </summary>
    // SHADER_READ includes sampled reads. Keep the aggregate access bit for image
    // sampling: UHD 770 / Windows driver 101.7088 returns stale texels after
    // attachment reuse with SHADER_SAMPLED_READ alone. The aggregate mask fixes
    // both the transient post-chain and TAA output reproductions without adding
    // a global barrier or widening the pipeline stages.
    public static UsageState For(ResourceUsage usage, bool depth) => Normalise(usage, depth) switch
    {
        ResourceUsage.ColorWrite => new(ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit),
        ResourceUsage.ColorBlend => new(ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit),
        ResourceUsage.DepthWrite => new(ImageLayout.DepthAttachmentOptimal, DepthTests,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit),
        ResourceUsage.DepthReadOnly => new(ImageLayout.DepthReadOnlyOptimal, DepthTests,
            AccessFlags2.DepthStencilAttachmentReadBit),
        ResourceUsage.DepthReadOnlySampled => new(ImageLayout.DepthReadOnlyOptimal,
            DepthTests | PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.ShaderReadBit),
        ResourceUsage.SampleFragment => new(ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderReadBit),
        ResourceUsage.SampleVertex => new(ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.VertexShaderBit, AccessFlags2.ShaderReadBit),
        ResourceUsage.StorageRead => new(ImageLayout.General,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit),
        ResourceUsage.SampleCompute => new(ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderReadBit),
        ResourceUsage.StorageReadCompute => new(ImageLayout.General,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit),
        ResourceUsage.StorageWrite => new(ImageLayout.General,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
        ResourceUsage.StorageReadWrite => new(ImageLayout.General,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit),
        ResourceUsage.TransferSrc => new(ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit),
        ResourceUsage.TransferDst => new(ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit),
        ResourceUsage.PresentSrc => new(ImageLayout.PresentSrcKhr,
            PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None),
        _ => throw new System.ArgumentOutOfRangeException(nameof(usage), usage, null),
    };

    /// <summary>
    /// The write a usage performs, which the next barrier must make available.
    /// An attachment is written by its store op even with writes off: a
    /// read-only depth attachment is still stored, and synchronization
    /// validation reports the next transition as write-after-write unless the
    /// barrier names that write (2026-09-11).
    /// </summary>
    public static (PipelineStageFlags2 Stage, AccessFlags2 Access) WriteOf(ResourceUsage usage, bool depth) =>
        Normalise(usage, depth) switch
        {
            ResourceUsage.ColorWrite or ResourceUsage.ColorBlend =>
                (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit),
            ResourceUsage.DepthWrite or ResourceUsage.DepthReadOnly or ResourceUsage.DepthReadOnlySampled =>
                (DepthTests, AccessFlags2.DepthStencilAttachmentWriteBit),
            ResourceUsage.TransferDst => (PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit),
            // A dispatch's storage write: the next barrier must make it available.
            ResourceUsage.StorageWrite or ResourceUsage.StorageReadWrite =>
                (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            _ => (PipelineStageFlags2.None, AccessFlags2.None),
        };

    /// <summary>
    /// The usage a layout stands for, for callers that still speak in layouts
    /// (tests, a readback restoring what it found). Attachment layouts map to
    /// the widest use of that layout: blending for colour, sampled for
    /// read-only depth.
    /// </summary>
    public static ResourceUsage ForLayout(ImageLayout layout) => layout switch
    {
        ImageLayout.ShaderReadOnlyOptimal => ResourceUsage.SampleFragment,
        ImageLayout.ColorAttachmentOptimal => ResourceUsage.ColorBlend,
        ImageLayout.DepthAttachmentOptimal or ImageLayout.DepthStencilAttachmentOptimal => ResourceUsage.DepthWrite,
        ImageLayout.DepthReadOnlyOptimal or ImageLayout.DepthStencilReadOnlyOptimal => ResourceUsage.DepthReadOnlySampled,
        ImageLayout.TransferSrcOptimal => ResourceUsage.TransferSrc,
        ImageLayout.TransferDstOptimal => ResourceUsage.TransferDst,
        ImageLayout.PresentSrcKhr => ResourceUsage.PresentSrc,
        ImageLayout.General => ResourceUsage.StorageRead,
        _ => throw new System.ArgumentOutOfRangeException(nameof(layout), layout, "no usage stands for this layout"),
    };

    private static ResourceUsage Normalise(ResourceUsage usage, bool depth) => (usage, depth) switch
    {
        (ResourceUsage.ColorWrite, true) => ResourceUsage.DepthWrite,
        (ResourceUsage.ColorBlend, true) => ResourceUsage.DepthWrite,
        (ResourceUsage.DepthWrite, false) => ResourceUsage.ColorBlend,
        (ResourceUsage.DepthReadOnly, false) => ResourceUsage.ColorBlend,
        (ResourceUsage.DepthReadOnlySampled, false) => ResourceUsage.ColorBlend,
        _ => usage,
    };
}

/// <summary>
/// The readers a buffer can have, derived from its usage flags. Buffers have no
/// layout, so the barriers around a staged copy name every use the buffer was
/// created for instead of ALL_COMMANDS.
/// </summary>
internal static class BufferUsageState
{
    public static (PipelineStageFlags2 Stage, AccessFlags2 Access) UsesOf(BufferUsageFlags usage)
    {
        PipelineStageFlags2 stage = PipelineStageFlags2.None;
        AccessFlags2 access = AccessFlags2.None;
        if ((usage & BufferUsageFlags.VertexBufferBit) != 0)
        {
            stage |= PipelineStageFlags2.VertexAttributeInputBit;
            access |= AccessFlags2.VertexAttributeReadBit;
        }
        if ((usage & BufferUsageFlags.IndexBufferBit) != 0)
        {
            stage |= PipelineStageFlags2.IndexInputBit;
            access |= AccessFlags2.IndexReadBit;
        }
        if ((usage & BufferUsageFlags.UniformBufferBit) != 0)
        {
            stage |= PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit;
            access |= AccessFlags2.UniformReadBit;
        }
        if ((usage & BufferUsageFlags.StorageBufferBit) != 0)
        {
            stage |= PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit |
                     PipelineStageFlags2.ComputeShaderBit;
            access |= AccessFlags2.ShaderStorageReadBit;
        }
        if ((usage & BufferUsageFlags.IndirectBufferBit) != 0)
        {
            stage |= PipelineStageFlags2.DrawIndirectBit;
            access |= AccessFlags2.IndirectCommandReadBit;
        }
        if ((usage & BufferUsageFlags.TransferSrcBit) != 0)
        {
            stage |= PipelineStageFlags2.TransferBit;
            access |= AccessFlags2.TransferReadBit;
        }
        if ((usage & BufferUsageFlags.TransferDstBit) != 0)
        {
            // A previous staged copy wrote it: that write must be made available too.
            stage |= PipelineStageFlags2.TransferBit;
            access |= AccessFlags2.TransferWriteBit;
        }
        return (stage, access);
    }
}
