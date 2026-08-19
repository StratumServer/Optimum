using System.Diagnostics;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class WorldgenPassTimingTests
{
    [Fact]
    public void RecordWorldgenPassTiming_AccumulatesTicksAndColumns()
    {
        OptimumDiagnostics.ResetWorldgenPassTiming();

        OptimumDiagnostics.RecordWorldgenPassTiming(1, 1000);
        OptimumDiagnostics.RecordWorldgenPassTiming(1, 2000);
        OptimumDiagnostics.RecordWorldgenPassTiming(2, 500);

        string summary = OptimumDiagnostics.GetWorldgenPassTimingSummary();
        Assert.Contains("Terrain=", summary);
        Assert.Contains("2cols", summary);
        Assert.Contains("TerrainFeatures=", summary);
        Assert.Contains("1cols", summary);
    }

    [Fact]
    public void RecordWorldgenPassTiming_IgnoresOutOfRangePass()
    {
        OptimumDiagnostics.ResetWorldgenPassTiming();

        OptimumDiagnostics.RecordWorldgenPassTiming(99, 5000);
        OptimumDiagnostics.RecordWorldgenPassTiming(-1, 5000);

        string summary = OptimumDiagnostics.GetWorldgenPassTimingSummary();
        Assert.Contains("totalColumns=0", summary);
    }

    [Fact]
    public void RecordWorldgenPassTiming_AllFivePasses()
    {
        OptimumDiagnostics.ResetWorldgenPassTiming();

        OptimumDiagnostics.RecordWorldgenPassTiming(1, 100);
        OptimumDiagnostics.RecordWorldgenPassTiming(2, 200);
        OptimumDiagnostics.RecordWorldgenPassTiming(3, 300);
        OptimumDiagnostics.RecordWorldgenPassTiming(4, 400);
        OptimumDiagnostics.RecordWorldgenPassTiming(5, 500);

        string summary = OptimumDiagnostics.GetWorldgenPassTimingSummary();
        Assert.Contains("Terrain=", summary);
        Assert.Contains("TerrainFeatures=", summary);
        Assert.Contains("Vegetation=", summary);
        Assert.Contains("SunLightFlood=", summary);
        Assert.Contains("PreDone=", summary);
        Assert.Contains("totalColumns=5", summary);
    }

    [Fact]
    public void ResetWorldgenPassTiming_ClearsAll()
    {
        OptimumDiagnostics.RecordWorldgenPassTiming(1, 9999);
        OptimumDiagnostics.RecordWorldgenPassTiming(3, 9999);

        OptimumDiagnostics.ResetWorldgenPassTiming();

        string summary = OptimumDiagnostics.GetWorldgenPassTimingSummary();
        Assert.Contains("totalColumns=0", summary);
        Assert.Contains("Terrain=0ms/col(0cols", summary);
    }

    [Fact]
    public void GetWorldgenPassTimingSummary_ReportsMeanMsPerColumn()
    {
        OptimumDiagnostics.ResetWorldgenPassTiming();

        long ticksPer10Ms = Stopwatch.Frequency / 100;
        OptimumDiagnostics.RecordWorldgenPassTiming(1, ticksPer10Ms);
        OptimumDiagnostics.RecordWorldgenPassTiming(1, ticksPer10Ms);

        string summary = OptimumDiagnostics.GetWorldgenPassTimingSummary();
        Assert.Contains("2cols", summary);
        Assert.Contains("Terrain=", summary);
        Assert.DoesNotContain("Terrain=0ms/col", summary);
    }

    [Fact]
    public void ResetAllCounters_IncludesWorldgenPass()
    {
        OptimumDiagnostics.RecordWorldgenPassTiming(2, 5000);

        OptimumDiagnostics.ResetAllCounters();

        string summary = OptimumDiagnostics.GetWorldgenPassTimingSummary();
        Assert.Contains("totalColumns=0", summary);
    }
}
