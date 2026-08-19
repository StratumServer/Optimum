using System.IO;
using Xunit;

namespace Optimum.Tests;

public class EntityRenderHysteresisCoverageTests
{
    [Fact]
    public void OptimumConfig_HasHysteresisFactorSq()
    {
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("HysteresisFactorSq", config);
        Assert.Contains("1.21", config);
    }

    [Fact]
    public void OnBeforeRender_UsesHysteresisForRenderDistance()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs");
        Assert.Contains("numHysteresis", source);
        Assert.Contains("HysteresisFactorSq", source);
        Assert.Contains("entity.IsRendered", source);
    }

    [Fact]
    public void OnRenderFrameShadows_UsesHysteresisForShadowCull()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs");
        Assert.Contains("entity.IsShadowRendered", source);
        Assert.Contains("shadowThreshold", source);
    }

    [Fact]
    public void ChiselLod_InFrustumAndRange_UsesHysteresisThresholds()
    {
        string source = Read("optimum-api-contracts/optimum-api-bridge.cs");
        Assert.Contains("outerThreshold", source);
        Assert.Contains("innerThreshold", source);
        Assert.Contains("HysteresisFactorSq", source);
        Assert.Contains("nowVisible", source);
    }

    [Fact]
    public void ChiselLod_InFrustumShadowPass_UsesHysteresis()
    {
        string source = Read("optimum-api-contracts/optimum-api-bridge.cs");
        Assert.Contains("InFrustumShadowPass", source);
        Assert.Contains("baseResult", source);
    }

    [Fact]
    public void HysteresisFactor_IsOnePointOneSquared()
    {
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("1.21", config);
        Assert.Contains("1.1", config);
    }

    [Fact]
    public void OnBeforeRender_HysteresisAppliesOnlyToDistanceCheck()
    {
        string source = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs");
        Assert.Contains("entity == game.EntityPlayer || entity.AllowOutsideLoadedRange", source);
        Assert.Contains("num * OptimumConfig.HysteresisFactorSq", source);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}