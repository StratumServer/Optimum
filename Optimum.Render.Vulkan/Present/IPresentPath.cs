using System;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Records the second submission of a frame (Submit B): whatever writes the
/// acquired swapchain image. The frame itself (Submit A) is already in flight
/// when this records, so the CPU only blocks on the acquire once all of the
/// frame's rendering is queued.
/// </summary>
internal interface IPresentPath
{
    /// <summary>
    /// The stage of the image's first use in <see cref="Record" />: the stage at
    /// which Submit B waits on the acquire semaphore. Never ALL_COMMANDS.
    /// </summary>
    PipelineStageFlags AcquireWaitStage { get; }

    void Record(CommandBuffer commandBuffer, in PresentTarget target);
}

/// <summary>
/// The wait stages of the present submission, checked in one place.
///
/// Submit B waits on two things: the Frame timeline at the value Submit A
/// signalled (the frame image it reads is finished), at COLOR_ATTACHMENT_OUTPUT;
/// and the acquire semaphore at the stage where the swapchain image is first
/// touched: TRANSFER for the flipped blit, COLOR_ATTACHMENT_OUTPUT for a raster
/// path (FSR's final pass). ALL_COMMANDS would also block the barrier and every
/// earlier command on the acquire, which is what the split exists to avoid.
/// </summary>
internal static class PresentWaitStages
{
    public const PipelineStageFlags FrameWait = PipelineStageFlags.ColorAttachmentOutputBit;
    public const PipelineStageFlags BlitAcquireWait = PipelineStageFlags.TransferBit;
    public const PipelineStageFlags RasterAcquireWait = PipelineStageFlags.ColorAttachmentOutputBit;

    /// <summary>Throws for a wait stage the present submission must not use.</summary>
    public static PipelineStageFlags RequireAcquireStage(PipelineStageFlags stage)
    {
        if (stage != BlitAcquireWait && stage != RasterAcquireWait)
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage,
                "the present submission waits on the acquire semaphore at TRANSFER or COLOR_ATTACHMENT_OUTPUT only");
        }
        return stage;
    }
}

/// <summary>
/// The default present path (policy BlitFromOwned): the whole frame, GUI
/// included, renders into the owned default image, and this copies it into the
/// acquired swapchain image, flipped.
///
/// This inverted blit is the entire Y-flip story for the backend. Everything
/// upstream stays in OpenGL's orientation, which is what keeps intermediate
/// targets and screenshots byte-identical to the GL path; the display wants row 0
/// at the top, so the source rows are read bottom-to-top exactly once, here.
/// </summary>
internal sealed unsafe class BlitPresentPath : IPresentPath
{
    private readonly VulkanContext _context;
    private readonly TextureManager _textures;
    private readonly Func<VulkanTexture?> _source;

    public BlitPresentPath(VulkanContext context, TextureManager textures, Func<VulkanTexture?> source)
    {
        _context = context;
        _textures = textures;
        _source = source;
    }

    public PipelineStageFlags AcquireWaitStage => PresentWaitStages.BlitAcquireWait;

    public void Record(CommandBuffer commandBuffer, in PresentTarget target)
    {
        Image destination = target.Image;

        // Every present leaves the swapchain image in PRESENT_SRC, and nothing
        // else writes it, so UNDEFINED discards nothing that matters.
        TransitionSwapchainImage(commandBuffer, destination,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.TransferBit, PipelineStageFlags2.TransferBit);

        VulkanTexture? source = _source();
        if (source != null)
        {
            _textures.TransitionTexture(commandBuffer, source, ImageLayout.TransferSrcOptimal);

            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            };
            // Source Y runs backwards: this is the flip.
            blit.SrcOffsets.Element0 = new Offset3D(0, (int)source.Height, 0);
            blit.SrcOffsets.Element1 = new Offset3D((int)source.Width, 0, 1);
            blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
            blit.DstOffsets.Element1 = new Offset3D((int)target.Extent.Width, (int)target.Extent.Height, 1);

            _context.Api.CmdBlitImage(commandBuffer,
                source.Image, ImageLayout.TransferSrcOptimal,
                destination, ImageLayout.TransferDstOptimal,
                1, &blit, Filter.Linear);
        }

        TransitionSwapchainImage(commandBuffer, destination,
            ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr,
            PipelineStageFlags2.TransferBit, PipelineStageFlags2.BottomOfPipeBit);
    }

    private void TransitionSwapchainImage(
        CommandBuffer commandBuffer, Image image, ImageLayout from, ImageLayout to,
        PipelineStageFlags2 srcStage, PipelineStageFlags2 dstStage)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = from == ImageLayout.TransferDstOptimal ? AccessFlags2.TransferWriteBit : AccessFlags2.None,
            DstStageMask = dstStage,
            DstAccessMask = to == ImageLayout.TransferDstOptimal ? AccessFlags2.TransferWriteBit : AccessFlags2.None,
            OldLayout = from,
            NewLayout = to,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        VulkanStats.NoteImageBarriers(1);
    }
}
