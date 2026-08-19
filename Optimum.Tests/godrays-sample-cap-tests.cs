using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class GodRaysSampleCapTests
{
    [Fact]
    public void DefaultUsesVanillaSampleLimit()
    {
        bool original = OptimumConfig.GodRaysSampleCapEnabled;
        try
        {
            OptimumConfig.GodRaysSampleCapEnabled = false;
            Assert.Equal(180, OptimumConfig.GodRaysSampleLimit);
            Assert.Equal(180, OptimumConfig.VanillaGodRaysSampleLimit);
            Assert.Equal(100, OptimumConfig.OptimumGodRaysSampleLimit);
        }
        finally
        {
            OptimumConfig.GodRaysSampleCapEnabled = original;
        }
    }

    [Fact]
    public void EnabledUsesOptimumSampleLimit()
    {
        bool original = OptimumConfig.GodRaysSampleCapEnabled;
        try
        {
            OptimumConfig.GodRaysSampleCapEnabled = true;
            Assert.Equal(100, OptimumConfig.GodRaysSampleLimit);
        }
        finally
        {
            OptimumConfig.GodRaysSampleCapEnabled = original;
        }
    }

    [Fact]
    public void ConfigDataPersistsGodRaysToggle()
    {
        var property = typeof(OptimumConfigData).GetProperty("GodRaysSampleCap");
        Assert.NotNull(property);
        Assert.False((bool)property!.GetValue(new OptimumConfigData())!);

        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("GodRaysSampleCapEnabled = data.GodRaysSampleCap;", config);
        Assert.Contains("GodRaysSampleCap = GodRaysSampleCapEnabled,", config);
    }

    [Fact]
    public void ShaderUsesUniformCapWithVanillaFallback()
    {
        string shader = Read("sources/shaders/godrays.fsh");

        Assert.Contains("uniform int maxGodRaySamples;", shader);
        Assert.Contains("int vanillaSamples = int(180 * min(1, intensity * 1.2));", shader);
        Assert.Contains("int samples = min(maxGodRaySamples, vanillaSamples);", shader);
        Assert.DoesNotContain("int samples = int(90", shader);
    }

    [Fact]
    public void RenderPassSendsConfiguredUniform()
    {
        string patch = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch");
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("godrays.Uniform(\"maxGodRaySamples\", OptimumConfig.GodRaysSampleLimit);", patch);
        Assert.Contains(
            "new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"RenderPostprocessingEffects\", 1),",
            patcher);
    }

    [Fact]
    public void ExtraTabBindsGodRaysToggle()
    {
        string patch = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch");

        Assert.Contains("optimum-godrayscap", patch);
        Assert.Contains("optGodRaysCap", patch);
        Assert.Contains("onOptimumGodRaysCapChanged", patch);
        Assert.Contains("GodRaysSampleCapEnabled", patch);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
