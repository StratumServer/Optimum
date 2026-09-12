using System;
using Optimum.Render.Vulkan.Graph;
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

    /// <summary>
    /// The latency backends this present path can run with (plan section
    /// "Latency seams", seam S8). A backend is chosen per present path, because
    /// what a backend needs is a property of how the frame reaches the screen:
    /// every Vulkan-swapchain path supports the Vulkan backends, and the later
    /// D3D12 bridge path will support XeLL only. Checked when the backend is
    /// selected, so an unsupported pairing degrades to None instead of making
    /// vendor calls the path cannot honour.
    /// </summary>
    LatencyBackendKind[] SupportedLatencyBackends { get; }

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
    /// <summary>Created at the first record, so a path built for its stage table alone needs no texture table.</summary>
    private BarrierBatcher? _barriers;

    /// <summary>The acquired image's state; reset per frame, since its contents are discarded.</summary>
    private readonly ResourceStateTracker _swapchainImage = new(1, 1, depth: false);

    public BlitPresentPath(VulkanContext context, TextureManager textures, Func<VulkanTexture?> source)
    {
        _context = context;
        _textures = textures;
        _source = source;
    }

    public PipelineStageFlags AcquireWaitStage => PresentWaitStages.BlitAcquireWait;

    /// <summary>
    /// Every backend that works over a Vulkan swapchain: the blit present is an
    /// ordinary vkQueuePresentKHR, so None, the renderer's own completion pacing,
    /// NV's low-latency2 and AMD's anti-lag all apply to it.
    /// </summary>
    public LatencyBackendKind[] SupportedLatencyBackends => VulkanSwapchainLatencyBackends;

    private static readonly LatencyBackendKind[] VulkanSwapchainLatencyBackends =
    {
        LatencyBackendKind.None,
        LatencyBackendKind.Native,
        LatencyBackendKind.NvLowLatency2,
        LatencyBackendKind.AmdAntiLag,
    };

    public void Record(CommandBuffer commandBuffer, in PresentTarget target)
    {
        Image destination = target.Image;

        // Every present leaves the swapchain image in PRESENT_SRC, and nothing
        // else writes it, so UNDEFINED discards nothing that matters. The
        // destination and the source move in one barrier command.
        // The acquire touched it last, at the stage this submission waits on it.
        BarrierBatcher barriers = _barriers ??= _textures.CreateBatcher();
        _swapchainImage.Reset((PipelineStageFlags2)(ulong)AcquireWaitStage);
        barriers.Require(destination, ImageAspectFlags.ColorBit, _swapchainImage, 0, 1, 0, 1,
            ResourceUsage.TransferDst, discard: true);

        VulkanTexture? source = _source();
        if (source != null) _textures.Require(barriers, commandBuffer, source, ResourceUsage.TransferSrc);
        barriers.Flush(commandBuffer);

        if (source != null)
        {

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

        // TRANSFER_DST (written at TRANSFER) to PRESENT_SRC (BOTTOM_OF_PIPE, no access).
        barriers.Require(destination, ImageAspectFlags.ColorBit, _swapchainImage, 0, 1, 0, 1,
            ResourceUsage.PresentSrc, discard: false);
        barriers.Flush(commandBuffer);
    }
}
