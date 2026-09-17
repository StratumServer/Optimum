using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Vulkan-native plan, Phase 2 step 2 (the streaming frame graph) at source level: the env
/// switch, one scope per pass through the pass recorder, clear promotion with its standalone
/// fallback, the platform declaring passes from the stage bracket, its binds and its post
/// methods, and the stats tokens. The pixels and scope counts are proven by
/// Optimum.Render.Vulkan.Tests/FrameGraphFrameTests.cs.
/// </summary>
public class FrameGraphCoverageTests
{
    [Fact]
    public void TheFrameGraphHasAnOffSwitchAndSolvesLoadOpsFromThePlan()
    {
        string graph = Read("Optimum.Render.Vulkan/Graph/FrameGraph.cs");
        Assert.Contains("public const string Variable = \"OPTIMUM_VULKAN_FRAMEGRAPH\";", graph);
        Assert.Contains("Environment.GetEnvironmentVariable(Variable) != \"0\"", graph);
        Assert.Contains("plan.MatchesPass(index, signature)", graph);
        Assert.Contains("_plans[0] = FramePlan.Build(_frame);", graph);

        string recorder = Read("Optimum.Render.Vulkan/Graph/PassRecorder.cs");
        Assert.Contains("_graph.PlannedLoad(passIndex, use)", recorder);
        Assert.Contains("attachments[i].LoadOp = AttachmentLoadOp.Clear;", recorder);
        Assert.Contains("CmdClearColorImage(", recorder);
        Assert.Contains("_graph.NoteSplit(allowed);", recorder);
    }

    [Fact]
    public void ScopesOpenThroughThePassRecorderAndClearsArePromoted()
    {
        string targets = Read("Optimum.Render.Vulkan/Core/RenderTargetManager.cs");
        Assert.Contains("_recorder.Prepare(commandBuffer, framebuffer, scopeColour!, scopeDepth, DepthReadOnly,", targets);
        Assert.Contains("public void DeclarePass(CommandBuffer commandBuffer, PassDeclaration declaration, int framebufferId)", targets);
        Assert.Contains("_graph.PromoteColorClear(texture, target.Color[attachment].Layer, r, g, b, a);", targets);
        Assert.Contains("_graph.PromoteDepthClear(texture, depth);", targets);
        Assert.Contains("_graph.NoteInPassClear();", targets);
        // The masked-out clear stays a no-op before either path, dropped where the mask is stated.
        Assert.Contains("stated.ColorMask == 0) return;",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeStated.cs"));

        string device = Read("Optimum.Render.Vulkan/VulkanDevice.cs");
        Assert.Contains("_targets.FlushAllPendingClears(_frames.Current.CommandBuffer);", device);
        Assert.Contains("if (_graph.Enabled) _graph.EndFrame();", device);
        Assert.Contains("_targets.FlushPendingClears(commandBuffer, texture);", Read("Optimum.Render.Vulkan/VulkanDevice.Native.cs"));
        Assert.Contains("_targets.FlushPendingClears(Commands, texture);", device);
    }

    [Fact]
    public void ASlotTheDeclaredPassLeavesOutIsTreatedAsOutsideTheScope()
    {
        // Phase 2 review: sampling a left-out slot neither splits nor takes a ReadSelf copy,
        // and a clear on it (draw buffer on) is promoted instead of dropped.
        // GPU proof: Optimum.Render.Vulkan.Tests/PassExclusionTests.cs.
        string targets = Read("Optimum.Render.Vulkan/Core/RenderTargetManager.cs");
        Assert.Contains("private void ApplyPassExclusion(CommandBuffer commandBuffer, VulkanFramebuffer target, uint colorSlots)", targets);
        Assert.Contains("if (((_bound.PassExclusion >> i) & 1) != 0) continue;", targets);
        Assert.Contains("if (((target.PassExclusion >> attachment) & 1) != 0)", targets);
    }

    [Fact]
    public void ThePlatformDeclaresThePassesOfTheFrame()
    {
        string graph = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Graph.cs");
        foreach (string member in new[]
                 {
                     "public override void MergeTransparentRenderPass()",
                     "public override bool RenderOptimumSkyMotion()",
                     "public override void RenderPostprocessingEffects(float[] projectMatrix)",
                     "public override bool RenderOptimumTaaResolve()",
                     "public override int RenderOptimumTaaSharpen(int resolvedScene)",
                     "public override void RenderFinalComposition()",
                     "public override void BlitPrimaryToDefault()",
                 })
        {
            Assert.Contains(member, graph);
        }
        Assert.Contains("PassFlags.OpenSampling | PassFlags.AllowSplit", graph);
        // Every pass is declared by the native route that records it; the stage bracket ends
        // whatever pass a stage left open.
        Assert.Contains("platform.GraphDevice?.EndStagePass();", graph);
        Assert.Contains("const uint slots = ~(1u << 1);",
            Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativePostFinal.cs"));
        Assert.Contains("Name = declared?.Name ?? \"Stated/\" + framebufferId,", Read("Optimum.Render.Vulkan/Platform/StatedDraw.cs"));

        string main = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs");
        Assert.Contains("RenderStageListener = new FrameGraphStageListener(this);", main);
        Assert.Contains("new(true, \"RenderOptimumTaaResolve\", Array.Empty<string>()),", main);
    }

    [Fact]
    public void TheStatsLineCarriesTheFrameGraphCounters()
    {
        string stats = Read("Optimum.Render.Vulkan/Core/VulkanStats.cs");
        Assert.Contains("\"passes={12} plan_hits={13} plan_misses={14} in_pass_clears={15} promoted_clears={16} \"", stats);
        Assert.Contains("\"standalone_clears={17} pass_splits={18} push_constants={19} storage_set_binds={20} \"", stats);
        Assert.Contains("compute_passes={23} dispatches={24}", stats);

        string doc = Read("docs/taa-acceptance.md");
        Assert.Contains("OPTIMUM_VULKAN_FRAMEGRAPH=0", doc);
    }

    private static string Read(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "VintageStory.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!, relativePath));
    }
}
