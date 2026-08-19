using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

// [Collection("TessellationDiagnostics")]: shares OptimumDiagnostics' static
// tessellation counters with OptimumBoundedHandoffTests - both must be serialized
// relative to each other, or ResetTessellation() calls interleave with assertions
// under xUnit's default cross-class parallelism.
[Collection("TessellationDiagnostics")]
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
            "EntityTesselationBudget",
            "EntityOutfitShapeCache",
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

    // Step 6 of the worker-pool wiring plan: OptimumTesselationWorkerRegistry.Register
    // publishes the registered thread id set, so `.optimum status` can show `ids=<the
    // tesselateterrain thread id>` as direct, in-game evidence that ClientMain::Start's
    // RegisterTesselationThread call actually ran.
    [Fact]
    public void TessellationSummaryReportsRegisteredWorkerIdsAndResets()
    {
        OptimumDiagnostics.ResetTessellation();

        var registry = new OptimumTesselationWorkerRegistry();
        registry.Register(4242);
        registry.Register(4242); // repeated registration must not duplicate the id
        registry.Register(9001);

        string summary = OptimumDiagnostics.GetTessellationSummary();
        Assert.Contains("workers=2", summary);
        Assert.Contains("ids=4242,9001", summary);
        Assert.Contains("ready-to-upload meanMs=", summary);
        Assert.DoesNotContain("ready->uploaded", summary);

        OptimumDiagnostics.ResetTessellation();
        Assert.Contains("workers=0 [ids=]", OptimumDiagnostics.GetTessellationSummary());
    }
}
