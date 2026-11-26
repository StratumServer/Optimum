using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

public sealed class OptimumProfilerTests
{
    [Fact]
    public void ProfilerModelsAreDormantAndComputePercentilesAccurately()
    {
        ProfilerDistribution empty = ProfilerDistribution.Compute(ReadOnlySpan<double>.Empty);
        Assert.Equal(0, empty.Count);
        Assert.Equal(0.0, empty.Mean);

        double[] values = [10.0, 20.0, 30.0, 40.0, 100.0];
        ProfilerDistribution dist = ProfilerDistribution.Compute(values);
        Assert.Equal(5, dist.Count);
        Assert.Equal(40.0, dist.Mean, 3);
        Assert.Equal(30.0, dist.P50, 3);
        Assert.Equal(100.0, dist.Max, 3);
        Assert.True(dist.P95 >= dist.P50);

        double low1 = ProfilerDistribution.ComputeOnePercentLowFps(values);
        Assert.InRange(low1, 10.0, 11.0);
    }

    [Fact]
    public void BottleneckClassifierDistinguishesGpuCpuGcAndBalanced()
    {
        ProfilerVerdict gpu = ProfilerVerdict.ClassifyBottleneck(50.0, 42.0, 0.2, 2.0, "ShadowNear", 18.0);
        Assert.Equal("GPU-BOUND", gpu.Category);
        Assert.Contains("ShadowNear", gpu.Reason);

        ProfilerVerdict cpu = ProfilerVerdict.ClassifyBottleneck(50.0, 12.0, 0.2, 2.0);
        Assert.Equal("CPU-BOUND", cpu.Category);

        ProfilerVerdict gc = ProfilerVerdict.ClassifyBottleneck(50.0, 12.0, 8.5, 2.0);
        Assert.Equal("GC/LOCK STALL", gc.Category);

        ProfilerVerdict balanced = ProfilerVerdict.ClassifyBottleneck(25.0, 16.0, 0.3, 5.0);
        Assert.Equal("BALANCED", balanced.Category);
    }

    [Fact]
    public void RuntimeEventListenerStartsResetsAndDisposesCleanly()
    {
        using var listener = new OptimumRuntimeEventListener();
        var list = new List<byte[]>(64);
        for (int i = 0; i < 64; i++)
        {
            list.Add(new byte[4096]);
        }
        GC.KeepAlive(list);

        ProfilerRuntimeSnapshot snap = listener.GetSnapshot(10);
        Assert.NotNull(snap.TopTypes);

        listener.Reset();
        ProfilerRuntimeSnapshot afterReset = listener.GetSnapshot(10);
        Assert.Equal(0, afterReset.AllocTicks);
        Assert.Equal(0, afterReset.ContentionCount);
    }

    [Fact]
    public void PerfettoExporterWritesValidGzipTraceSummaryAndMarkdownWithoutDashes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"opt-prof-test-{Guid.NewGuid():N}");
        try
        {
            var samples = new List<ProfilerFrameSample>
            {
                new()
                {
                    FrameIndex = 1,
                    StartUnixUs = 1_000_000,
                    FrameMs = 20.0,
                    CpuStagesMs = [1.0, 0.2, 2.0, 0.5, 0.1, 0.1, 0.1, 0.1, 0.2, 0.1],
                    GpuStagesMs = [0.2, 5.0, 8.0, 2.0, 0.1, 0.1, 0.1, 0.1, 0.2, 0.0],
                    GpuSpanMs = 17.5,
                    CpuGapsMs = [0.4, 0.0, 0.2, 0.3, 0.1, 0.1],
                    GpuGapsMs = [4.0, 0.0, 0.2, 3.0, 0.8, 0.2],
                    AllocBytes = 1024 * 512,
                    Gen0Delta = 1,
                    GcPauseMs = 0.8,
                    LockContentionDelta = 2,
                    LoadedChunks = 1200,
                    MeshPoolMb = 64.5,
                }
            };

            var snapshot = new OptimumProfilerSnapshot
            {
                TotalFrames = 1,
                WindowSeconds = 0.02,
                AvgFps = 50.0,
                Low1Fps = 50.0,
                FrameDist = ProfilerDistribution.Compute([20.0]),
                GpuSpanDist = ProfilerDistribution.Compute([17.5]),
                AllocMbPerSec = 25.0,
                Gen0Count = 1,
                Verdict = ProfilerVerdict.ClassifyBottleneck(20.0, 17.5, 0.1, 1.0, "Opaque", 8.0),
                StageCpuMeansMs = [new KeyValuePair<string, double>("Opaque", 2.0)],
                StageGpuMeansMs = [new KeyValuePair<string, double>("Opaque", 8.0)],
                Census = new ProfilerCensusSnapshot { OptimumVersion = "0.4.5", GlRenderer = "TestGL", GlVersion = "4.6" },
            };

            OptimumPerfettoExporter.ExportBundle(tempDir, samples, snapshot);

            string traceGz = Path.Combine(tempDir, "trace.perfetto.json.gz");
            string summaryJson = Path.Combine(tempDir, "summary.json");
            string reportMd = Path.Combine(tempDir, "report.md");

            Assert.True(File.Exists(traceGz));
            Assert.True(File.Exists(summaryJson));
            Assert.True(File.Exists(reportMd));

            using (var fs = File.OpenRead(traceGz))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var doc = JsonDocument.Parse(gz))
            {
                JsonElement events = doc.RootElement.GetProperty("traceEvents");
                Assert.True(events.GetArrayLength() >= 6);
            }

            byte[] rawSummary = File.ReadAllBytes(summaryJson);
            Assert.False(rawSummary.Length >= 3 && rawSummary[0] == 0xEF && rawSummary[1] == 0xBB && rawSummary[2] == 0xBF);
            using (JsonDocument sumDoc = JsonDocument.Parse(rawSummary))
            {
                Assert.Equal("0.4.5", sumDoc.RootElement.GetProperty("optimum_version").GetString());
            }

            string md = File.ReadAllText(reportMd);
            Assert.Contains("Optimum Integrated Profiler Report", md);
            Assert.DoesNotContain("\u2014", md);
            Assert.DoesNotContain("\u2013", md);
        }
        finally
        {
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
