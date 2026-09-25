using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Optimum.Render.Vulkan.Core;
using Optimum.Tests;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class RenderDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public void PacingStatisticsUseOnlyTheLatestValidIntervals()
    {
        var ring = new FrameIntervalRing(100);
        for (int i = 0; i < 5; i++) ring.Add(1000);
        for (int i = 1; i <= 100; i++) ring.Add(i);
        foreach (double invalid in new[] { double.NaN, -1, double.PositiveInfinity }) ring.Add(invalid);
        var sample = ring.Snapshot();
        Assert.Equal(100, sample.Samples);
        Assert.Equal(50, sample.P50); Assert.Equal(95, sample.P95); Assert.Equal(99, sample.P99);
        Assert.Equal(Math.Sqrt(9999.0 / 12), sample.StdDev, 9);
        Assert.Equal(0, sample.Stutters);
        for (int i = 0; i < 95; i++) ring.Add(10);
        foreach (double interval in new[] { 20, 20.001, 25, 30, 100 }) ring.Add(interval);
        Assert.Equal(4, ring.Snapshot().Stutters);
        Assert.Equal(10, ring.Snapshot().P50);
        Assert.Equal(default, new FrameIntervalRing(4).Snapshot());
    }

    [Fact]
    public void PhaseDurationsBelongToThePresentedFrame()
    {
        var tracker = new LatencyPhaseTracker();
        tracker.Mark(42, LatencyMarker.InputSample, 100);
        tracker.Mark(42, LatencyMarker.SimulationStart, 105);
        tracker.Mark(42, LatencyMarker.SimulationEnd, 125);
        tracker.Mark(42, LatencyMarker.RenderSubmitStart, 130);
        tracker.Mark(42, LatencyMarker.RenderSubmitEnd, 160);
        tracker.Mark(42, LatencyMarker.PresentStart, 165);
        tracker.Mark(42, LatencyMarker.PresentEnd, 172);

        Assert.False(tracker.TryComplete(41, 900, out _));
        Assert.True(tracker.TryComplete(42, 901, out var report));
        Assert.Equal(new LatencyFrameReport(42, 901, 5, 20, 30, 7, 72), report);
        Assert.False(tracker.TryComplete(42, 902, out _));
    }

    [Fact]
    public void InterruptedFramesCannotLeakTimestampsIntoTheNextReport()
    {
        var warnings = new List<string>();
        var tracker = new LatencyPhaseTracker(warnings.Add);
        tracker.Mark(1, LatencyMarker.SimulationStart, 10);
        tracker.Mark(2, LatencyMarker.SimulationStart, 100);
        tracker.Mark(3, LatencyMarker.SimulationStart, 200);
        tracker.Mark(3, LatencyMarker.SimulationEnd, 230);
        tracker.Mark(3, LatencyMarker.PresentEnd, 250);

        Assert.True(tracker.TryComplete(3, 7, out var report));
        Assert.Equal(30UL, report.SimulationUs);
        Assert.Equal(50UL, report.TotalUs);
        Assert.Equal(0UL, report.RenderSubmitUs);
        Assert.Equal(0UL, report.PresentUs);
        Assert.Single(warnings);
    }

    [Fact]
    public void UnconsumedReportsStayBoundedAndDrainInFrameOrder()
    {
        var recorder = new FrameTimingRecorder();
        for (ulong frame = 1; frame <= 300; frame++)
        {
            recorder.Marker(frame, LatencyMarker.InputSample);
            recorder.Marker(frame, LatencyMarker.PresentEnd);
            recorder.OnPresent(frame, frame + 1000);
        }
        var reports = recorder.TakeReports();
        Assert.Equal(256, reports.Length);
        Assert.Equal(Enumerable.Range(45, 256).Select(i => (ulong)i), reports.Select(r => r.FrameId));
        Assert.All(reports, r => Assert.Equal(r.FrameId + 1000, r.PresentId));
        Assert.Empty(recorder.TakeReports());
    }

    [Fact]
    public void MissingOrReversedPhasesDoNotInventElapsedTime()
    {
        var report = LatencyFrameReport.FromCpuTimestamps(9, 12, 0, 100, 90, 0, 120, 130, 130);
        Assert.Equal(0UL, report.InputUs);
        Assert.Equal(0UL, report.SimulationUs);
        Assert.Equal(0UL, report.RenderSubmitUs);
        Assert.Equal(0UL, report.PresentUs);
        Assert.Equal(30UL, report.TotalUs);
    }

    [Fact]
    public void CpuPhaseReportsKeepInvariantNumbersAndKnownDurations()
    {
        var reports = new[] {
            new LatencyFrameReport(1, 10, 1000, 2000, 3000, 400, 16000),
            new LatencyFrameReport(2, 11, 3000, 4000, 5000, 600, 20000),
        };
        var mean = new double[VulkanStats.LatencyIntervalCount];
        var p99 = new double[VulkanStats.LatencyIntervalCount];
        VulkanStats.ReduceReports(reports, mean, p99);
        Assert.Equal(new[] { 2.0, 3, 4, 0.5, 18 }, mean);
        Assert.Equal(new[] { 3.0, 4, 5, 0.6, 20 }, p99);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var fields = VulkanStats.FormatLatencyLine(2, mean, p99).Split(' ').Skip(1)
                .Select(field => field.Split('=')).ToDictionary(field => field[0], field => field[1]);
            Assert.Equal("2", fields["frames"]);
            Assert.Equal("18.00", fields["total_mean_ms"]);
            Assert.Equal("0.60", fields["present_p99_ms"]);
            Assert.All(fields.Values, value => Assert.True(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
        VulkanStats.ReduceReports(Array.Empty<LatencyFrameReport>(), mean, p99);
        Assert.All(mean, value => Assert.Equal(0, value));
        Assert.All(p99, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ErrorsAreDeliveredOnceWithoutAllocatingOnTheEmptyPath()
    {
        using var device = GpuTest.NewDevice(); // Diagnostics do not require a GPU.
        device.AddDiagnosticForTests("warning");
        for (int i = 0; i < 1000; i++) device.GetError();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool unexpected = false;
        for (int i = 0; i < 10000; i++) unexpected |= device.GetError() != null;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(unexpected); Assert.Equal(0, allocated);
        Parallel.For(0, 256, i => device.AddDiagnosticForTests(VulkanContext.ErrorPrefix + i));
        string[] actual = device.GetError()!.Split('\n');
        Assert.Equal(256, actual.Length);
        Assert.Equal(Enumerable.Range(0, 256), actual.Select(line => int.Parse(line[VulkanContext.ErrorPrefix.Length..])).Order());
        Assert.Null(device.GetError());
        device.AddDiagnosticForTests(VulkanContext.ErrorPrefix + "first");
        device.AddDiagnosticForTests("advice");
        device.AddDiagnosticForTests(VulkanContext.ErrorPrefix + "second");
        Assert.Equal(VulkanContext.ErrorPrefix + "first\n" + VulkanContext.ErrorPrefix + "second", device.GetError());
        Assert.Null(device.GetError());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 1)]
    public async Task PacingGateAcceptsActualStatsAndRejectsBlockingUploads(int blockingUploads, int expectedExit)
    {
        string directory = Directory.CreateTempSubdirectory("optimum-pacing-").FullName;
        try
        {
            string fps = Path.Combine(directory, "fps.log"), stats = Path.Combine(directory, "stats.log");
            File.WriteAllText(fps, "[Optimum] fps window=1.002 frames=120 mean=8.350 min=7.900 max=10.100 p99=9.800 stddev=0.500\n");
            File.WriteAllText(stats, string.Join('\n',
                VulkanStats.FormatIntervalLine(1, 120, 0, 812, 0, 0, 0, 0, 0, 0),
                VulkanStats.FormatPacingLine(new FramePacingSnapshot(512, 8.3, 9.8, 10.7, 0.6, 0)),
                VulkanStats.FormatWaitsLine(new long[VulkanStats.WaitSiteCount], new double[VulkanStats.WaitSiteCount]),
                VulkanStats.FormatCountersLine(new CounterSample(blockingUploads, 0, 2640, 240, 0, 168000, 402112, 16777216))) + "\n");
            var start = new ProcessStartInfo(TestToolchain.Bash) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                CreateNoWindow = true, WorkingDirectory = ShaderCorpus.RepositoryRoot,
            };
            foreach (string argument in new[] { Path.Combine(ShaderCorpus.RepositoryRoot, "scripts", "dev", "pacing-gate.sh"),
                         "--renderer", "vulkan", "--fps", fps, "--stats", stats }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { process.Kill(entireProcessTree: true); throw; }
            output.WriteLine(await stdout + await stderr);
            Assert.Equal(expectedExit, process.ExitCode);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
