using System.Linq;
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

    [Fact]
    public void BenchmarkMetricsAreVersionedStableAndNumeric()
    {
        Assert.Equal(2, OptimumDiagnostics.BenchmarkMetricsSchemaVersion);

        var first = OptimumDiagnostics.CaptureBenchmarkMetrics();
        var second = OptimumDiagnostics.CaptureBenchmarkMetrics();

        Assert.NotEmpty(first);
        Assert.Equal(first.Keys.OrderBy(key => key), second.Keys.OrderBy(key => key));
        Assert.All(first.Values, value => Assert.True(double.IsFinite(value)));
        Assert.Contains("counters.EntityShadowCull.hits", first.Keys);
        Assert.Contains("counters.EntityShadowCull.skips", first.Keys);
        Assert.Contains("mesh.greedy.chunks.count", first.Keys);
        Assert.Contains("mesh.chiselLod.tessellation.stopwatchTicks", first.Keys);
        Assert.Contains("tessellation.chunksProcessed.count", first.Keys);
        Assert.Contains("render.chunk.drawCalls.count", first.Keys);
        Assert.Contains("worldgen.Terrain.elapsed.stopwatchTicks", first.Keys);
        Assert.Contains("runtime.process.cpuTime.timeSpanTicks", first.Keys);
        Assert.Contains("runtime.process.memory.workingSet.bytes", first.Keys);
        Assert.Contains("runtime.process.memory.private.bytes", first.Keys);
        Assert.Contains("runtime.gc.totalAllocated.bytes", first.Keys);
        foreach (var name in OptimumDiagnostics.Counters.Keys)
        {
            Assert.Contains("counters." + name + ".hits", first.Keys);
            Assert.Contains("counters." + name + ".skips", first.Keys);
        }
    }

    [Fact]
    public void BenchmarkMetricsReflectRecordedCounterAndTimingThenReset()
    {
        var counter = OptimumDiagnostics.EntityOutfitAnimatorCache;
        OptimumDiagnostics.ResetChiselShadowCull();
        counter.Reset();

        try
        {
            counter.Hit();
            counter.Skip();
            OptimumDiagnostics.RecordChiselShadowCull(321);

            var recorded = OptimumDiagnostics.CaptureBenchmarkMetrics();
            Assert.Equal(1d, recorded["counters.EntityOutfitAnimatorCache.hits"]);
            Assert.Equal(1d, recorded["counters.EntityOutfitAnimatorCache.skips"]);
            Assert.Equal(1d, recorded["render.chiselShadowCull.calls.count"]);
            Assert.Equal(321d, recorded["render.chiselShadowCull.elapsed.stopwatchTicks"]);

            counter.Reset();
            OptimumDiagnostics.ResetChiselShadowCull();

            var reset = OptimumDiagnostics.CaptureBenchmarkMetrics();
            Assert.Equal(0d, reset["counters.EntityOutfitAnimatorCache.hits"]);
            Assert.Equal(0d, reset["counters.EntityOutfitAnimatorCache.skips"]);
            Assert.Equal(0d, reset["render.chiselShadowCull.calls.count"]);
            Assert.Equal(0d, reset["render.chiselShadowCull.elapsed.stopwatchTicks"]);
        }
        finally
        {
            counter.Reset();
            OptimumDiagnostics.ResetChiselShadowCull();
        }
    }
}
