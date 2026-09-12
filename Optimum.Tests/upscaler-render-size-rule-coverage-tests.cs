using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// PR #3, item A: "the DLSS quality profile's render scale also applies to TAA
/// when DLSS is switched off while not on DLAA."
///
/// The sizing question - what to allocate the frame buffers at - was answered
/// from the upscaler host's liveness. The host outlives a settings change by
/// design (NGX allows one lifetime per process, and the player may switch back
/// on), so on the rebuild that stood the upscaler down Primary was still built at
/// the vendor's reduced size, and the in-house TAA resolve then resolved a
/// 1707x993 frame into a 2560x1490 chain. DLAA hid it because its ratio is 1.
///
/// The rule these tests pin: the answer comes from the setting in force for the
/// frame being built, asked <i>before</i> the host is asked anything at all, and
/// the feature is retired before the rebuild that follows it. The numeric proof
/// that the allocator's size really goes back to the display size on every preset
/// is the GPU test
/// <c>Optimum.Render.Vulkan.Tests.UpscalerRenderSizeSwitchTests</c>.
/// </summary>
public class UpscalerRenderSizeRuleCoverageTests
{
    private const string Upscaler = "Optimum.Render.Vulkan/Upscale/DlssUpscaler.cs";
    private const string Platform = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Upscale.cs";

    /// <summary>
    /// One place decides, and it reads the setting first. A host check before the
    /// setting check would be the bug again: an active host is exactly the state a
    /// switch-off leaves behind.
    /// </summary>
    [Fact]
    public void TheSizingRuleAsksTheSettingBeforeItAsksTheHost()
    {
        string upscaler = Read(Upscaler);
        string rule = Between(upscaler, "public static bool TryPlanForFrame(", "\n    }");

        // The no-upscaler answer is the display size, unconditionally, before
        // anything can return early.
        Assert.Contains("renderWidth = displayWidth;", rule);
        Assert.Contains("renderHeight = displayHeight;", rule);

        int setting = rule.IndexOf("if (!OptimumConfig.UpscalerReplacesTaa) return false;", StringComparison.Ordinal);
        int host = rule.IndexOf("if (host == null || !host.Active) return false;", StringComparison.Ordinal);
        int plan = rule.IndexOf("host.TryPlan(", StringComparison.Ordinal);
        Assert.True(setting >= 0, "the setting in force must decide the render size");
        Assert.True(host > setting, "the host's liveness must be asked after the setting, never instead of it");
        Assert.True(plan > host, "nothing is planned before both checks have passed");

        // The preset comes from the same config the switch writes, so a preset
        // change and a switch-off are the same question asked twice.
        Assert.Contains("OptimumConfig.UpscalerQuality", rule);
    }

    /// <summary>
    /// The platform's override is that rule and nothing else - no second copy of
    /// the condition that could drift away from it.
    /// </summary>
    [Fact]
    public void ThePlatformOverrideDelegatesToTheOneRule()
    {
        string platform = Read(Platform);
        string body = Between(
            platform,
            "public override bool OptimumTryPlanUpscaleRenderSize(",
            "\n    }");

        Assert.Contains("DlssUpscaler.TryPlanForFrame(", body);
        // The old condition, which answered from liveness alone, must not come back.
        Assert.DoesNotContain("if (upscaler == null || !upscaler.Active) return false;", body);
        Assert.DoesNotContain("upscaler.TryPlan(", body);
    }

    /// <summary>
    /// Standing the upscaler down happens before the rebuild that follows it: the
    /// live feature is retired first, and only then does the base rebuild every
    /// framebuffer and re-ask the sizing question.
    /// </summary>
    [Fact]
    public void TheFeatureIsRetiredBeforeTheRebuild()
    {
        string platform = Read(Platform);
        string apply = Between(platform, "public override void ApplyOptimumUpscalerSettings()", "\n    }");

        int retire = apply.IndexOf("upscaler.RetireFeature();", StringComparison.Ordinal);
        int rebuild = apply.IndexOf("base.ApplyOptimumUpscalerSettings();", StringComparison.Ordinal);
        Assert.True(retire >= 0 && rebuild > retire,
            "the feature must be retired before the rebuild re-plans the render size");
    }

    /// <summary>
    /// The frame-side gate keeps its own copy of the same idea: the setting, not
    /// the host, decides whether the frame evaluates through an upscaler, so a
    /// switch-off cannot leave a resolve running over a history it did not make.
    /// </summary>
    [Fact]
    public void TheFrameGateAlsoAnswersFromTheSetting()
    {
        string platform = Read(Platform);
        Assert.Contains(
            "UpscalerActive && OptimumConfig.UpscalerReplacesTaa && MotionAttachmentIndex >= 0",
            platform);
    }

    /// <summary>
    /// And the client's own body still stands the upscaler down when it cannot
    /// plan one, which is the other half of the same contract: the setting alone
    /// silences the in-house resolve.
    /// </summary>
    [Fact]
    public void TheFrameBufferSetupStillStandsAnUnplannableUpscalerDown()
    {
        string setup = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs");
        Assert.Contains(
            "if (!upscaling && Vintagestory.API.Config.OptimumConfig.UpscalerReplacesTaa)",
            setup);
        Assert.Contains("DisableOptimumUpscaler(", setup);
    }

    // ---- helpers -----------------------------------------------------------

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "not found: " + start);
        int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to >= 0, "not found after " + start + ": " + end);
        return text[from..to];
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
