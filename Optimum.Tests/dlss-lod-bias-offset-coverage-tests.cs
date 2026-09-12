using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// DLSS plan, Phase 6 follow-up: the upscaler's LOD bias offset is a setting.
///
/// The user judged DLSS on an RTX 4070 and reported "the lower the Quality, the
/// more jitter comes back" - clean at DLAA, visibly shimmering on foliage at
/// Performance and Ultra Performance. The bias is the term that grows with
/// exactly that ratio, and the DLSS Programming Guide 310.x says so itself in
/// section 3.5: its recommended <c>log2(render / display) - 1</c> "can sometimes
/// lead to increased temporal instability, in the form of flickering and/or
/// moire", with high-frequency textures (3.5.1) as the caveat case - which is
/// what a 32px pixel-art block atlas with alpha-tested foliage is. The guide's
/// advice is a less aggressive bias, never one above <c>log2(render / display)</c>.
///
/// So the subtracted 1 is a slider, defaulting to the guide's recommendation (the
/// picture does not move unless the player moves it), clamped so the bound cannot
/// be crossed in either direction of rounding, and applied live through the one
/// applier without a shader reload, a framebuffer rebuild, an NGX feature change
/// or a history reset.
///
/// The GPU half is
/// <c>Optimum.Render.Vulkan.Tests/TerrainLodBiasFollowsThePlanTests.TheBiasInEffectFollowsTheSharpnessSetting</c>,
/// which measures the bias two offsets really put on the samplers.
/// </summary>
public class DlssLodBiasOffsetCoverageTests
{
    [Fact]
    public void TheOffsetDefaultsToTheGuideRecommendationAndCannotPassItsBound()
    {
        float offset = OptimumConfig.UpscalerLodBiasOffset;
        try
        {
            // The default is the guide's own recommendation, so nothing moves
            // until the player moves it: the numbers the previous stage measured
            // on the atlases and the terrain samplers stand.
            Assert.Contains("public static float UpscalerLodBiasOffset = 1.0f;",
                Read("VintagestoryApi/Config/OptimumConfig.cs"));

            OptimumConfig.UpscalerLodBiasOffset = 1.0f;
            Assert.Equal(MathF.Log2(1707f / 2560f) - 1.0f, OptimumConfig.RecommendedUpscalerLodBias(1707, 2560), 4);
            Assert.Equal(-1.585f, OptimumConfig.RecommendedUpscalerLodBias(1707, 2560), 3);
            Assert.Equal(-2.000f, OptimumConfig.RecommendedUpscalerLodBias(1280, 2560), 3);
            Assert.Equal(-2.585f, OptimumConfig.RecommendedUpscalerLodBias(853, 2560), 2);

            // Half the offset is half a mip less sharp, at every ratio.
            OptimumConfig.UpscalerLodBiasOffset = 0.5f;
            Assert.Equal(-1.085f, OptimumConfig.RecommendedUpscalerLodBias(1707, 2560), 3);
            Assert.Equal(-1.500f, OptimumConfig.RecommendedUpscalerLodBias(1280, 2560), 3);

            // 0 is exactly the guide's upper bound, log2(render / display).
            OptimumConfig.UpscalerLodBiasOffset = 0f;
            Assert.Equal(MathF.Log2(1280f / 2560f), OptimumConfig.RecommendedUpscalerLodBias(1280, 2560), 5);

            // And the bound holds however the value got there: a hand-edited
            // config, a negative rounding, a value past 1.
            OptimumConfig.UpscalerLodBiasOffset = -0.4f;
            Assert.Equal(MathF.Log2(1280f / 2560f), OptimumConfig.RecommendedUpscalerLodBias(1280, 2560), 5);
            OptimumConfig.UpscalerLodBiasOffset = 7.5f;
            Assert.Equal(MathF.Log2(1280f / 2560f) - 1.0f, OptimumConfig.RecommendedUpscalerLodBias(1280, 2560), 5);
            foreach (float candidate in new[] { -1f, -0.0001f, 0f, 0.5f, 1f, 2f })
            {
                OptimumConfig.UpscalerLodBiasOffset = candidate;
                Assert.True(OptimumConfig.RecommendedUpscalerLodBias(1280, 2560) <= MathF.Log2(0.5f));
                Assert.True(OptimumConfig.RecommendedUpscalerLodBiasForScale(1f / 3f) <= MathF.Log2(1f / 3f));
            }

            // DLAA and "no upscale" stay at 0 - the value that means "do not
            // touch the sampler parameter at all" - whatever the offset says.
            OptimumConfig.UpscalerLodBiasOffset = 0.2f;
            Assert.Equal(0f, OptimumConfig.RecommendedUpscalerLodBias(2560, 2560));
            Assert.Equal(0f, OptimumConfig.RecommendedUpscalerLodBiasForScale(1.0f));
            Assert.Equal(0f, OptimumConfig.RecommendedUpscalerLodBiasForScale(0f));
        }
        finally
        {
            OptimumConfig.UpscalerLodBiasOffset = offset;
        }
    }

    [Fact]
    public void MovingTheOffsetRepublishesThePublishedPlansBias()
    {
        float offset = OptimumConfig.UpscalerLodBiasOffset;
        string upscaler = OptimumConfig.Upscaler;
        bool taa = OptimumConfig.Taa;
        float scale = OptimumConfig.RenderScale;
        try
        {
            OptimumConfig.Taa = false;
            OptimumConfig.RenderScale = 1.0f;
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.UpscalerLodBiasOffset = 1.0f;
            OptimumConfig.SetUpscalerPlan(0.5f, OptimumConfig.RecommendedUpscalerLodBiasForScale(0.5f));
            Assert.Equal(-2.0f, OptimumConfig.EffectiveTerrainLodBias, 3);
            OptimumConfig.NoteTerrainLodBiasApplied(OptimumConfig.EffectiveTerrainLodBias, reachedAtlases: true);
            Assert.False(OptimumConfig.TerrainLodBiasPending());

            // The slider moves: the plan's ratio is untouched, the published bias
            // is re-derived from it, and the samplers are pending again.
            OptimumConfig.UpscalerLodBiasOffset = 0.25f;
            Assert.True(OptimumConfig.RepublishUpscalerLodBias());
            Assert.Equal(0.5f, OptimumConfig.UpscalerRenderScale, 5);
            Assert.Equal(-1.25f, OptimumConfig.UpscalerLodBias, 3);
            Assert.Equal(-1.25f, OptimumConfig.EffectiveTerrainLodBias, 3);
            Assert.True(OptimumConfig.TerrainLodBiasPending());

            // Idempotent: the same offset twice is not a second write.
            OptimumConfig.NoteTerrainLodBiasApplied(OptimumConfig.EffectiveTerrainLodBias, reachedAtlases: true);
            Assert.False(OptimumConfig.RepublishUpscalerLodBias());
            Assert.False(OptimumConfig.TerrainLodBiasPending());

            // With no plan published there is nothing to re-derive.
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.UpscalerLodBiasOffset = 0.75f;
            Assert.False(OptimumConfig.RepublishUpscalerLodBias());
            Assert.Equal(0f, OptimumConfig.UpscalerLodBias);
        }
        finally
        {
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.Upscaler = upscaler;
            OptimumConfig.UpscalerLodBiasOffset = offset;
            OptimumConfig.Taa = taa;
            OptimumConfig.RenderScale = scale;
            OptimumConfig.InvalidateTerrainLodBias();
        }
    }

    [Fact]
    public void TheRowShowsTheBiasThePresetEndsUpWith()
    {
        float offset = OptimumConfig.UpscalerLodBiasOffset;
        string preset = OptimumConfig.UpscalerQuality;
        try
        {
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.UpscalerLodBiasOffset = 1.0f;

            // Before a feature exists the preset's nominal ratio describes it -
            // the numbers the user was shown in game.
            OptimumConfig.UpscalerQuality = "quality";
            Assert.Equal(-1.585f, OptimumConfig.PreviewUpscalerLodBias(), 3);
            OptimumConfig.UpscalerQuality = "performance";
            Assert.Equal(-2.0f, OptimumConfig.PreviewUpscalerLodBias(), 3);
            OptimumConfig.UpscalerQuality = "ultraperformance";
            Assert.Equal(-2.585f, OptimumConfig.PreviewUpscalerLodBias(), 3);
            OptimumConfig.UpscalerQuality = "dlaa";
            Assert.Equal(0f, OptimumConfig.PreviewUpscalerLodBias());

            // Once a plan is published the row shows the vendor's real ratio.
            OptimumConfig.SetUpscalerPlan(0.6f, OptimumConfig.RecommendedUpscalerLodBiasForScale(0.6f));
            Assert.Equal(MathF.Log2(0.6f) - 1.0f, OptimumConfig.PreviewUpscalerLodBias(), 3);

            OptimumConfig.UpscalerLodBiasOffset = 0.4f;
            Assert.Equal(MathF.Log2(0.6f) - 0.4f, OptimumConfig.PreviewUpscalerLodBias(), 3);
        }
        finally
        {
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.UpscalerQuality = preset;
            OptimumConfig.UpscalerLodBiasOffset = offset;
        }
    }

    [Fact]
    public void TheSettingIsPersistedWithTheOtherUpscalerRows()
    {
        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");

        Assert.Contains("public static float UpscalerLodBiasOffset = 1.0f;", config);
        Assert.Contains("public float UpscalerLodBiasOffset { get; set; } = 1.0f;", config);
        Assert.Contains("(nameof(OptimumConfigData.UpscalerLodBiasOffset), UpscalerLodBiasOffset.ToString(\"F2\")),", config);
        Assert.Contains("UpscalerLodBiasOffset = Math.Clamp(data.UpscalerLodBiasOffset, 0f, 1.0f);", config);
        Assert.Contains("UpscalerLodBiasOffset = UpscalerLodBiasOffset,", config);
        // The bound is enforced where the bias is computed as well as on load.
        Assert.Contains("return MathF.Min(bound - offset, bound);", config);
    }

    [Fact]
    public void TheTabHasTheRowAndAppliesItLive()
    {
        string gui = PatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");

        // The row itself, in the rhythm of the rows beside it: label, tooltip,
        // slider, placed with the upscaler and preset rows.
        Assert.Contains("Lang.Get(\"optimum-upscalersharpness\")", gui);
        Assert.Contains("Lang.Get(\"optimum-upscalersharpness-tooltip\")", gui);
        Assert.Contains("onOptimumUpscalerSharpnessChanged", gui);
        Assert.Contains("\"optUpscalerSharpness\"", gui);

        // Disabled while nothing upscales, exactly as the quality row is.
        Assert.Contains("sharpness.Enabled = Vintagestory.API.Config.OptimumConfig.EffectiveUpscalerIsDlss;", gui);
        // And it shows the bias the choice produces.
        Assert.Contains("optimumUpscalerLodBiasText()", gui);
        Assert.Contains("OptimumConfig.PreviewUpscalerLodBias()", gui);

        // The handler: persist, re-derive the published plan's bias, one applier.
        Assert.Contains("OptimumConfig.UpscalerLodBiasOffset = Math.Clamp(val / 100f, 0f, 1f);", gui);
        Assert.Contains("OptimumConfig.RepublishUpscalerLodBias();", gui);
        Assert.Contains("ShaderRegistry.ApplyOptimumLodBias();", gui);

        // Nothing structural: the render size does not change with the bias, so
        // the handler must not rebuild targets, reload shaders, re-plan through
        // the platform or throw the history away.
        string handler = Between(gui, "private bool onOptimumUpscalerSharpnessChanged(int val)", "return true;");
        Assert.DoesNotContain("RebuildFrameBuffers", handler);
        Assert.DoesNotContain("ReloadShaders", handler);
        Assert.DoesNotContain("ApplyOptimumUpscalerSettings", handler);
        Assert.DoesNotContain("RequestReset", handler);
    }

    [Fact]
    public void CecilPatcherShipsTheRowAndItsHandler()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"onOptimumUpscalerSharpnessChanged\"", patcher);
        Assert.Contains("\"optimumUpscalerLodBiasText\"", patcher);
        // The composer and the row updater carry the row, so both are transplanted.
        Assert.Contains("\"OnOptimumOptions\"", patcher);
        Assert.Contains("\"optimumUpdateUpscalerRows\"", patcher);
    }

    [Fact]
    public void TheLanguageKeysExist()
    {
        string lang = Read("sources/lang/en.json");

        Assert.Contains("\"optimum-upscalersharpness\":", lang);
        Assert.Contains("\"optimum-upscalersharpness-tooltip\":", lang);
        // The tooltip says what the trade is, in the player's terms.
        int tip = lang.IndexOf("\"optimum-upscalersharpness-tooltip\":", StringComparison.Ordinal);
        string line = lang.Substring(tip, lang.IndexOf('\n', tip) - tip);
        Assert.Contains("temporal stability", line);
        Assert.Contains("sharpness", line);
    }

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from > 0, "missing: " + start);
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "missing: " + end);
        return text.Substring(from, to - from);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    private static string PatchedOrSource(string patchPath, string sourcePath)
    {
        try
        {
            return Read(patchPath);
        }
        catch (FileNotFoundException)
        {
            return Read(sourcePath);
        }
    }
}
