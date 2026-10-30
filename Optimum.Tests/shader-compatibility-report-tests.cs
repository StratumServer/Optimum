using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class ShaderCompatibilityReportTests : IDisposable
{
    private readonly string _tempDataDir;

    public ShaderCompatibilityReportTests()
    {
        _tempDataDir = Path.Combine(
            Path.GetTempPath(),
            "optimum-shader-compat-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataDir);
    }

    public void Dispose()
    {
        OptimumConfig.ResetShaderCompatibilityForTests();
        OptimumConfig.SetDataPath(null);
        try
        {
            string clean = Path.Combine(Path.GetTempPath(), "optimum-shader-compat-reset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(clean);
            OptimumConfig.SetDataPath(clean);
            Directory.Delete(clean, true);
        }
        catch { }
        OptimumConfig.ResetShaderCompatibilityForTests();
        // Reset state with clean directory to prevent polluting other test runs
        if (Directory.Exists(_tempDataDir))
        {
            try
            {
                Directory.Delete(_tempDataDir, true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public void MissingReportFile_IsReportedWithoutBlockingUnverifiedFeatures()
    {
        float originalRenderScale = OptimumConfig.RenderScale;
        try
        {
            OptimumConfig.SetDataPath(_tempDataDir);
            OptimumConfig.RenderScale = 0.77f;

            Assert.True(OptimumConfig.IsShaderCompatibilityReportMissing);
            Assert.False(OptimumConfig.IsShaderFeatureDisabled("RenderScale"));
            Assert.False(OptimumConfig.IsShaderFeatureDisabled("GreedyMesh"));
            Assert.False(OptimumConfig.IsShaderFeatureDisabled("GodRaysSampleCap"));
            Assert.False(OptimumConfig.IsShaderFeatureDisabled("MapPageCache"));
            Assert.Equal(0.77f, OptimumConfig.EffectiveRenderScale);
        }
        finally
        {
            OptimumConfig.RenderScale = originalRenderScale;
        }
    }

    [Fact]
    public void ReportWithScanFailed_DisablesAllFeaturesFailClosed()
    {
        float originalRenderScale = OptimumConfig.RenderScale;
        try
        {
            string optimumDir = Path.Combine(_tempDataDir, ".optimum");
            Directory.CreateDirectory(optimumDir);
            File.WriteAllText(
                Path.Combine(optimumDir, "shader-compatibility.json"),
                """
                {
                    "schemaVersion": 1,
                    "scanFailed": true,
                    "disabledFeatures": []
                }
                """);

            OptimumConfig.SetDataPath(_tempDataDir);
            OptimumConfig.RenderScale = 0.77f;

            Assert.True(OptimumConfig.IsShaderFeatureDisabled("RenderScale"));
            Assert.True(OptimumConfig.IsShaderFeatureDisabled("GreedyMesh"));
            Assert.Equal(1.0f, OptimumConfig.EffectiveRenderScale);
        }
        finally
        {
            OptimumConfig.RenderScale = originalRenderScale;
        }
    }

    [Fact]
    public void ReportWithSelectiveDisabledFeatures_DisablesOnlySpecifiedFeatures()
    {
        float originalRenderScale = OptimumConfig.RenderScale;
        try
        {
            string optimumDir = Path.Combine(_tempDataDir, ".optimum");
            Directory.CreateDirectory(optimumDir);
            File.WriteAllText(
                Path.Combine(optimumDir, "shader-compatibility.json"),
                """
                {
                    "schemaVersion": 1,
                    "scanFailed": false,
                    "disabledFeatures": ["GreedyMesh", "GodRaysSampleCap"]
                }
                """);

            OptimumConfig.SetDataPath(_tempDataDir);
            OptimumConfig.RenderScale = 0.85f;

            Assert.False(OptimumConfig.IsShaderFeatureDisabled("RenderScale"));
            Assert.True(OptimumConfig.IsShaderFeatureDisabled("GreedyMesh"));
            Assert.True(OptimumConfig.IsShaderFeatureDisabled("GodRaysSampleCap"));
            Assert.False(OptimumConfig.IsShaderFeatureDisabled("EntityLightBatch"));
            Assert.Equal(0.85f, OptimumConfig.EffectiveRenderScale);
        }
        finally
        {
            OptimumConfig.RenderScale = originalRenderScale;
        }
    }

    [Fact]
    public void ReportExposesFeatureReasonSourceAndScanErrorsWithoutChangingSavedPreference()
    {
        float originalRenderScale = OptimumConfig.RenderScale;
        try
        {
            string optimumDir = Path.Combine(_tempDataDir, ".optimum");
            Directory.CreateDirectory(optimumDir);
            File.WriteAllText(
                Path.Combine(optimumDir, "shader-compatibility.json"),
                """
                {
                    "scanFailed": true,
                    "disabledFeatures": ["RenderScale"],
                    "featureReasons": { "RenderScale": ["external final shader owns the post-process contract"] },
                    "featureSources": { "RenderScale": ["ExampleMod (Mods/ExampleMod.zip; final.fsh)"] },
                    "scanErrors": ["Mods/ExampleMod.zip: IOException: unreadable archive"]
                }
                """);

            OptimumConfig.SetDataPath(_tempDataDir);
            OptimumConfig.RenderScale = 0.77f;

            string reason = OptimumConfig.GetShaderFeatureBlockReason("RenderScale");
            Assert.Contains("unreadable archive", reason);
            Assert.Contains("ExampleMod (Mods/ExampleMod.zip; final.fsh)", reason);
            Assert.Equal(0.77f, OptimumConfig.RenderScale);
            Assert.Equal(1.0f, OptimumConfig.EffectiveRenderScale);

            File.WriteAllText(
                Path.Combine(optimumDir, "shader-compatibility.json"),
                """
                {
                    "scanFailed": false,
                    "disabledFeatures": []
                }
                """);
            OptimumConfig.ReloadShaderCompatibilityReport();
            Assert.Equal(0.77f, OptimumConfig.EffectiveRenderScale);
        }
        finally
        {
            OptimumConfig.RenderScale = originalRenderScale;
        }
    }

    [Fact]
    public void SelectiveBlockReasonIncludesOwningModAndReason()
    {
        string optimumDir = Path.Combine(_tempDataDir, ".optimum");
        Directory.CreateDirectory(optimumDir);
        File.WriteAllText(
            Path.Combine(optimumDir, "shader-compatibility.json"),
            """
            {
                "scanFailed": false,
                "disabledFeatures": ["RenderScale"],
                "featureReasons": { "RenderScale": ["external final shader owns the post-process contract"] },
                "featureSources": { "RenderScale": ["ExampleMod (Mods/ExampleMod.zip; assets/game/shaders/final.fsh)"] }
            }
            """);
        OptimumConfig.SetDataPath(_tempDataDir);

        string reason = OptimumConfig.GetShaderFeatureBlockReason("RenderScale");
        Assert.Contains("external final shader", reason);
        Assert.Contains("ExampleMod", reason);
    }

    [Fact]
    public void BlockModsListsModNamesFromCurrentReport()
    {
        WriteReport("""
            {
                "schemaVersion": 2,
                "scanFailed": false,
                "disabledFeatures": ["RenderScale", "Oit"],
                "featureMods": { "RenderScale": ["Foo (beta)", "ModB"] }
            }
            """);

        Assert.Equal("Foo (beta), ModB", OptimumConfig.GetShaderFeatureBlockMods("RenderScale"));
        Assert.Equal(string.Empty, OptimumConfig.GetShaderFeatureBlockMods("Oit"));
        Assert.False(OptimumConfig.IsShaderCompatibilityReportStale);
        Assert.False(OptimumConfig.IsShaderCompatibilityScanFailed);
    }

    [Fact]
    public void OldReportIsStaleAndFallsBackToModsWithShadersOrHooks()
    {
        WriteReport("""
            {
                "schemaVersion": 1,
                "scanFailed": false,
                "disabledFeatures": ["EntityLightBatch"],
                "sources": [
                    { "name": "ShaderMod", "shaderFiles": ["assets/game/shaders/blur.fsh"], "indicators": [] },
                    { "name": "HookMod", "shaderFiles": [], "indicators": ["Harmony", "RenderHook"] },
                    { "name": "PlainMod", "shaderFiles": [], "indicators": ["Other"] }
                ]
            }
            """);

        Assert.True(OptimumConfig.IsShaderCompatibilityReportStale);
        Assert.Equal("ShaderMod, HookMod", OptimumConfig.GetShaderFeatureBlockMods("EntityLightBatch"));
    }

    [Fact]
    public void StaleReportWithScanFailureDoesNotInventModNames()
    {
        WriteReport("""
            {
                "schemaVersion": 1,
                "scanFailed": true,
                "sources": [ { "name": "ShaderMod", "shaderFiles": ["assets/game/shaders/blur.fsh"] } ]
            }
            """);

        Assert.True(OptimumConfig.IsShaderCompatibilityReportStale);
        Assert.Equal(string.Empty, OptimumConfig.GetShaderFeatureBlockMods("RenderScale"));
    }

    private void WriteReport(string json)
    {
        string optimumDir = Path.Combine(_tempDataDir, ".optimum");
        Directory.CreateDirectory(optimumDir);
        File.WriteAllText(Path.Combine(optimumDir, "shader-compatibility.json"), json);
        OptimumConfig.SetDataPath(_tempDataDir);
    }

    [Fact]
    public void SettingsPatchListsCompatibilityFeaturesAndShowsAvailabilityStatus()
    {
        string patch = File.ReadAllText(FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch"));
        string language = File.ReadAllText(FindRepositoryFile("sources/lang/en.json"));

        Assert.Contains("AddOptimumCompatibilityStatus();", patch);
        Assert.Contains("IsShaderFeatureDisabled(feature)", patch);
        Assert.Contains("GetShaderFeatureBlockReason(feature)", patch);
        Assert.Contains("GetShaderFeatureBlockMods(feature)", patch);
        Assert.Contains(".Enabled = false", patch);
        Assert.Contains("OptimumConfig.RenderScale >= 1.0f", patch);
        Assert.Contains("optimum-compatibility-unavailable-suffix", patch);
        Assert.Contains("optimum-compatibility-caused-by", patch);
        Assert.Contains("optimum-compatibility-blocked-header", language);
        Assert.Contains("optimum-compatibility-report-missing", language);
        Assert.Contains("optimum-compatibility-report-stale", language);
        Assert.Contains("IsShaderCompatibilityReportStale", patch);
        Assert.Contains("CurParentBounds.WithChild(optimumContentBounds)", patch);
        Assert.DoesNotContain("BeginChildElements(optimumContentBounds)", patch);
        Assert.DoesNotContain('—', language);

        // Every feature the settings page can block needs a plain-language
        // explanation and a display name.
        foreach (string feature in new[] { "GreedyMesh", "RenderScale", "GodRaysSampleCap", "EntityLightBatch", "EntityShaderStateCache", "Oit", "MapPageCache", "ShaderPreprocessParallel" })
        {
            Assert.Contains($"\"optimum-compatibility-why-{feature}\"", language);
            Assert.Contains($"\"optimum-compatibility-feature-name-{feature}\"", language);
        }
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }

    [Fact]
    public void CorruptedReportFile_FailsClosedToEnsureSafety()
    {
        float originalRenderScale = OptimumConfig.RenderScale;
        try
        {
            string optimumDir = Path.Combine(_tempDataDir, ".optimum");
            Directory.CreateDirectory(optimumDir);
            File.WriteAllText(
                Path.Combine(optimumDir, "shader-compatibility.json"),
                "THIS IS NOT VALID JSON {[[}");

            OptimumConfig.SetDataPath(_tempDataDir);
            OptimumConfig.RenderScale = 0.77f;

            Assert.True(OptimumConfig.IsShaderFeatureDisabled("RenderScale"));
            Assert.Equal(1.0f, OptimumConfig.EffectiveRenderScale);
        }
        finally
        {
            OptimumConfig.RenderScale = originalRenderScale;
        }
    }

    [Fact]
    public void ReloadShaderCompatibilityReport_UpdatesWhenReportChanges()
    {
        float originalRenderScale = OptimumConfig.RenderScale;
        try
        {
            string optimumDir = Path.Combine(_tempDataDir, ".optimum");
            Directory.CreateDirectory(optimumDir);
            string reportFile = Path.Combine(optimumDir, "shader-compatibility.json");

            // 1. Initial state: absent report is visible but does not disable features
            OptimumConfig.SetDataPath(_tempDataDir);
            OptimumConfig.RenderScale = 0.67f;
            Assert.True(OptimumConfig.IsShaderCompatibilityReportMissing);
            Assert.False(OptimumConfig.IsShaderFeatureDisabled("RenderScale"));
            Assert.Equal(0.67f, OptimumConfig.EffectiveRenderScale);

            // 2. Report written disabling RenderScale
            File.WriteAllText(
                reportFile,
                """
                {
                    "schemaVersion": 1,
                    "scanFailed": false,
                    "disabledFeatures": ["RenderScale"]
                }
                """);
            OptimumConfig.ReloadShaderCompatibilityReport();
            Assert.True(OptimumConfig.IsShaderFeatureDisabled("RenderScale"));
            Assert.Equal(1.0f, OptimumConfig.EffectiveRenderScale);

            // 3. A valid report with no blockers restores the saved preference
            File.Delete(reportFile);
            File.WriteAllText(reportFile, """
                {
                    "schemaVersion": 1,
                    "scanFailed": false,
                    "disabledFeatures": []
                }
                """);
            OptimumConfig.ReloadShaderCompatibilityReport();
            Assert.False(OptimumConfig.IsShaderFeatureDisabled("RenderScale"));
            Assert.Equal(0.67f, OptimumConfig.EffectiveRenderScale);
        }
        finally
        {
            OptimumConfig.RenderScale = originalRenderScale;
        }
    }
}
