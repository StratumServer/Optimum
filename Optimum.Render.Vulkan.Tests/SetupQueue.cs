using System;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Component tests' stand-in for the synchronous setup submit the renderer no
/// longer has (Phase 1B step 3 deleted VulkanCommands.SubmitAndWait).
///
/// It owns a Transfer timeline and an <see cref="UploadManager" /> for managers
/// used without a frame ring. <see cref="SubmitAndWait" /> appends the test's
/// commands to the open upload batch, after every upload recorded so far, submits
/// the batch on its own and waits for its Transfer value: the order a test wrote
/// its calls in is the order the GPU runs them. Test code only; the renderer
/// itself never waits for an upload.
/// </summary>
internal sealed unsafe class SetupQueue : IDisposable
{
    private readonly VulkanContext _context;
    private bool _disposed;

    public FrameTimeline Timeline { get; }
    public RetireQueue Retired { get; }
    public UploadManager Uploads { get; }

    public SetupQueue(VulkanContext context, ulong stagingPerSlot = 4UL << 20)
    {
        _context = context;
        Timeline = new FrameTimeline(context);
        Retired = new RetireQueue(Timeline);
        Uploads = new UploadManager(context, Timeline, Retired, framesInFlight: 2, stagingPerSlot);
    }

    /// <summary>Records into the open upload batch, submits it and waits for it.</summary>
    public void SubmitAndWait(Action<CommandBuffer> record)
    {
        CommandBuffer commandBuffer = Uploads.BeginRecording(inlineInFrame: false);
        try
        {
            record(commandBuffer);
        }
        finally
        {
            Uploads.EndRecording();
        }

        ulong transferValue = Uploads.SubmitStandalone();
        Timeline.WaitForTransfer(transferValue, WaitSite.Readback);
        Retired.Collect();
    }

    /// <summary>
    /// Moves a standalone image between layouts with a broad synchronization2
    /// barrier (all commands on both sides), for tests that drive raw images.
    /// </summary>
    public void TransitionImage(CommandBuffer commandBuffer, VulkanImage image, ImageLayout target, ImageAspectFlags aspect)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            OldLayout = image.Layout,
            NewLayout = target,
            Image = image.Handle,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };

        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        image.Layout = target;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uploads.Dispose();
        Retired.DisposeAll();
        Timeline.Dispose();
    }
}
