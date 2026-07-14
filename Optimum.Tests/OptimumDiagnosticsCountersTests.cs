using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class OptimumDiagnosticsCountersTests
{
    [Fact]
    public void HitSkipCounterTracksBothIndependently()
    {
        var counter = new OptimumDiagnostics.HitSkipCounter();
        counter.Hit();
        counter.Hit();
        counter.Skip();

        var (hits, skips) = counter.Snapshot();
        Assert.Equal(2, hits);
        Assert.Equal(1, skips);
    }

    [Fact]
    public void HitSkipCounterResetClearsBothCounts()
    {
        var counter = new OptimumDiagnostics.HitSkipCounter();
        counter.Hit();
        counter.Skip();

        counter.Reset();

        var (hits, skips) = counter.Snapshot();
        Assert.Equal(0, hits);
        Assert.Equal(0, skips);
    }

    [Fact]
    public void EveryShippedOptimizationHasACounter()
    {
        string[] expected =
        {
            "EntityShadowCull",
            "EntityRenderCull",
            "DynamicLightRadius",
            "BackgroundFpsLimiter",
            "PreciseFramePacing",
            "HudEntityNameTags",
            "ShadowFarVegetation",
            "RepulseAgents",
            "WeatherWindThrottle",
            "AnimBlockLodNear",
            "AnimBlockLodMid",
            "AnimBlockLodFar",
            "AnimBlockLodDeferred",
            "ParticleDistanceGate",
            "EntityLightBatch",
            "EntityShaderStateCache",
        };

        foreach (var name in expected)
        {
            Assert.True(OptimumDiagnostics.Counters.ContainsKey(name), $"missing counter: {name}");
        }
    }

    [Fact]
    public void GetCountersSummaryIncludesEveryCounterName()
    {
        string summary = OptimumDiagnostics.GetCountersSummary();
        foreach (var name in OptimumDiagnostics.Counters.Keys)
        {
            Assert.Contains(name, summary);
        }
    }

    [Fact]
    public void ResetAllCountersClearsChiselLodToo()
    {
        OptimumDiagnostics.RecordChiselLod(fullTriangles: 10, proxyTriangles: 0, fallback: false, elapsedTicks: 5);
        OptimumDiagnostics.ResetAllCounters();

        string summary = OptimumDiagnostics.GetChiselLodSummary();
        Assert.Contains("blocks=0", summary);
    }
}
