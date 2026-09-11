using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>
/// The synchronization state of one subresource (mip level, array layer).
/// </summary>
/// <param name="Layout">The layout the subresource is in.</param>
/// <param name="WriteStage">The stage of the last write since the last barrier, or none.</param>
/// <param name="WriteAccess">The access of that write.</param>
/// <param name="VisibleStages">The stages the current contents were made visible to by a barrier or a write.</param>
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
/// need one only when an earlier read or write ran at a stage the new write
/// does not;</item>
/// <item>read after read, and any use repeating the previous one, needs none.</item>
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
            // WAR / WAW: an earlier read or write at a stage this write does not run at.
            needed = (state.ReadStages & ~target.Stage) != PipelineStageFlags2.None ||
                     (state.WriteStage & ~writeStage) != PipelineStageFlags2.None;
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
            state = new SubresourceState(target.Layout, PipelineStageFlags2.None, AccessFlags2.None,
                target.Stage, PipelineStageFlags2.None, AccessFlags2.None);
        }

        if (writes)
        {
            state = new SubresourceState(target.Layout, writeStage, writeAccess,
                target.Stage, readStage, readAccess);
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
