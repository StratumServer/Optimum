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
    public void MissingReportFile_FailsOpenAndAllowsShaderOptimizations()
    {
        float originalRenderScale = OptimumConfig.RenderScale;
        try
        {
            OptimumConfig.SetDataPath(_tempDataDir);
            OptimumConfig.RenderScale = 0.77f;

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

            // 1. Initial state: absent report -> enabled
            OptimumConfig.SetDataPath(_tempDataDir);
            OptimumConfig.RenderScale = 0.67f;
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

            // 3. Report removed -> reloaded -> enabled again
            File.Delete(reportFile);
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
