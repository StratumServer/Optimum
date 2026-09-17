using System;
using System.IO;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The present submission's wait stages, without a device: the frame is waited
/// on at COLOR_ATTACHMENT_OUTPUT, the acquire semaphore at the swapchain image's
/// first use (TRANSFER for the blit, COLOR_ATTACHMENT_OUTPUT for a raster path),
/// and no ALL_COMMANDS wait stage remains anywhere on the submission path.
/// </summary>
public class PresentWaitStageTests
{
    [Fact]
    public void TheFrameIsWaitedOnAtColorAttachmentOutput() =>
        Assert.Equal(PipelineStageFlags.ColorAttachmentOutputBit, PresentWaitStages.FrameWait);

    [Fact]
    public void TheBlitPathWaitsForTheAcquiredImageAtTransfer()
    {
        IPresentPath blit = new BlitPresentPath(null!, null!, () => null);
        Assert.Equal(PipelineStageFlags.TransferBit, blit.AcquireWaitStage);
        Assert.Equal(blit.AcquireWaitStage, PresentWaitStages.RequireAcquireStage(blit.AcquireWaitStage));
    }

    [Theory]
    [InlineData(PipelineStageFlags.AllCommandsBit)]
    [InlineData(PipelineStageFlags.AllGraphicsBit)]
    [InlineData(PipelineStageFlags.TopOfPipeBit)]
    [InlineData(PipelineStageFlags.BottomOfPipeBit)]
    [InlineData(PipelineStageFlags.FragmentShaderBit)]
    [InlineData(PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit)]
    public void AnyOtherAcquireWaitStageIsRefused(PipelineStageFlags stage) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PresentWaitStages.RequireAcquireStage(stage));

    [Fact]
    public void BothAllowedAcquireStagesAreAccepted()
    {
        Assert.Equal(PipelineStageFlags.TransferBit,
            PresentWaitStages.RequireAcquireStage(PresentWaitStages.BlitAcquireWait));
        Assert.Equal(PipelineStageFlags.ColorAttachmentOutputBit,
            PresentWaitStages.RequireAcquireStage(PresentWaitStages.RasterAcquireWait));
    }

    /// <summary>The submission code itself: no ALL_COMMANDS wait stage, and both waits come from PresentWaitStages.</summary>
    [Fact]
    public void TheSubmissionPathHasNoAllCommandsWaitStage()
    {
        string root = Path.Combine(ShaderCorpus.RepositoryRoot, "Optimum.Render.Vulkan");
        string ring = File.ReadAllText(Path.Combine(root, "Core", "FrameRing.cs"));
        Assert.DoesNotContain("PipelineStageFlags.AllCommandsBit", ring);
        Assert.Contains("waitStages[waitCount] = PresentWaitStages.FrameWait;", ring);
        Assert.Contains("PresentWaitStages.RequireAcquireStage(acquireStage);", ring);

        string device = File.ReadAllText(Path.Combine(root, "VulkanDevice.cs"));
        Assert.Contains("_presentPath.AcquireWaitStage, renderValue, target.PresentSemaphore);", device);
    }
}
