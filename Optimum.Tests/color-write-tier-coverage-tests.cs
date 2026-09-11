using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Phase 2 contract C4 (colour write masks) at source level: the optional
/// extensions are negotiated with an env override, draws emit the tier's dynamic
/// state, draw buffers never restart a scope, and a masked-out clear stays a no-op.
/// The pixels are proven per tier by Optimum.Render.Vulkan.Tests/MotionWindowTests.cs.
/// </summary>
public class ColorWriteTierCoverageTests
{
    [Fact]
    public void TheContextNegotiatesTheTiersWithAnOverride()
    {
        string tiers = Read("Optimum.Render.Vulkan/Core/ColorWriteTier.cs");
        Assert.Contains("OPTIMUM_VULKAN_COLOR_WRITE_TIER", tiers);
        Assert.Contains("public static ColorWriteTier SelectColorWriteTier(", tiers);

        string context = Read("Optimum.Render.Vulkan/Core/VulkanContext.cs");
        Assert.Contains("\"VK_EXT_color_write_enable\"", context);
        Assert.Contains("\"VK_EXT_extended_dynamic_state3\"", context);
        Assert.Contains("ExtendedDynamicState3ColorWriteMask = true,", context);
        Assert.Contains("options.ColorWriteTier ?? DeviceCaps.FromEnvironment()", context);
    }

    [Fact]
    public void DrawsEmitTheTiersColourWriteState()
    {
        string device = Read("Optimum.Render.Vulkan/VulkanDevice.cs");
        Assert.Contains("CmdSetColorWriteEnable(commandBuffer,", device);
        Assert.Contains("CmdSetColorWriteMask(commandBuffer, 0,", device);
        Assert.Contains("CmdSetColorBlendEquation(commandBuffer, 0,", device);
        Assert.Contains("_state.BuildKey(layoutId, formatsId, attachmentCount, drawBuffers)", device);
        Assert.Contains("_targets.ExcludeSampledAttachment(commandBuffer, _boundTextures[unit]);", device);

        string cache = Read("Optimum.Render.Vulkan/Core/PipelineCache.cs");
        Assert.Contains("DynamicState.ColorWriteEnableExt", cache);
        Assert.Contains("DynamicState.ColorWriteMaskExt", cache);
        // The undeclared-output masking is kept on every tier.
        Assert.Contains("request.Program.Interface.WrittenFragmentOutputs.Contains(i)", cache);
    }

    [Fact]
    public void DrawBufferChangesNeverRestartTheScope()
    {
        string targets = Read("Optimum.Render.Vulkan/Core/RenderTargetManager.cs");
        int start = targets.IndexOf("public void SetDrawBuffers(int framebufferId, uint mask)", StringComparison.Ordinal);
        int end = targets.IndexOf("public void ExcludeSampledAttachment(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string setDrawBuffers = targets.Substring(start, end - start);
        // The only restart is a sample-excluded slot rejoining.
        Assert.Contains("uint rejoining = framebuffer.SampledExclusion & mask;", setDrawBuffers);
        Assert.Contains("if (rejoining == 0) return;", setDrawBuffers);

        Assert.Contains("VulkanStats.NoteMaskRestart();", targets);
        Assert.Contains("if ((_bound.DrawBufferMask & (1u << attachment)) == 0) return;", targets);
        Assert.Contains("if (_state.ColorMask == 0) return;", targets);

        string stats = Read("Optimum.Render.Vulkan/Core/VulkanStats.cs");
        Assert.Contains("mask_restarts={8} feedback_splits={9}", stats);
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
