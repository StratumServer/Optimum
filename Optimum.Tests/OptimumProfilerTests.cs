using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

public sealed class OptimumProfilerTests
{
    private static int Mask(params ProfilerMarker[] markers) => markers.Aggregate(0, (m, k) => m | 1 << (int)k);

    [Fact]
    public void DistributionComputesPercentilesAndOnePercentLow()
    {
        Assert.Equal(0, ProfilerDistribution.Compute(Span<double>.Empty).Count);

        double[] values = [100.0, 10.0, 40.0, 20.0, 30.0];
        ProfilerDistribution dist = ProfilerDistribution.Compute(values);
        Assert.Equal(5, dist.Count);
        Assert.Equal(40.0, dist.Mean, 3);
        Assert.Equal(30.0, dist.P50, 3);
        Assert.Equal(100.0, dist.Max, 3);
        Assert.InRange(dist.OnePercentLowFps, 10.0, 11.0);
    }

    [Fact]
    public void SegmentsPartitionTheRenderSpanWithoutDroppingTime()
    {
        long[] stamps = new long[ProfilerSegments.MarkerCount];
        for (int m = 0; m < stamps.Length; m++)
        {
            stamps[m] = 1000 + m * m * 10;
        }
        int all = (1 << ProfilerSegments.MarkerCount) - 1;
        double[] segments = new double[ProfilerSegments.Count];

        double span = ProfilerSegments.Compute(stamps, all, 1.0, segments);

        Assert.Equal(stamps[^1] - stamps[0], span, 6);
        Assert.Equal(span, segments.Sum(), 6);
        Assert.Equal(stamps[(int)ProfilerMarker.AfterOitStart] - stamps[(int)ProfilerMarker.OitStart], segments[4], 6);
        // AfterBlit and Ortho both feed the Ortho segment.
        Assert.Equal(stamps[(int)ProfilerMarker.DoneStart] - stamps[(int)ProfilerMarker.AfterBlitStart], segments[8], 6);
    }

    [Fact]
    public void SkippedStagesFoldIntoTheirPredecessor()
    {
        long[] stamps = new long[ProfilerSegments.MarkerCount];
        for (int m = 0; m < stamps.Length; m++)
        {
            stamps[m] = m * 100;
        }
        // Shadow quality 1 (no ShadowNear), no OIT pass, GUI hidden (no Ortho).
        int mask = ((1 << ProfilerSegments.MarkerCount) - 1)
            & ~Mask(ProfilerMarker.ShadowNearStart, ProfilerMarker.OitStart, ProfilerMarker.OrthoStart);
        double[] segments = new double[ProfilerSegments.Count];

        double span = ProfilerSegments.Compute(stamps, mask, 1.0, segments);

        Assert.Equal(1100.0, span, 6);
        Assert.Equal(span, segments.Sum(), 6);
        Assert.Equal(200.0, segments[1], 6); // ShadowFar runs until Opaque starts.
        Assert.Equal(0.0, segments[2], 6);
        Assert.Equal(200.0, segments[3], 6); // Opaque runs until AfterOIT starts.
        Assert.Equal(0.0, segments[4], 6);
        Assert.Equal(200.0, segments[8], 6);
    }

    [Fact]
    public void VerdictClassifiesByWhereTheRenderThreadWaits()
    {
        // GPU behind the CPU: the CPU submits in 9 ms and waits in SwapBuffers.
        ProfilerVerdict gpu = ProfilerVerdict.Classify(new ProfilerVerdictInput(20, 9, 11, 19, double.NaN, double.NaN, double.NaN, 0.2, false, "Opaque", 4, "ShadowNear", 7));
        Assert.Equal("GPU-BOUND", gpu.Category);
        Assert.Contains("ShadowNear", gpu.Reason);

        // GPU span close to the frame only because it follows CPU submission.
        ProfilerVerdict tracking = ProfilerVerdict.Classify(new ProfilerVerdictInput(20, 18, 2, 18.5, double.NaN, double.NaN, double.NaN, 0.2, false, "Opaque", 11, "Opaque", 8));
        Assert.Equal("CPU-BOUND", tracking.Category);
        Assert.Contains("Opaque", tracking.Reason);

        // FrameProfiler waits: a long SwapBuffers wait means the driver holds the CPU back for the GPU or presentation.
        ProfilerVerdict swapBusyGpu = ProfilerVerdict.Classify(new ProfilerVerdictInput(25, 12.8, 12.2, 21, 0, 9.9, 2.3, 0.2, false, "Opaque", 6.7, "Opaque", 8));
        Assert.Equal("GPU-BOUND", swapBusyGpu.Category);
        Assert.Contains("SwapBuffers", swapBusyGpu.Reason);

        // Measured in the offscreen client: SwapBuffers waits 9.9 of 25 ms while the GPU span is 17.2 ms.
        // GC at 6% must not hide it.
        ProfilerVerdict present = ProfilerVerdict.Classify(new ProfilerVerdictInput(25, 12.8, 12.2, 17.2, 0, 9.9, 2.3, 6.0, false, "Opaque", 6.7, "Opaque", 8));
        Assert.Equal("PRESENT-BOUND", present.Category);

        ProfilerVerdict tick = ProfilerVerdict.Classify(new ProfilerVerdictInput(30, 8, 22, 6, 0, 0.5, 20, 0.2, false, "Opaque", 4, "Opaque", 3));
        Assert.Equal("CPU-BOUND", tick.Category);
        Assert.Contains("Tick+Input", tick.Reason);

        // VSync or an FPS cap idling most of the frame is not a CPU bottleneck.
        ProfilerVerdict limit = ProfilerVerdict.Classify(new ProfilerVerdictInput(16.7, 4, 12.7, 5, double.NaN, double.NaN, double.NaN, 0.1, true, "Opaque", 2, "Opaque", 3));
        Assert.Equal("FPS LIMIT", limit.Category);

        // HUD only (no FrameProfiler waits): time outside rendering dominates but tick and swap cannot be split.
        ProfilerVerdict unsplit = ProfilerVerdict.Classify(new ProfilerVerdictInput(20, 6, 14, 5, double.NaN, double.NaN, double.NaN, 0.2, false, "Opaque", 3, "Opaque", 2));
        Assert.Equal("TICK OR SWAP", unsplit.Category);

        ProfilerVerdict gc = ProfilerVerdict.Classify(new ProfilerVerdictInput(50, 10, 40, 12, double.NaN, double.NaN, double.NaN, 8.5, false, "Opaque", 4, "Opaque", 5));
        Assert.Equal("GC STALL", gc.Category);
    }

    [Fact]
    public void SunOcclusionQueryStaysOutsideTheOpaqueCounterWindows()
    {
        string sunMoon = File.ReadAllText(PatchReader.FindRepositoryFile("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSunMoon.cs"));
        Match order = Regex.Match(sunMoon, @"RegisterRenderer\(OnRenderFrame3DPost, EnumRenderStage\.Opaque, Name, ([0-9.]+)\)");
        Assert.True(order.Success);
        Assert.Equal(ProfilerGlCounters.SunQueryOrder, double.Parse(order.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Contains("GL.BeginQuery((QueryTarget)35092", sunMoon); // GL_SAMPLES_PASSED

        string session = File.ReadAllText(PatchReader.FindRepositoryFile("sources/VSEssentials/Systems/Profiler/OptimumProfilerSession.cs"));
        Assert.Contains("ProfilerGlCounters.SunQueryOrder - 0.5, endWindow: 1", session);
        Assert.Contains("ProfilerGlCounters.SunQueryOrder + 0.5, beginWindow: 2", session);
        Assert.DoesNotContain("EnumRenderStage.ShadowFar,", session);
        Assert.Equal(1, ProfilerGlCounters.CounterOfWindow[1]);
        Assert.Equal(1, ProfilerGlCounters.CounterOfWindow[2]);
    }

    [Fact]
    public void RuntimeEventListenerStartsResetsAndDisposesCleanly()
    {
        var listener = new OptimumRuntimeEventListener();
        var list = new List<byte[]>(64);
        for (int i = 0; i < 64; i++)
        {
            list.Add(new byte[4096]);
        }
        GC.KeepAlive(list);
        Assert.NotNull(listener.GetSnapshot(10).TopTypes);

        // The runtime source is process-wide and other tests allocate in parallel, so events can
        // keep arriving after Reset while the listener is live. After Dispose none are counted.
        listener.Dispose();
        listener.Reset();
        ProfilerRuntimeSnapshot afterReset = listener.GetSnapshot(10);
        Assert.Equal(0, afterReset.AllocTicks);
        Assert.Equal(0, afterReset.ContentionCount);
        Assert.Empty(afterReset.TopTypes);
    }

    [Fact]
    public void PerfettoExporterWritesTraceSummaryAndInvariantMarkdown()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"opt-prof-test-{Guid.NewGuid():N}");
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
            var frame = new ProfilerFrameSample
            {
                FrameIndex = 7,
                StartTicks = 1_000_000,
                FrameMs = 20.0,
                OutsideRenderMs = 4.5,
                CpuRenderMs = 15.5,
                HasGpu = true,
                GpuSpanMs = 17.5,
                AllocBytes = 512 * 1024,
                Gen0Delta = 1,
                GcPauseMs = 0.8,
                LockContentionDelta = 2,
                LoadedChunks = 1200,
                MeshPoolMb = 64.5,
            };
            frame.Cpu[3] = 10.0;
            frame.Gpu[3] = 12.0;
            var snapshot = new OptimumProfilerSnapshot
            {
                TotalFrames = 1,
                GpuFrames = 1,
                WindowSeconds = 0.02,
                AvgFps = 50.25,
                FrameDist = ProfilerDistribution.Compute(new[] { 20.0 }),
                Instrumentation = "probes",
                StageCpuMeansMs = [new KeyValuePair<string, double>("Opaque", 10.0), new KeyValuePair<string, double>("Tick+Swap", 4.5)],
                StageGpuMeansMs = [new KeyValuePair<string, double>("Opaque", 12.0), new KeyValuePair<string, double>("Tick+Swap", 0.0)],
                Census = new ProfilerCensusSnapshot { OptimumVersion = "0.4.5", GlRenderer = "TestGL", GlVersion = "4.6" },
            };

            OptimumPerfettoExporter.ExportBundle(tempDir, [frame], snapshot);

            using (var gz = new GZipStream(File.OpenRead(Path.Combine(tempDir, "trace.perfetto.json.gz")), CompressionMode.Decompress))
            using (JsonDocument doc = JsonDocument.Parse(gz))
            {
                JsonElement[] events = doc.RootElement.GetProperty("traceEvents").EnumerateArray().ToArray();
                JsonElement opaque = events.Single(e => e.GetProperty("name").GetString() == "CPU:Opaque");
                // Opaque follows Tick+Swap (4.5 ms) and the empty segments before it.
                Assert.Equal(4500.0, opaque.GetProperty("ts").GetDouble(), 3);
                Assert.Equal(10000.0, opaque.GetProperty("dur").GetDouble(), 3);
                Assert.Contains(events, e => e.GetProperty("name").GetString() == "GPU:Opaque");
                Assert.All(events.Where(e => e.TryGetProperty("args", out JsonElement a) && a.TryGetProperty("frame", out _)),
                    e => Assert.Equal(7, e.GetProperty("args").GetProperty("frame").GetInt64()));
            }

            using (JsonDocument summary = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(tempDir, "summary.json"))))
            {
                Assert.Equal("0.4.5", summary.RootElement.GetProperty("optimum_version").GetString());
                Assert.Equal("probes", summary.RootElement.GetProperty("instrumentation").GetString());
                Assert.Equal(10.0, summary.RootElement.GetProperty("stages_cpu_ms").GetProperty("Opaque").GetDouble());
            }

            string md = File.ReadAllText(Path.Combine(tempDir, "report.md"));
            Assert.Contains("**50.25 FPS**", md);
            Assert.DoesNotContain("50,25", md);
            Assert.DoesNotContain("\u2014", md);
            Assert.DoesNotContain("\u2013", md);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void SettingsPatchPatcherAndStatusWireExplicitProfilerOptIn()
    {
        string patch = File.ReadAllText(PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch"));
        string patcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        string enLang = File.ReadAllText(PatchReader.FindRepositoryFile("sources/lang/en.json"));
        string ptLang = File.ReadAllText(PatchReader.FindRepositoryFile("sources/lang/pt-br.json"));
        string status = File.ReadAllText(PatchReader.FindRepositoryFile("sources/VSEssentials/Systems/OptimumStatus.cs"));

        Assert.Contains("onOptimumProfilerClicked", patch);
        Assert.Contains("\"optimum:profiler:open\"", patch);
        Assert.Contains("\"onOptimumProfilerClicked\"", patcher);
        Assert.Contains("\"optimum-profiler-button\"", enLang);
        Assert.Contains("\"optimum-profiler-button\"", ptLang);
        Assert.Contains(".BeginSubCommand(\"profiler\")", status);
        Assert.Contains("private OptimumProfilerService? profilerService;", status);
    }
}
