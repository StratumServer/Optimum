using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

public class FramePlanningTests
{
    private static PassSignature Pass(int target, bool transient = true, params int[] reads) => new()
    {
        NameId = target, Width = 32, Height = 24, FormatsId = 1,
        Attachments = new[] { new AttachmentUse(target, ResourceUsage.ColorWrite, transient) },
        Reads = reads,
    };

    [Fact]
    public void PostChainDiscardsOnlyTransientInitialContents()
    {
        var frame = new[] { Pass(1), Pass(2, true, 1), Pass(3, true, 2), Pass(4, false, 3) };
        var plan = FramePlan.Build(frame);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(AttachmentLoadOp.DontCare, plan.LoadOp(i, 0));
        }
        Assert.Equal(AttachmentLoadOp.Load, plan.LoadOp(3, 0));
    }

    [Theory]
    [InlineData(ResourceUsage.ColorBlend)]
    [InlineData(ResourceUsage.DepthReadOnly)]
    public void InitialReadsPreserveExistingContents(ResourceUsage firstUse)
    {
        var pass = Pass(1);
        pass.Attachments[0] = new AttachmentUse(1, firstUse, true);
        var plan = FramePlan.Build(new[] { pass });
        Assert.Equal(AttachmentLoadOp.Load, plan.LoadOp(0, 0));
    }

    [Fact]
    public void CachedPlansOwnTheirDataAndMismatchesFallBackToLoading()
    {
        var frame = new[] { Pass(1), Pass(2, true, 1) };
        var original = frame.Select(p => p.Clone()).ToArray();
        var plan = FramePlan.Build(frame);
        frame[0].Attachments[0] = new AttachmentUse(99, ResourceUsage.ColorBlend, false);
        frame[1].Reads[0] = 99;
        Assert.True(plan.Matches(original));
        Assert.False(plan.Matches(frame));
        var graph = new FrameGraph { Enabled = true };
        foreach (var pass in original) graph.OpenPass(pass, true);
        graph.EndFrame();
        foreach (var pass in frame)
        {
            int index = graph.OpenPass(pass, true);
            Assert.Equal(AttachmentLoadOp.Load, graph.PlannedLoad(index, 0));
        }
    }

    [Fact]
    public void AlternatingHistoryShapesReusePlansUntilTheirInputsChange()
    {
        var graph = new FrameGraph { Enabled = true };
        var a = Pass(10, true, 11);
        var b = Pass(11, true, 10);
        graph.OpenPass(a, true); graph.EndFrame();
        var planA = graph.Plan;
        graph.OpenPass(b, true); graph.EndFrame();
        var planB = graph.Plan;
        for (int i = 0; i < 6; i++)
        {
            var pass = i % 2 == 0 ? a : b;
            int index = graph.OpenPass(pass, true);
            Assert.Equal(AttachmentLoadOp.DontCare, graph.PlannedLoad(index, 0));
            graph.EndFrame();
            Assert.Same(i % 2 == 0 ? planA : planB, graph.Plan);
        }
        var resized = a.Clone(); resized.Height++;
        int changed = graph.OpenPass(resized, true);
        Assert.Equal(AttachmentLoadOp.Load, graph.PlannedLoad(changed, 0));
        graph.EndFrame();
        Assert.NotSame(planA, graph.Plan);
    }

    [Fact]
    public void AllocatorPreservesInclusiveLifetimesAndDistinctImageDescriptions()
    {
        var random = new Random(901);
        var intervals = Enumerable.Range(0, 96).Select(i =>
        {
            int first = random.Next(20);
            return (Description: new TransientImageDesc(32, 24, Format.R8G8B8A8Unorm, (uint)(i % 3 + 1)), FirstPass: first, LastPass: first + random.Next(5));
        }).OrderBy(i => i.FirstPass).ToArray();
        var allocator = new TransientAllocator(new ImageBacking(), aliasing: true);
        allocator.BeginFrame();
        int[] slots = intervals.Select(i => allocator.Acquire(i.Description, i.FirstPass, i.LastPass).Slot).ToArray();
        for (int i = 0; i < intervals.Length; i++)
            for (int j = i + 1; j < intervals.Length; j++)
                if (slots[i] == slots[j])
                {
                    Assert.Equal(intervals[i].Description, intervals[j].Description);
                    Assert.True(intervals[i].LastPass < intervals[j].FirstPass || intervals[j].LastPass < intervals[i].FirstPass);
                }
        int minimum = intervals.GroupBy(i => i.Description).Sum(group =>
            Enumerable.Range(0, 25).Max(pass => group.Count(i => i.FirstPass <= pass && pass <= i.LastPass)));
        Assert.Equal(minimum, slots.Distinct().Count());
    }

    private sealed class ImageBacking : ITransientBacking
    {
        private int _next;
        public int Create(TransientImageDesc desc) => ++_next;
        public void Destroy(int textureId) { }
        public ulong BytesOf(int textureId) => 0;
        public bool TryDescribe(int textureId, out TransientImageDesc desc) { desc = default; return false; }
        public void Discard(int textureId) { }
        public void Rebind(int logicalTextureId, int physicalTextureId) { }
        public void RestoreBindings() { }
    }

    [Theory]
    [InlineData(ResourceUsage.StorageReadCompute)]
    [InlineData(ResourceUsage.StorageWrite)]
    [InlineData(ResourceUsage.StorageReadWrite)]
    public void ConsecutiveComputeUsesOrderPreviousWritesEvenInTheSameStage(ResourceUsage next)
    {
        var state = new ResourceStateTracker(1, 1, false);
        var transitions = new List<ImageTransition>();
        state.Require(0, 1, 0, 1, ResourceUsage.StorageWrite, false, transitions);
        transitions.Clear();
        state.Require(0, 1, 0, 1, next, false, transitions);
        var barrier = Assert.Single(transitions).Sides;
        Assert.Equal(ImageLayout.General, barrier.OldLayout);
        Assert.Equal(ImageLayout.General, barrier.NewLayout);
        Assert.True((barrier.SrcAccess & AccessFlags2.ShaderStorageWriteBit) != 0);
        Assert.True((barrier.DstStage & PipelineStageFlags2.ComputeShaderBit) != 0);
    }

    [Fact]
    public void ASecondReaderStageMustAlsoObserveTheProducer()
    {
        var state = new ResourceStateTracker(1, 1, false);
        var transitions = new List<ImageTransition>();
        state.Require(0, 1, 0, 1, ResourceUsage.TransferDst, false, transitions);
        state.Require(0, 1, 0, 1, ResourceUsage.SampleVertex, false, transitions);
        transitions.Clear();
        state.Require(0, 1, 0, 1, ResourceUsage.SampleFragment, false, transitions);
        var barrier = Assert.Single(transitions).Sides;
        Assert.True((barrier.SrcAccess & AccessFlags2.TransferWriteBit) != 0);
        Assert.True((barrier.DstStage & PipelineStageFlags2.FragmentShaderBit) != 0);
        transitions.Clear();
        state.Require(0, 1, 0, 1, ResourceUsage.SampleFragment, false, transitions);
        Assert.Empty(transitions);
    }

    [Fact]
    public void MipTransitionsDoNotChangeUntouchedLevelsAndDiscardStillOrdersPriorUses()
    {
        var state = new ResourceStateTracker(3, 1, false);
        var transitions = new List<ImageTransition>();
        state.Require(0, 3, 0, 1, ResourceUsage.TransferDst, false, transitions);
        state.Require(1, 1, 0, 1, ResourceUsage.SampleFragment, false, transitions);
        Assert.Equal(ImageLayout.TransferDstOptimal, state.StateOf(0, 0).Layout);
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, state.StateOf(1, 0).Layout);
        Assert.Equal(ImageLayout.TransferDstOptimal, state.StateOf(2, 0).Layout);
        state.Discard(); transitions.Clear();
        state.Require(1, 1, 0, 1, ResourceUsage.ColorWrite, false, transitions);
        var barrier = Assert.Single(transitions).Sides;
        Assert.Equal(ImageLayout.Undefined, barrier.OldLayout);
        Assert.True((barrier.SrcStage & PipelineStageFlags2.FragmentShaderBit) != 0);
        Assert.Equal(ImageLayout.ColorAttachmentOptimal, barrier.NewLayout);
    }

    [Fact]
    public void ComputeMipBindingsCoverOddExtentsAndRejectFeedbackOnTheSameLevel()
    {
        var pass = new ComputePassDeclaration
        {
            Bindings = new[] { new ComputeBinding(0, 1, ComputeAccess.Sampled), new ComputeBinding(1, 1, ComputeAccess.StorageWrite, 1) },
            Dispatches = new[] { ComputeDispatch.Covering(1) },
        };
        ComputeImageInfo? Image(int _) => new ComputeImageInfo(35, 19, 4, 1);
        Assert.Null(ComputePassPlanner.Validate(pass, Image));
        Assert.Equal((3u, 2u, 1u), ComputePassPlanner.Groups(pass.Dispatches[0], pass, Image, 8, 8));
        pass.Bindings[1] = pass.Bindings[1] with { BaseMip = 0 };
        Assert.NotNull(ComputePassPlanner.Validate(pass, Image));
    }
}
