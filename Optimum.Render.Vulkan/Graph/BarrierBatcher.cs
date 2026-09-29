using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>
/// Collects the image barriers a group of uses needs and records them as one
/// <c>vkCmdPipelineBarrier2</c>. Stages and accesses come from each image's
/// <see cref="ResourceStateTracker" />, never ALL_COMMANDS.
///
/// <see cref="Require" /> updates the tracker at once, so the barriers must be
/// flushed before the commands that perform the uses are recorded, and never
/// inside an open rendering scope (checked in debug builds). One batcher belongs
/// to one recording thread; the trackers it touches are locked per call.
/// </summary>
internal sealed unsafe class BarrierBatcher
{
    private readonly Vk _api;
    private readonly List<ImageTransition> _scratch = new();
    private ImageMemoryBarrier2[] _pending = new ImageMemoryBarrier2[16];
    private int _count;

    public BarrierBatcher(Vk api) => _api = api;

    /// <summary>
    /// Whether a rendering scope is open in the given command buffer. A flush
    /// there is a transition inside a scope, which debug builds reject.
    /// </summary>
    public Func<CommandBuffer, bool>? ScopeOpen { get; set; }

    /// <summary>Barriers recorded by <see cref="Require" /> and not yet flushed.</summary>
    public int Pending => _count;

    public void Require(VulkanTexture texture, uint baseMip, uint mipCount, uint baseLayer, uint layerCount,
        ResourceUsage usage) =>
        Require(texture, baseMip, mipCount, baseLayer, layerCount, usage, discard: false);

    public void Require(VulkanTexture texture, uint baseMip, uint mipCount, uint baseLayer, uint layerCount,
        ResourceUsage usage, bool discard) =>
        Require(texture.Image, texture.Aspect, texture.Sync, baseMip, mipCount, baseLayer, layerCount, usage, discard);

    /// <summary>An image the texture table does not own (a swapchain image), with its own tracker.</summary>
    public void Require(Image image, ImageAspectFlags aspect, ResourceStateTracker tracker,
        uint baseMip, uint mipCount, uint baseLayer, uint layerCount, ResourceUsage usage, bool discard)
    {
        lock (tracker)
        {
            _scratch.Clear();
            if (tracker.Require(baseMip, mipCount, baseLayer, layerCount, usage, discard, _scratch) == 0) return;

            foreach (ImageTransition transition in _scratch)
            {
                Append(image, aspect, transition);
            }
        }
    }

    private void Append(Image image, ImageAspectFlags aspect, ImageTransition transition)
    {
        BarrierSides sides = transition.Sides;
        var range = new ImageSubresourceRange(aspect, transition.BaseMip, transition.MipCount,
            transition.BaseLayer, transition.LayerCount);

        // A second use of the same range before the flush (one texture attached
        // to two slots in different roles): one barrier per subresource per
        // call, so the two chain into one from the first source to the last
        // destination.
        for (int i = 0; i < _count; i++)
        {
            ref ImageMemoryBarrier2 existing = ref _pending[i];
            if (existing.Image.Handle != image.Handle || !SameRange(existing.SubresourceRange, range)) continue;
            existing.NewLayout = sides.NewLayout;
            existing.DstStageMask = sides.DstStage;
            existing.DstAccessMask = sides.DstAccess;
            return;
        }

        if (_count == _pending.Length) Array.Resize(ref _pending, _pending.Length * 2);
        _pending[_count++] = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = sides.SrcStage,
            SrcAccessMask = sides.SrcAccess,
            DstStageMask = sides.DstStage,
            DstAccessMask = sides.DstAccess,
            OldLayout = sides.OldLayout,
            NewLayout = sides.NewLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
    }

    private static bool SameRange(ImageSubresourceRange a, ImageSubresourceRange b) =>
        a.AspectMask == b.AspectMask && a.BaseMipLevel == b.BaseMipLevel && a.LevelCount == b.LevelCount &&
        a.BaseArrayLayer == b.BaseArrayLayer && a.LayerCount == b.LayerCount;

    /// <summary>Records every pending barrier as one vkCmdPipelineBarrier2; nothing when none is pending.</summary>
    public void Flush(CommandBuffer commandBuffer)
    {
        if (_count == 0) return;

#if DEBUG
        if (ScopeOpen?.Invoke(commandBuffer) == true)
        {
            _count = 0;
            throw new InvalidOperationException("image barriers flushed inside an open rendering scope");
        }
#endif

        fixed (ImageMemoryBarrier2* barriers = _pending)
        {
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = (uint)_count,
                PImageMemoryBarriers = barriers,
            };
            _api.CmdPipelineBarrier2(commandBuffer, &dependency);
        }

        if (RenderTrace.Enabled)
        {
            for (int i = 0; i < _count; i++)
            {
                ImageMemoryBarrier2 b = _pending[i];
                RenderTrace.Write("barrier image=" + b.Image.Handle.ToString("x") + " mips=" +
                    b.SubresourceRange.BaseMipLevel + "+" + b.SubresourceRange.LevelCount + " layers=" +
                    b.SubresourceRange.BaseArrayLayer + "+" + b.SubresourceRange.LayerCount + " " +
                    b.OldLayout + "->" + b.NewLayout + " src=" + b.SrcStageMask + "/" + b.SrcAccessMask +
                    " dst=" + b.DstStageMask + "/" + b.DstAccessMask);
            }
        }

        VulkanStats.NoteImageBarriers(_count);
        VulkanStats.NoteBarrierCommand();
        _count = 0;
    }
}
