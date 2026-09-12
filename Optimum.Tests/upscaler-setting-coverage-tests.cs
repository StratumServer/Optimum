using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// DLSS plan, Phase 2 step 3: the upscaler slot as a setting.
///
/// "Upscaler" (off|dlss) and a quality preset live in OptimumConfig with the
/// usual defensive load - an unrecognised upscaler means off, an unrecognised
/// preset means quality, and neither can fail the parse - and the renderer can
/// stand the upscaler down for the session without touching the persisted value.
/// </summary>
public class UpscalerSettingCoverageTests
{
    [Fact]
    public void TheSettingIsDeclaredPersistedAndDefensivelyParsed()
    {
        foreach (string config in new[]
        {
            Read("VintagestoryApi/Config/OptimumConfig.cs"),
            Read("sources/VintagestoryApi/Config/OptimumConfig.cs"),
        })
        {
            // Off by default, in the live value and in the persisted data object, so a
            // config that never mentions the setting comes up with the old render chain.
            Assert.Contains("public static string Upscaler = \"off\";", config);
            Assert.Contains("public string Upscaler { get; set; } = \"off\";", config);
            Assert.Contains("public static string UpscalerQuality = \"quality\";", config);
            Assert.Contains("public string UpscalerQuality { get; set; } = \"quality\";", config);

            Assert.Contains("(nameof(OptimumConfigData.Upscaler), Upscaler),", config);
            Assert.Contains("(nameof(OptimumConfigData.UpscalerQuality), UpscalerQuality),", config);
            Assert.Contains("Upscaler = Upscaler,", config);
            Assert.Contains("UpscalerQuality = UpscalerQuality,", config);

            // Defensive load, the same shape the renderer selection uses.
            Assert.Contains("string requestedUpscaler = data.Upscaler?.Trim() ?? \"\";", config);
            Assert.Contains("string requestedUpscalerQuality = data.UpscalerQuality?.Trim() ?? \"\";", config);

            // A runtime stand-down that never touches the persisted value, and reports
            // itself exactly once so the renderer logs one line.
            Assert.Contains("public static bool UpscalerRuntimeDisabled { get; private set; }", config);
            Assert.Contains("public static bool DisableUpscalerAtRuntime()", config);
            Assert.Contains("public static string EffectiveUpscaler => UpscalerRuntimeDisabled ? \"off\" : Upscaler;", config);
            Assert.Contains("public static bool UpscalerReplacesTaa => EffectiveUpscalerIsDlss;", config);
        }
    }

    /// <summary>
    /// The LOD bias every vendor asks for is computed from the sizes the SDK's own
    /// query returned, never hard-coded per preset, and it replaces the render-scale
    /// term rather than stacking with it.
    /// </summary>
    [Theory]
    [InlineData(1280, 2560, -2.0f)]   // performance, 1/2 -> log2(0.5) - 1
    [InlineData(1707, 2560, -1.585f)] // quality, 2/3-ish
    [InlineData(2560, 2560, 0f)]      // DLAA renders at display size: no bias at all
    [InlineData(0, 2560, 0f)]
    [InlineData(2560, 0, 0f)]
    public void TheRecommendedLodBiasIsLog2OfTheRatioMinusOne(int renderWidth, int displayWidth, float expected)
    {
        Assert.Equal(expected, OptimumConfig.RecommendedUpscalerLodBias(renderWidth, displayWidth), 3);
    }

    [Fact]
    public void TheUpscalerBiasReplacesTheRenderScaleTermAndIsGoneWhenTheUpscalerIsOff()
    {
        string upscaler = OptimumConfig.Upscaler;
        float renderScale = OptimumConfig.RenderScale;
        try
        {
            OptimumConfig.SetUpscalerLodBias(-2.0f);
            OptimumConfig.RenderScale = 0.5f;

            // Off: the render-scale term alone, exactly as before this phase.
            OptimumConfig.Upscaler = "off";
            Assert.Equal(-1.0f, OptimumConfig.EffectiveTerrainLodBias, 3);

            // On: the vendor's bias, not the sum of the two.
            OptimumConfig.Upscaler = "dlss";
            Assert.Equal(-2.0f, OptimumConfig.EffectiveTerrainLodBias, 3);

            // Cleared (no feature): back to the old term.
            OptimumConfig.SetUpscalerLodBias(0f);
            Assert.Equal(-1.0f, OptimumConfig.EffectiveTerrainLodBias, 3);
        }
        finally
        {
            OptimumConfig.SetUpscalerLodBias(0f);
            OptimumConfig.Upscaler = upscaler;
            OptimumConfig.RenderScale = renderScale;
        }
    }

    /// <summary>
    /// The round trip through a real optimum.json: the default is off, a config that
    /// never mentions the setting comes up off, an explicit value survives load and
    /// save, and an unknown one degrades instead of failing the parse.
    /// </summary>
    [Theory]
    [InlineData("\"dlss\"", "\"performance\"", "dlss", "performance")]
    [InlineData("\"DLSS\"", "\"ULTRAPERFORMANCE\"", "dlss", "ultraperformance")]
    [InlineData("\"  dlss  \"", "\"  dlaa  \"", "dlss", "dlaa")]
    [InlineData("\"off\"", "\"balanced\"", "off", "balanced")]
    [InlineData("\"xess\"", "\"nonsense\"", "off", "quality")]
    [InlineData("null", "null", "off", "quality")]
    [InlineData(null, null, "off", "quality")]
    public void TheUpscalerRoundTripsThroughOptimumJson(
        string? upscalerJson, string? qualityJson, string expectedUpscaler, string expectedQuality)
    {
        string originalUpscaler = OptimumConfig.Upscaler;
        string originalQuality = OptimumConfig.UpscalerQuality;
        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-upscaler-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A scan report that succeeded and disabled nothing leaves the assembly's
            // shader-compatibility state exactly as a fresh process has it.
            Directory.CreateDirectory(Path.Combine(dataPath, ".optimum"));
            File.WriteAllText(Path.Combine(dataPath, ".optimum", "shader-compatibility.json"),
                "{ \"ScanFailed\": false, \"DisabledFeatures\": [] }");

            OptimumConfig.SetDataPath(dataPath);
            string configPath = Path.Combine(dataPath, "ModConfig", "optimum.json");
            File.WriteAllText(configPath, upscalerJson == null
                ? "{}"
                : "{ \"Upscaler\": " + upscalerJson + ", \"UpscalerQuality\": " + qualityJson + " }");

            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.UpscalerQuality = "ultraperformance";
            OptimumConfig.Load();
            Assert.Equal(expectedUpscaler, OptimumConfig.Upscaler);
            Assert.Equal(expectedQuality, OptimumConfig.UpscalerQuality);

            string written = File.ReadAllText(configPath);
            Assert.Contains("\"Upscaler\": \"" + expectedUpscaler + "\"", written);
            Assert.Contains("\"UpscalerQuality\": \"" + expectedQuality + "\"", written);
        }
        finally
        {
            OptimumConfig.Upscaler = originalUpscaler;
            OptimumConfig.UpscalerQuality = originalQuality;
            try { Directory.Delete(dataPath, recursive: true); } catch (IOException) { }
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
