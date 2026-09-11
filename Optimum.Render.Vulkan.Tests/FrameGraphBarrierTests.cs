using System.Collections.Generic;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The usage table and the barrier derivation of <see cref="ResourceStateTracker" />,
/// without a device: layout, stage and access come from the usage; read after
/// write, write after read and write after write are ordered; repeated reads and
/// repeated uses cost nothing; sub-ranges split and merge; a present ends in
/// PRESENT_SRC.
/// </summary>
public class FrameGraphBarrierTests
{
    private static List<ImageTransition> Require(ResourceStateTracker tracker, ResourceUsage usage,
        bool discard = false) =>
        Require(tracker, 0, tracker.MipLevels, 0, tracker.Layers, usage, discard);

    private static List<ImageTransition> Require(ResourceStateTracker tracker, uint baseMip, uint mipCount,
        uint baseLayer, uint layerCount, ResourceUsage usage, bool discard = false)
    {
        var output = new List<ImageTransition>();
        int count = tracker.Require(baseMip, mipCount, baseLayer, layerCount, usage, discard, output);
        Assert.Equal(output.Count, count);
        return output;
    }

    [Theory]
    [InlineData(ResourceUsage.ColorWrite, false, ImageLayout.ColorAttachmentOptimal, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit)]
    [InlineData(ResourceUsage.ColorBlend, false, ImageLayout.ColorAttachmentOptimal, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit)]
    [InlineData(ResourceUsage.DepthWrite, true, ImageLayout.DepthAttachmentOptimal, PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit)]
    [InlineData(ResourceUsage.DepthReadOnly, true, ImageLayout.DepthReadOnlyOptimal, PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentReadBit)]
    [InlineData(ResourceUsage.DepthReadOnlySampled, true, ImageLayout.DepthReadOnlyOptimal, PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit | PipelineStageFlags2.FragmentShaderBit, AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.ShaderSampledReadBit)]
    [InlineData(ResourceUsage.SampleFragment, false, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit)]
    [InlineData(ResourceUsage.SampleFragment, true, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit)]
    [InlineData(ResourceUsage.SampleVertex, false, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags2.VertexShaderBit, AccessFlags2.ShaderSampledReadBit)]
    [InlineData(ResourceUsage.StorageRead, false, ImageLayout.General, PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit)]
    [InlineData(ResourceUsage.TransferSrc, false, ImageLayout.TransferSrcOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit)]
    [InlineData(ResourceUsage.TransferDst, false, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit)]
    [InlineData(ResourceUsage.PresentSrc, false, ImageLayout.PresentSrcKhr, PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None)]
    public void TheUsageTableDerivesLayoutStageAndAccess(ResourceUsage usage, bool depth, ImageLayout layout,
        PipelineStageFlags2 stage, AccessFlags2 access)
    {
        Assert.Equal(new UsageState(layout, stage, access), UsageState.For(usage, depth));
        Assert.NotEqual(PipelineStageFlags2.AllCommandsBit, UsageState.For(usage, depth).Stage & PipelineStageFlags2.AllCommandsBit);
    }

    [Fact]
    public void AnAttachmentUsageFollowsTheImageAspect()
    {
        Assert.Equal(ImageLayout.DepthAttachmentOptimal, UsageState.For(ResourceUsage.ColorBlend, depth: true).Layout);
        Assert.Equal(ImageLayout.ColorAttachmentOptimal, UsageState.For(ResourceUsage.DepthWrite, depth: false).Layout);
    }

    [Fact]
    public void TheFirstUseStartsFromUndefinedWithNoSourceStage()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        ImageTransition only = Assert.Single(Require(tracker, ResourceUsage.TransferDst));
        Assert.Equal(ImageLayout.Undefined, only.Sides.OldLayout);
        Assert.Equal(ImageLayout.TransferDstOptimal, only.Sides.NewLayout);
        Assert.Equal(PipelineStageFlags2.None, only.Sides.SrcStage);
        Assert.Equal(AccessFlags2.None, only.Sides.SrcAccess);
        Assert.Equal(PipelineStageFlags2.TransferBit, only.Sides.DstStage);
        Assert.Equal(AccessFlags2.TransferWriteBit, only.Sides.DstAccess);
    }

    [Fact]
    public void WriteThenReadGivesOneBarrierThatMakesTheWriteAvailable()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        Require(tracker, ResourceUsage.TransferDst);

        ImageTransition read = Assert.Single(Require(tracker, ResourceUsage.SampleFragment));
        Assert.Equal(ImageLayout.TransferDstOptimal, read.Sides.OldLayout);
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, read.Sides.NewLayout);
        Assert.Equal(PipelineStageFlags2.TransferBit, read.Sides.SrcStage);
        Assert.Equal(AccessFlags2.TransferWriteBit, read.Sides.SrcAccess);
        Assert.Equal(PipelineStageFlags2.FragmentShaderBit, read.Sides.DstStage);
        Assert.Equal(AccessFlags2.ShaderSampledReadBit, read.Sides.DstAccess);
    }

    [Fact]
    public void ReadThenReadNeedsNoBarrier()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        Require(tracker, ResourceUsage.TransferDst);
        Assert.Single(Require(tracker, ResourceUsage.SampleFragment));
        Assert.Empty(Require(tracker, ResourceUsage.SampleFragment));
        // A reader at another stage, with no write since the barrier: still nothing.
        Assert.Empty(Require(tracker, ResourceUsage.SampleVertex));
    }

    [Fact]
    public void RepeatedAttachmentUseNeedsNoBarrier()
    {
        var colour = new ResourceStateTracker(1, 1, depth: false);
        Assert.Single(Require(colour, ResourceUsage.ColorBlend));
        Assert.Empty(Require(colour, ResourceUsage.ColorBlend));
        Assert.Empty(Require(colour, ResourceUsage.ColorWrite));

        var depth = new ResourceStateTracker(1, 1, depth: true);
        Assert.Single(Require(depth, ResourceUsage.DepthReadOnlySampled));
        Assert.Empty(Require(depth, ResourceUsage.DepthReadOnlySampled));
    }

    [Fact]
    public void ReadAfterWriteInTheSameLayoutOrdersOnlyAStageTheWriteIsNotVisibleTo()
    {
        // A storage write at the fragment shader in GENERAL, then readers in the
        // same layout. No usage in the table writes a layout a pure reader shares,
        // so the rule is exercised on the state directly.
        var written = new SubresourceState(ImageLayout.General,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit, PipelineStageFlags2.None, AccessFlags2.None);

        SubresourceState sameStage = written;
        var fragmentReader = new UsageState(ImageLayout.General,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderStorageReadBit);
        Assert.False(ResourceStateTracker.Advance(ref sameStage, fragmentReader,
            PipelineStageFlags2.None, AccessFlags2.None, discard: false, out _));

        SubresourceState otherStage = written;
        Assert.True(ResourceStateTracker.Advance(ref otherStage, UsageState.For(ResourceUsage.StorageRead, false),
            PipelineStageFlags2.None, AccessFlags2.None, discard: false, out BarrierSides raw));
        Assert.Equal(ImageLayout.General, raw.OldLayout);
        Assert.Equal(ImageLayout.General, raw.NewLayout);
        Assert.Equal(PipelineStageFlags2.FragmentShaderBit, raw.SrcStage);
        Assert.Equal(AccessFlags2.ShaderStorageWriteBit, raw.SrcAccess);
        Assert.True((raw.DstStage & PipelineStageFlags2.ComputeShaderBit) != 0);

        // Once visible, the same reader again costs nothing.
        Assert.False(ResourceStateTracker.Advance(ref otherStage, UsageState.For(ResourceUsage.StorageRead, false),
            PipelineStageFlags2.None, AccessFlags2.None, discard: false, out _));
    }

    [Fact]
    public void SamplingTheReadOnlyDepthOfTheSameScopeNeedsNoBarrier()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: true);
        Require(tracker, ResourceUsage.DepthReadOnly);
        // Both store the attachment at the depth tests; the sampled form only adds a reader.
        Assert.Empty(Require(tracker, ResourceUsage.DepthReadOnlySampled));
    }

    [Fact]
    public void WriteAfterReadInTheSameLayoutOrdersAgainstTheEarlierReaderStage()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: true);
        Require(tracker, ResourceUsage.DepthReadOnlySampled);
        // The depth-test-only use does not run at the fragment shader stage that read before.
        ImageTransition war = Assert.Single(Require(tracker, ResourceUsage.DepthReadOnly));
        Assert.True((war.Sides.SrcStage & PipelineStageFlags2.FragmentShaderBit) != 0);
        Assert.True((war.Sides.SrcAccess & AccessFlags2.ShaderSampledReadBit) != 0);
    }

    [Fact]
    public void AReadOnlyDepthAttachmentMakesItsStoreWriteAvailableToTheNextTransition()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: true);
        Require(tracker, ResourceUsage.DepthReadOnlySampled);
        ImageTransition next = Assert.Single(Require(tracker, ResourceUsage.DepthWrite));
        Assert.True((next.Sides.SrcAccess & AccessFlags2.DepthStencilAttachmentWriteBit) != 0,
            "write-after-write: the read-only pass's store must be named on the source side");
        Assert.Equal(ImageLayout.DepthReadOnlyOptimal, next.Sides.OldLayout);
        Assert.Equal(ImageLayout.DepthAttachmentOptimal, next.Sides.NewLayout);
    }

    [Fact]
    public void WriteAfterWriteAcrossLayoutsNamesThePreviousWrite()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        Require(tracker, ResourceUsage.ColorWrite);
        ImageTransition waw = Assert.Single(Require(tracker, ResourceUsage.TransferDst));
        Assert.Equal(PipelineStageFlags2.ColorAttachmentOutputBit, waw.Sides.SrcStage);
        Assert.Equal(AccessFlags2.ColorAttachmentWriteBit, waw.Sides.SrcAccess);
    }

    [Fact]
    public void ADiscardingUseStartsFromUndefined()
    {
        var tracker = new ResourceStateTracker(1, 1, depth: false);
        Require(tracker, ResourceUsage.SampleFragment);
        ImageTransition discarded = Assert.Single(Require(tracker, ResourceUsage.TransferDst, discard: true));
        Assert.Equal(ImageLayout.Undefined, discarded.Sides.OldLayout);
    }

    [Fact]
    public void ASubRangeSplitsTheImageAndAgreeingAgainMergesIt()
    {
        var tracker = new ResourceStateTracker(7, 1, depth: false);
        Require(tracker, ResourceUsage.TransferSrc);
        Assert.False(tracker.IsSplit);

        // Mip 3 alone: one barrier over exactly that level, and the image splits.
        ImageTransition level = Assert.Single(Require(tracker, 3, 1, 0, 1, ResourceUsage.TransferDst, discard: true));
        Assert.Equal((3u, 1u, 0u, 1u), (level.BaseMip, level.MipCount, level.BaseLayer, level.LayerCount));
        Assert.True(tracker.IsSplit);
        Assert.Equal(ImageLayout.Undefined, tracker.Layout);
        Assert.Equal(ImageLayout.TransferDstOptimal, tracker.StateOf(3, 0).Layout);
        Assert.Equal(ImageLayout.TransferSrcOptimal, tracker.StateOf(2, 0).Layout);

        // Back to TRANSFER_SRC: every level agrees, one entry again.
        Assert.Single(Require(tracker, 3, 1, 0, 1, ResourceUsage.TransferSrc));
        Assert.False(tracker.IsSplit);
        Assert.Equal(ImageLayout.TransferSrcOptimal, tracker.Layout);

        // And the whole image moves in a single barrier.
        ImageTransition whole = Assert.Single(Require(tracker, ResourceUsage.SampleFragment));
        Assert.Equal((0u, 7u), (whole.BaseMip, whole.MipCount));
    }

    [Fact]
    public void AWholeUseOfASplitImageEmitsOneRectanglePerAgreeingRun()
    {
        var tracker = new ResourceStateTracker(4, 2, depth: false);
        Require(tracker, ResourceUsage.SampleFragment);
        // Layer 1 of mips 2 and 3 becomes a copy destination.
        Assert.Single(Require(tracker, 2, 2, 1, 1, ResourceUsage.TransferDst));

        List<ImageTransition> back = Require(tracker, ResourceUsage.SampleFragment);
        // Only the destination rectangle changes layout; the rest is already sampled.
        ImageTransition rectangle = Assert.Single(back);
        Assert.Equal((2u, 2u, 1u, 1u), (rectangle.BaseMip, rectangle.MipCount, rectangle.BaseLayer, rectangle.LayerCount));
        Assert.False(tracker.IsSplit);
    }

    [Fact]
    public void AMipChainBuildEndsInOneWholeImageBarrier()
    {
        const uint mips = 7;
        var tracker = new ResourceStateTracker(mips, 1, depth: false);
        int barriers = 0;
        barriers += Require(tracker, ResourceUsage.TransferDst).Count;
        barriers += Require(tracker, ResourceUsage.SampleFragment).Count;
        barriers += Require(tracker, ResourceUsage.TransferSrc).Count;
        for (uint level = 1; level < mips; level++)
        {
            barriers += Require(tracker, level, 1, 0, 1, ResourceUsage.TransferDst, discard: true).Count;
            barriers += Require(tracker, level, 1, 0, 1, ResourceUsage.TransferSrc).Count;
        }
        Assert.False(tracker.IsSplit);
        ImageTransition final = Assert.Single(Require(tracker, ResourceUsage.SampleFragment));
        Assert.Equal(mips, final.MipCount);
        Assert.Equal(3 + 2 * (int)(mips - 1), barriers);
    }

    [Fact]
    public void APresentEndsInPresentSrc()
    {
        var swapchainImage = new ResourceStateTracker(1, 1, depth: false);
        Require(swapchainImage, ResourceUsage.TransferDst, discard: true);
        ImageTransition present = Assert.Single(Require(swapchainImage, ResourceUsage.PresentSrc));
        Assert.Equal(ImageLayout.PresentSrcKhr, present.Sides.NewLayout);
        Assert.Equal(ImageLayout.PresentSrcKhr, swapchainImage.Layout);
        Assert.Equal(PipelineStageFlags2.TransferBit, present.Sides.SrcStage);
        Assert.Equal(AccessFlags2.TransferWriteBit, present.Sides.SrcAccess);
        Assert.Equal(PipelineStageFlags2.BottomOfPipeBit, present.Sides.DstStage);

        swapchainImage.Reset();
        Assert.Equal(ImageLayout.Undefined, swapchainImage.Layout);
    }

    /// <summary>
    /// The next frame's first barrier on an acquired image names the acquire's
    /// wait stage on its source side (synchronization validation reported
    /// write-after-read against vkAcquireNextImageKHR without it, 2026-09-11).
    /// </summary>
    [Fact]
    public void AnAcquiredImagesFirstBarrierOrdersAgainstTheAcquireWaitStage()
    {
        var swapchainImage = new ResourceStateTracker(1, 1, depth: false);
        Require(swapchainImage, ResourceUsage.TransferDst, discard: true);
        Require(swapchainImage, ResourceUsage.PresentSrc);

        swapchainImage.Reset(PipelineStageFlags2.TransferBit);
        ImageTransition first = Assert.Single(Require(swapchainImage, ResourceUsage.TransferDst, discard: true));
        Assert.Equal(ImageLayout.Undefined, first.Sides.OldLayout);
        Assert.Equal(PipelineStageFlags2.TransferBit, first.Sides.SrcStage);
        Assert.Equal(AccessFlags2.None, first.Sides.SrcAccess);
    }

    [Fact]
    public void NoBarrierEverNamesAllCommands()
    {
        foreach (ResourceUsage from in System.Enum.GetValues<ResourceUsage>())
        foreach (ResourceUsage to in System.Enum.GetValues<ResourceUsage>())
        {
            bool depth = from is ResourceUsage.DepthWrite or ResourceUsage.DepthReadOnly or ResourceUsage.DepthReadOnlySampled;
            var tracker = new ResourceStateTracker(1, 1, depth);
            Require(tracker, from);
            foreach (ImageTransition transition in Require(tracker, to))
            {
                Assert.True((transition.Sides.SrcStage & PipelineStageFlags2.AllCommandsBit) == 0, from + "->" + to);
                Assert.True((transition.Sides.DstStage & PipelineStageFlags2.AllCommandsBit) == 0, from + "->" + to);
            }
        }
    }

    [Fact]
    public void BufferCopiesNameTheBuffersOwnUses()
    {
        (PipelineStageFlags2 stage, AccessFlags2 access) = BufferUsageState.UsesOf(
            BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit);
        Assert.Equal(PipelineStageFlags2.VertexAttributeInputBit | PipelineStageFlags2.IndexInputBit |
                     PipelineStageFlags2.TransferBit, stage);
        Assert.Equal(AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit | AccessFlags2.TransferWriteBit, access);
        Assert.Equal(0, (int)((ulong)stage & (ulong)PipelineStageFlags2.AllCommandsBit));
    }
}
