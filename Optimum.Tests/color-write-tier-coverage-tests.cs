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

        string cache = Read("Optimum.Render.Vulkan/Core/PipelineCache.cs");
        Assert.Contains("DynamicState.ColorWriteEnableExt", cache);
        Assert.Contains("DynamicState.ColorWriteMaskExt", cache);
        // The undeclared-output masking is kept on every tier.
        Assert.Contains("request.Program.Interface.WrittenFragmentOutputs.Contains(i)", cache);
    }

    [Fact]
    public void DrawBufferChangesNeverRestartTheScope()
    {
        // Draw buffers are the stated route's per-attachment write masks; the render-target
        // manager has no draw-buffer state at all, so a change cannot restart a scope there.
        string targets = Read("Optimum.Render.Vulkan/Core/RenderTargetManager.cs");
        Assert.DoesNotContain("DrawBufferMask", targets);
        Assert.DoesNotContain("SampledExclusion", targets);
        Assert.Contains("VulkanStats.NoteMaskRestart();", targets);

        string state = Read("Optimum.Render.Vulkan/Platform/StatedRenderState.cs");
        Assert.Contains("blend.WriteMask = ((DrawBuffers(framebufferId) >> slot) & 1) != 0 ? ColorMask : 0;", state);
        // A stated draw on the same target and slots coalesces into the open pass.
        Assert.Contains("device.EndNativePass(keepScope: true);", Read("Optimum.Render.Vulkan/Platform/StatedDraw.cs"));

        // A clear on a draw buffer that is off, or through an all-false colour mask, is dropped by
        // the platform before it reaches the device (GL's rule on the stated draw buffers).
        string stated = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativeStated.cs");
        Assert.Contains("if (((stated.DrawBuffers(framebufferId) >> slot) & 1) == 0 || stated.ColorMask == 0) return;", stated);

        string stats = Read("Optimum.Render.Vulkan/Core/VulkanStats.cs");
        Assert.Contains("mask_restarts={10} feedback_splits={11}", stats);
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
