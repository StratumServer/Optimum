using System;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The latency contracts of seam L0, all without a device: the marker enum
/// against Silk.NET's VkLatencyMarkerNV, the settings, the report builder, the
/// OPTIMUM_VULKAN_LATENCY parse and selection table, the self-healing phase
/// tracker, and the None backend's behaviour.
/// </summary>
public class LatencyContractTests
{
    // vkSetLatencyMarkerNV takes the NV enum directly, so a mismatch here would
    // be a silently mis-attributed phase in every NV report. The markers travel
    // as ints: the enum is internal to the backend, and xunit needs public
    // signatures.
    [Fact]
    public void EveryMarkerHasTheValueOfItsNvCounterpart()
    {
        Assert.Equal((int)LatencyMarkerNV.SimulationStartNV, (int)LatencyMarker.SimulationStart);
        Assert.Equal((int)LatencyMarkerNV.SimulationEndNV, (int)LatencyMarker.SimulationEnd);
        Assert.Equal((int)LatencyMarkerNV.RendersubmitStartNV, (int)LatencyMarker.RenderSubmitStart);
        Assert.Equal((int)LatencyMarkerNV.RendersubmitEndNV, (int)LatencyMarker.RenderSubmitEnd);
        Assert.Equal((int)LatencyMarkerNV.PresentStartNV, (int)LatencyMarker.PresentStart);
        Assert.Equal((int)LatencyMarkerNV.PresentEndNV, (int)LatencyMarker.PresentEnd);
        Assert.Equal((int)LatencyMarkerNV.InputSampleNV, (int)LatencyMarker.InputSample);
        Assert.Equal((int)LatencyMarkerNV.TriggerFlashNV, (int)LatencyMarker.TriggerFlash);
        Assert.Equal((int)LatencyMarkerNV.OutOfBandRendersubmitStartNV, (int)LatencyMarker.OutOfBandRenderSubmitStart);
        Assert.Equal((int)LatencyMarkerNV.OutOfBandRendersubmitEndNV, (int)LatencyMarker.OutOfBandRenderSubmitEnd);
        Assert.Equal((int)LatencyMarkerNV.OutOfBandPresentStartNV, (int)LatencyMarker.OutOfBandPresentStart);
        Assert.Equal((int)LatencyMarkerNV.OutOfBandPresentEndNV, (int)LatencyMarker.OutOfBandPresentEnd);
    }

    [Fact]
    public void TheMarkerSetIsExactlyTheNvSetWithNoGaps()
    {
        Array values = Enum.GetValues(typeof(LatencyMarker));
        Assert.Equal(12, values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(i, (int)(LatencyMarker)values.GetValue(i)!);
        }
    }

    [Fact]
    public void TheDefaultSettingsAreOffAndUncapped()
    {
        LatencySettings settings = LatencySettings.Disabled;
        Assert.Equal(LatencyMode.Off, settings.Mode);
        Assert.Equal(0UL, settings.MinimumIntervalUs);
        Assert.False(settings.Enabled);
        Assert.False(settings.Boost);
        Assert.Equal(0u, settings.MaxFps);
    }

    [Theory]
    [InlineData((int)LatencyMode.Off, false, false)]
    [InlineData((int)LatencyMode.On, true, false)]
    [InlineData((int)LatencyMode.Boost, true, true)]
    public void EnabledAndBoostFollowTheMode(int mode, bool enabled, bool boost)
    {
        var settings = new LatencySettings((LatencyMode)mode, 0);
        Assert.Equal(enabled, settings.Enabled);
        Assert.Equal(boost, settings.Boost);
    }

    [Fact]
    public void TheFrameCapConvertsBothWays()
    {
        ulong interval = LatencySettings.IntervalUsForFps(60);
        Assert.Equal(16666UL, interval);
        Assert.Equal(60u, new LatencySettings(LatencyMode.On, interval).MaxFps);
        Assert.Equal(0UL, LatencySettings.IntervalUsForFps(0));
    }

    [Fact]
    public void ACpuReportIsTheDifferenceOfItsMarkersAndLeavesTheDriverFieldsAtZero()
    {
        LatencyFrameReport report = LatencyFrameReport.FromCpuTimestamps(
            frameId: 7, presentId: 5,
            inputSampleUs: 1000,
            simulationStartUs: 1100,
            simulationEndUs: 3100,
            renderSubmitStartUs: 3100,
            renderSubmitEndUs: 4600,
            presentStartUs: 4600,
            presentEndUs: 4900);

        Assert.Equal(7UL, report.FrameId);
        Assert.Equal(5UL, report.PresentId);
        Assert.Equal(100UL, report.InputUs);
        Assert.Equal(2000UL, report.SimulationUs);
        Assert.Equal(1500UL, report.RenderSubmitUs);
        Assert.Equal(300UL, report.PresentUs);
        Assert.Equal(0UL, report.DriverUs);
        Assert.Equal(0UL, report.OsRenderQueueUs);
        Assert.Equal(0UL, report.GpuUs);
        Assert.Equal(3900UL, report.TotalUs);
    }

    [Fact]
    public void AMissingOrOutOfOrderMarkerMakesItsIntervalZeroInsteadOfWrapping()
    {
        LatencyFrameReport report = LatencyFrameReport.FromCpuTimestamps(
            frameId: 1, presentId: 0,
            inputSampleUs: 0,          // never stamped
            simulationStartUs: 2000,
            simulationEndUs: 1000,     // out of order
            renderSubmitStartUs: 2500,
            renderSubmitEndUs: 0,      // never stamped
            presentStartUs: 3000,
            presentEndUs: 3200);

        Assert.Equal(0UL, report.InputUs);
        Assert.Equal(0UL, report.SimulationUs);
        Assert.Equal(0UL, report.RenderSubmitUs);
        Assert.Equal(200UL, report.PresentUs);
        // With no input sample the total runs from simulation start.
        Assert.Equal(1200UL, report.TotalUs);
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("  ", "auto")]
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("off", "off")]
    [InlineData("none", "off")]
    [InlineData(" Native ", "native")]
    [InlineData("nv", "nv")]
    [InlineData("NVIDIA", "nv")]
    [InlineData("reflex", "nv")]
    [InlineData("amd", "amd")]
    [InlineData("anti-lag", "amd")]
    public void TheEnvOverrideParsesToItsBackend(string? value, string expected)
    {
        Assert.Equal(expected, LatencyBackends.Token(LatencyBackends.ParseBackend(value)));
    }

    [Fact]
    public void AnUnknownValueIsAutoWithALogNote()
    {
        string? note = null;
        LatencyBackendKind? parsed = LatencyBackends.ParseBackend("turbo", m => note = m);
        Assert.Null(parsed);
        Assert.NotNull(note);
        Assert.Contains("OPTIMUM_VULKAN_LATENCY=turbo", note);
        Assert.Contains("auto", note);
    }

    [Fact]
    public void AKnownValueLogsNothing()
    {
        string? note = null;
        Assert.Equal(LatencyBackendKind.Native, LatencyBackends.ParseBackend("native", m => note = m));
        Assert.Null(note);
    }

    // auto picks the vendor path; a forced backend the device lacks degrades to
    // Native (the rule ColorWriteTier's tier table follows); only an explicit
    // "off" turns latency work off entirely.
    [Theory]
    [InlineData(false, false, null, "native")]
    [InlineData(true, false, null, "nv")]
    [InlineData(false, true, null, "amd")]
    [InlineData(true, true, null, "nv")]
    [InlineData(true, true, "off", "off")]
    [InlineData(true, true, "native", "native")]
    [InlineData(false, true, "nv", "native")]
    [InlineData(true, false, "amd", "native")]
    [InlineData(true, false, "nv", "nv")]
    [InlineData(false, true, "amd", "amd")]
    public void TheBestAvailableBackendAtOrBelowTheForcedOneIsSelected(
        bool nv, bool amd, string? forced, string expected)
    {
        LatencyBackendKind selected = LatencyBackends.SelectBackend(nv, amd, LatencyBackends.ParseBackend(forced));
        Assert.Equal(expected, LatencyBackends.Token(selected));
    }

    [Fact]
    public void TheEnvironmentIsReadFromTheDocumentedVariable()
    {
        string? previous = Environment.GetEnvironmentVariable(LatencyBackends.LatencyVariable);
        try
        {
            Environment.SetEnvironmentVariable(LatencyBackends.LatencyVariable, "amd");
            Assert.Equal(LatencyBackendKind.AmdAntiLag, LatencyBackends.FromEnvironment());
            Environment.SetEnvironmentVariable(LatencyBackends.LatencyVariable, null);
            Assert.Null(LatencyBackends.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LatencyBackends.LatencyVariable, previous);
        }
    }

    [Fact]
    public void AWholeFrameOfMarkersBecomesOneReport()
    {
        var tracker = new LatencyPhaseTracker();
        tracker.Mark(4, LatencyMarker.InputSample, 1000);
        tracker.Mark(4, LatencyMarker.SimulationStart, 1050);
        tracker.Mark(4, LatencyMarker.SimulationEnd, 2050);
        tracker.Mark(4, LatencyMarker.RenderSubmitStart, 2050);
        tracker.Mark(4, LatencyMarker.RenderSubmitEnd, 3050);
        tracker.Mark(4, LatencyMarker.PresentStart, 3060);
        tracker.Mark(4, LatencyMarker.PresentEnd, 3160);

        Assert.False(tracker.HasOpenPhase);
        Assert.True(tracker.TryComplete(4, presentId: 9, out LatencyFrameReport report));
        Assert.Equal(50UL, report.InputUs);
        Assert.Equal(1000UL, report.SimulationUs);
        Assert.Equal(1000UL, report.RenderSubmitUs);
        Assert.Equal(100UL, report.PresentUs);
        Assert.Equal(2160UL, report.TotalUs);
        Assert.Equal(0, tracker.SelfHealCount);
    }

    [Fact]
    public void APresentOfAnotherFrameIsNotCompleted()
    {
        var tracker = new LatencyPhaseTracker();
        tracker.Mark(4, LatencyMarker.SimulationStart, 1000);
        Assert.False(tracker.TryComplete(5, 0, out _));
        Assert.True(tracker.TryComplete(4, 0, out _));
        // The frame is closed: completing it twice reports once.
        Assert.False(tracker.TryComplete(4, 0, out _));
    }

    [Fact]
    public void APhaseStillOpenWhenTheNextFrameStartsIsClosedAndLoggedExactlyOnce()
    {
        int notes = 0;
        var tracker = new LatencyPhaseTracker(_ => notes++);

        for (ulong frame = 1; frame <= 5; frame++)
        {
            long baseUs = (long)frame * 10_000;
            tracker.Mark(frame, LatencyMarker.InputSample, baseUs);
            tracker.Mark(frame, LatencyMarker.SimulationStart, baseUs + 10);
            // SimulationEnd never arrives: the phase is open when the next frame starts.
            tracker.Mark(frame, LatencyMarker.RenderSubmitStart, baseUs + 500);
            tracker.Mark(frame, LatencyMarker.RenderSubmitEnd, baseUs + 900);
            tracker.Mark(frame, LatencyMarker.PresentStart, baseUs + 910);
            tracker.Mark(frame, LatencyMarker.PresentEnd, baseUs + 950);
        }

        // Four frame starts saw the previous frame's simulation phase open...
        Assert.Equal(4, tracker.SelfHealCount);
        // ...and the log heard about it once, not every frame.
        Assert.Equal(1, notes);
        Assert.True(tracker.SelfHealLogged);
    }

    [Fact]
    public void ClosingAnOpenPhaseDoesNotLeakIntoTheNextFramesReport()
    {
        var tracker = new LatencyPhaseTracker();
        tracker.Mark(1, LatencyMarker.InputSample, 1000);
        tracker.Mark(1, LatencyMarker.SimulationStart, 1010);
        // frame 1's simulation stays open.
        tracker.Mark(2, LatencyMarker.InputSample, 2000);
        tracker.Mark(2, LatencyMarker.SimulationStart, 2010);
        tracker.Mark(2, LatencyMarker.SimulationEnd, 2210);
        tracker.Mark(2, LatencyMarker.PresentStart, 2300);
        tracker.Mark(2, LatencyMarker.PresentEnd, 2400);

        Assert.Equal(1, tracker.SelfHealCount);
        Assert.True(tracker.TryComplete(2, 0, out LatencyFrameReport report));
        Assert.Equal(2UL, report.FrameId);
        Assert.Equal(200UL, report.SimulationUs);
        Assert.Equal(400UL, report.TotalUs);
    }

    [Fact]
    public void OutOfBandMarkersDoNotDisturbTheFrameBeingCollected()
    {
        var tracker = new LatencyPhaseTracker();
        tracker.Mark(3, LatencyMarker.InputSample, 1000);
        tracker.Mark(3, LatencyMarker.SimulationStart, 1010);
        tracker.Mark(99, LatencyMarker.OutOfBandRenderSubmitStart, 1020);
        tracker.Mark(99, LatencyMarker.OutOfBandRenderSubmitEnd, 1030);
        tracker.Mark(3, LatencyMarker.SimulationEnd, 1210);
        tracker.Mark(3, LatencyMarker.PresentEnd, 1300);

        Assert.Equal(3UL, tracker.CurrentFrameId);
        Assert.Equal(0, tracker.SelfHealCount);
        Assert.True(tracker.TryComplete(3, 0, out LatencyFrameReport report));
        Assert.Equal(200UL, report.SimulationUs);
    }

    [Fact]
    public void TheNoneBackendNeverSleepsAndNeverOwnsTheFrameCap()
    {
        using var backend = new NoneLatencyBackend();
        Assert.Equal(LatencyBackendKind.None, backend.Kind);
        Assert.False(backend.OwnsFrameCap);
        Assert.Equal(0UL, backend.Sleep(1));

        backend.Apply(new LatencySettings(LatencyMode.Boost, 16666));
        Assert.Equal(LatencyMode.Boost, backend.Settings.Mode);
        // Applying changes nothing about the pacing: this backend paces nothing.
        Assert.False(backend.OwnsFrameCap);
    }

    [Fact]
    public void TheNoneBackendReportsOneFramePerPresentAndDrains()
    {
        using var backend = new NoneLatencyBackend();
        backend.Marker(1, LatencyMarker.InputSample);
        backend.Marker(1, LatencyMarker.SimulationStart);
        backend.Marker(1, LatencyMarker.SimulationEnd);
        backend.Marker(1, LatencyMarker.PresentStart);
        backend.Marker(1, LatencyMarker.PresentEnd);
        backend.OnPresent(1, presentId: 11);

        LatencyFrameReport[] reports = backend.TakeReports();
        LatencyFrameReport report = Assert.Single(reports);
        Assert.Equal(1UL, report.FrameId);
        Assert.Equal(11UL, report.PresentId);
        Assert.Empty(backend.TakeReports());
    }

    [Fact]
    public unsafe void TheNoneBackendAddsNothingToTheSubmitChain()
    {
        using var backend = new NoneLatencyBackend();
        int chain = 42;
        void* pNext = &chain;
        Assert.True(pNext == backend.TagSubmit(1, pNext));
        Assert.True(null == backend.TagSubmit(1, null));
        backend.OnSwapchainCreated(default);
    }

    [Fact]
    public void TheRecordingFakeKeepsMarkerOrderAndCounts()
    {
        var backend = new RecordingLatencyBackend { SleepDurationUs = 1234, OwnsFrameCapValue = true };
        Assert.Equal(1234UL, backend.Sleep(1));
        backend.Marker(1, LatencyMarker.InputSample);
        backend.Marker(1, LatencyMarker.SimulationStart);
        backend.OnSwapchainCreated(default);
        backend.SleepDurationUs = 0;
        Assert.Equal(0UL, backend.Sleep(2));
        backend.Marker(2, LatencyMarker.InputSample);
        backend.OnPresent(2, 7);

        Assert.Equal(new[] { LatencyMarker.InputSample, LatencyMarker.SimulationStart }, backend.MarkersOf(1));
        Assert.Equal(new[] { LatencyMarker.InputSample }, backend.MarkersOf(2));
        Assert.Equal(2, backend.SleepCount);
        Assert.Equal(1, backend.SwapchainCount);
        Assert.True(backend.OwnsFrameCap);
        Assert.Equal((2UL, 7UL), Assert.Single(backend.Presents));
        Assert.Single(backend.TakeReports());
        Assert.Empty(backend.TakeReports());
    }
}
