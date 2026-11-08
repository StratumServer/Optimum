using System;
using System.Diagnostics;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public sealed class GcPauseDiagnosticsTests
{
    [Fact]
    public void GcPauseSummary_ContainsExpectedKeys()
    {
        string summary = OptimumDiagnostics.GetGcPauseSummary();
        Assert.Contains("Optimum GC pause:", summary);
        Assert.Contains("totalPauseMs=", summary);
        Assert.Contains("hitches(>16ms)=", summary);
        Assert.Contains("maxPauseMs=", summary);
    }

    [Fact]
    public void RecordGcPauseHitch_UpdatesHitchCountAndMaxPause()
    {
        OptimumDiagnostics.ResetGcPauseDiagnostics();
        Assert.Equal(0, OptimumDiagnostics.GcPauseHitchCount);

        OptimumDiagnostics.RecordGcPauseHitch(25.5, 2, 1024 * 1024 * 128);
        Assert.Equal(1, OptimumDiagnostics.GcPauseHitchCount);
        long maxTicks = OptimumDiagnostics.GcMaxPauseDurationTicks;
        Assert.True(maxTicks > 0);

        double maxMs = maxTicks * 1000.0 / Stopwatch.Frequency;
        Assert.InRange(maxMs, 25.0, 26.0);

        string summary = OptimumDiagnostics.GetGcPauseSummary();
        Assert.Contains("hitches(>16ms)=1", summary);

        OptimumDiagnostics.ResetGcPauseDiagnostics();
        Assert.Equal(0, OptimumDiagnostics.GcPauseHitchCount);
        Assert.Equal(0, OptimumDiagnostics.GcMaxPauseDurationTicks);
    }

    [Fact]
    public void CheckGcPauseHitch_ReturnsBooleanAndDetails()
    {
        bool hitched = OptimumDiagnostics.CheckGcPauseHitch(16.0, out double pauseMs, out int gen, out long committed);
        Assert.True(pauseMs >= 0.0);
        Assert.True(gen >= 0);
        Assert.True(committed >= 0);
    }

    [Fact]
    public void StutterReport_IncludesGcPauseSummary()
    {
        string report = OptimumDiagnostics.BuildStutterReport(30.0);
        Assert.Contains("Optimum GC pause:", report);
    }
}
