using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The frame graph's bookkeeping without a device: which plan a streamed frame is matched
/// against (the last two frames, so the TAA history ping-pong hits), the load op a pass
/// gets while the frame so far matches and after it stops matching, and the pending-clear
/// table behind clear promotion.
/// </summary>
public class FrameGraphUnitTests
{
    private static PassSignature Pass(int name, int resource, bool transient) => new()
    {
        NameId = name,
        Attachments = new[]
        {
            new AttachmentUse(resource, transient ? ResourceUsage.ColorWrite : ResourceUsage.ColorBlend, transient),
        },
        Width = 8,
        Height = 8,
        FormatsId = 1,
    };

    [Fact]
    public void AFrameCycleOfTwoHitsThePlanFromTwoFramesAgo()
    {
        var graph = new FrameGraph { Enabled = true };
        var loads = new List<AttachmentLoadOp>();
        for (int frame = 0; frame < 6; frame++)
        {
            int resource = frame % 2 == 0 ? 10 : 11;
            int index = graph.OpenPass(Pass(1, resource, transient: true), declared: true);
            loads.Add(graph.PlannedLoad(index, 0));
            graph.EndFrame();
        }

        Assert.Equal(4, graph.PlanHits);
        Assert.Equal(2, graph.PlanMisses);
        Assert.Equal(new[]
        {
            AttachmentLoadOp.Load, AttachmentLoadOp.Load, AttachmentLoadOp.DontCare,
            AttachmentLoadOp.DontCare, AttachmentLoadOp.DontCare, AttachmentLoadOp.DontCare,
        }, loads);
    }

    [Fact]
    public void APassThatStopsMatchingMakesTheRestOfTheFrameConservative()
    {
        var graph = new FrameGraph { Enabled = true };
        graph.OpenPass(Pass(1, 10, transient: true), declared: true);
        graph.OpenPass(Pass(2, 20, transient: true), declared: true);
        graph.EndFrame();

        int first = graph.OpenPass(Pass(1, 10, transient: true), declared: true);
        Assert.Equal(AttachmentLoadOp.DontCare, graph.PlannedLoad(first, 0));
        int changed = graph.OpenPass(Pass(3, 30, transient: true), declared: true);
        Assert.Equal(AttachmentLoadOp.Load, graph.PlannedLoad(changed, 0));
        // The same pass as last frame, but after a mismatch: no DONT_CARE.
        int later = graph.OpenPass(Pass(2, 20, transient: true), declared: true);
        Assert.Equal(AttachmentLoadOp.Load, graph.PlannedLoad(later, 0));
        graph.EndFrame();

        Assert.Equal(0, graph.PlanHits);
        Assert.Equal(2, graph.PlanMisses);
    }

    [Fact]
    public void PersistentAttachmentsAlwaysLoad()
    {
        var graph = new FrameGraph { Enabled = true };
        for (int frame = 0; frame < 3; frame++)
        {
            int index = graph.OpenPass(Pass(1, 10, transient: false), declared: true);
            Assert.Equal(AttachmentLoadOp.Load, graph.PlannedLoad(index, 0));
            graph.EndFrame();
        }
        Assert.Equal(2, graph.PlanHits);
    }

    [Fact]
    public void PendingClearsAreReplacedTakenInOrderAndDropped()
    {
        var graph = new FrameGraph { Enabled = true };
        var a = new VulkanTexture(null!);
        var b = new VulkanTexture(null!);

        graph.PromoteColorClear(a, 0, 1f, 0f, 0f, 1f);
        graph.PromoteColorClear(a, 0, 0f, 1f, 0f, 1f);
        graph.PromoteColorClear(a, 1, 0f, 0f, 1f, 1f);
        graph.PromoteDepthClear(b, 1f);
        Assert.True(graph.HasPendingClear(a));
        Assert.True(graph.HasPendingClear(b));

        // Layer 0's second clear replaced its first; layer 2 has none.
        Assert.False(graph.TakeForLoad(a, 2, depth: false, out _));
        Assert.True(graph.TakeForLoad(a, 0, depth: false, out PendingClear taken));
        Assert.Equal(1f, taken.G);
        Assert.Equal(1, graph.PromotedClears);

        var standalone = new List<PendingClear>();
        graph.TakeStandalone(a, standalone);
        Assert.Single(standalone);
        Assert.Equal(1u, standalone[0].Layer);
        Assert.False(graph.HasPendingClear(a));

        graph.Drop(b);
        Assert.False(graph.HasPendingClears);
    }
}
