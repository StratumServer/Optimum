#nullable enable
using System;
using System.Collections.Generic;

namespace Vintagestory.GameContent;

public enum ProfilerStageId
{
    Before = 0,
    ShadowNear = 1,
    Opaque = 2,
    OIT = 3,
    AfterOIT = 4,
    AfterPostProcessing = 5,
    AfterFinalComposition = 6,
    AfterBlit = 7,
    Ortho = 8,
    Done = 9,
}

public enum GlCounterStageId
{
    ShadowNear = 0,
    Opaque = 1,
    OIT = 2,
    Ortho = 3,
}

public readonly struct ProfilerFrameSample
{
    public long FrameIndex { get; init; }
    public long StartUnixUs { get; init; }
    public double FrameMs { get; init; }
    public double[] CpuStagesMs { get; init; }
    public double[] GpuStagesMs { get; init; }
    public double[] CpuGapsMs { get; init; }
    public double[] GpuGapsMs { get; init; }
    public double GpuSpanMs { get; init; }
    public long AllocBytes { get; init; }
    public int Gen0Delta { get; init; }
    public int Gen1Delta { get; init; }
    public int Gen2Delta { get; init; }
    public double GcPauseMs { get; init; }
    public long LockContentionDelta { get; init; }
    public long[] Primitives { get; init; }
    public long[] SamplesPassed { get; init; }
    public int LoadedChunks { get; init; }
    public double MeshPoolMb { get; init; }
}

public sealed class ProfilerDistribution
{
    public int Count { get; init; }
    public double Mean { get; init; }
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double Max { get; init; }

    public static ProfilerDistribution Compute(ReadOnlySpan<double> values)
    {
        if (values.Length == 0)
        {
            return new ProfilerDistribution();
        }

        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        double sum = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            sum += sorted[i];
        }

        return new ProfilerDistribution
        {
            Count = sorted.Length,
            Mean = sum / sorted.Length,
            P50 = Percentile(sorted, 0.50),
            P95 = Percentile(sorted, 0.95),
            P99 = Percentile(sorted, 0.99),
            Max = sorted[sorted.Length - 1],
        };
    }

    public static double ComputeOnePercentLowFps(ReadOnlySpan<double> frameMs)
    {
        if (frameMs.Length == 0)
        {
            return 0.0;
        }

        double[] sorted = frameMs.ToArray();
        Array.Sort(sorted);
        double p99Ms = Percentile(sorted, 0.99);
        return p99Ms > 0.001 ? 1000.0 / p99Ms : 0.0;
    }

    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return 0.0;
        }
        double idx = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(idx);
        int hi = Math.Min(sorted.Length - 1, lo + 1);
        double frac = idx - lo;
        return sorted[lo] * (1.0 - frac) + sorted[hi] * frac;
    }
}

public sealed class ProfilerVerdict
{
    public string Category { get; init; } = "BALANCED";
    public string Reason { get; init; } = "";

    public static ProfilerVerdict ClassifyBottleneck(
        double frameMeanMs,
        double gpuSpanMeanMs,
        double gcPausePct,
        double lockContentionsPerSec,
        string? dominantGpuStage = null,
        double dominantGpuStageMs = 0.0)
    {
        if (frameMeanMs <= 0.01)
        {
            return new ProfilerVerdict { Category = "WARMING UP", Reason = "Collecting frames" };
        }

        if (gcPausePct >= 5.0 || lockContentionsPerSec >= 100.0)
        {
            return new ProfilerVerdict
            {
                Category = "GC/LOCK STALL",
                Reason = $"GC pause {gcPausePct:F1}%, locks {lockContentionsPerSec:F0}/s",
            };
        }

        double gpuRatio = gpuSpanMeanMs / frameMeanMs;
        if (gpuSpanMeanMs > 0.1 && gpuRatio >= 0.72)
        {
            string stageText = !string.IsNullOrEmpty(dominantGpuStage) && dominantGpuStageMs > 0.05
                ? $"{dominantGpuStage} {dominantGpuStageMs:F1}ms ({gpuRatio * 100:F0}% GPU)"
                : $"GPU span {gpuSpanMeanMs:F1}ms / {frameMeanMs:F1}ms";
            return new ProfilerVerdict
            {
                Category = "GPU-BOUND",
                Reason = stageText,
            };
        }

        if (gpuSpanMeanMs <= 0.05 || gpuRatio <= 0.55)
        {
            return new ProfilerVerdict
            {
                Category = "CPU-BOUND",
                Reason = $"GPU {gpuSpanMeanMs:F1}ms vs Frame {frameMeanMs:F1}ms",
            };
        }

        return new ProfilerVerdict
        {
            Category = "BALANCED",
            Reason = $"GPU {gpuSpanMeanMs:F1}ms / Frame {frameMeanMs:F1}ms",
        };
    }
}

public sealed class ProfilerAllocTypeEntry
{
    public string TypeName { get; init; } = "";
    public long Bytes { get; init; }
    public long Ticks { get; init; }
    public double SharePct { get; init; }
    public long LohBytes { get; init; }
}

public sealed class ProfilerRuntimeSnapshot
{
    public long SampledAllocBytes { get; init; }
    public long SohBytes { get; init; }
    public long LohBytes { get; init; }
    public long PohBytes { get; init; }
    public long AllocTicks { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public double GcPauseTotalMs { get; init; }
    public double GcPauseMaxMs { get; init; }
    public long ContentionCount { get; init; }
    public double ContentionTotalMs { get; init; }
    public double ContentionMaxMs { get; init; }
    public IReadOnlyList<ProfilerAllocTypeEntry> TopTypes { get; init; } = Array.Empty<ProfilerAllocTypeEntry>();
}

public sealed class ProfilerMeshPoolPassStat
{
    public string Pass { get; init; } = "";
    public int PoolCount { get; init; }
    public int UsedVertices { get; init; }
    public int CapacityVertices { get; init; }
    public int AllocatedTris { get; init; }
    public int RenderedTris { get; init; }
    public int Groups { get; init; }
}

public sealed class ProfilerOptimumCounterStat
{
    public string Name { get; init; } = "";
    public long Hits { get; init; }
    public long Skips { get; init; }
    public double SkipRatePct { get; init; }
}

public sealed class ProfilerMarkerStat
{
    public string Path { get; init; } = "";
    public double TotalMs { get; init; }
    public double MaxMs { get; init; }
    public int Calls { get; init; }
}

public sealed class ProfilerCensusSnapshot
{
    public string GlVendor { get; init; } = "";
    public string GlRenderer { get; init; } = "";
    public string GlVersion { get; init; } = "";
    public string OptimumVersion { get; init; } = "";
    public int ChunksTotal { get; init; }
    public int ChunksEmpty { get; init; }
    public int ChunksUnpacked { get; init; }
    public int ChunksAwaitingTessellation { get; init; }
    public int ChunksTesselatedPerSecond { get; init; }
    public int ChunksTesselatedTotal { get; init; }
    public int DirtyQueueCount { get; init; }
    public int UploadedQueueCount { get; init; }
    public int EntitiesLoaded { get; init; }
    public int EntitiesRendered { get; init; }
    public int EntitiesShadowRendered { get; init; }
    public int EntitiesActiveAnimators { get; init; }
    public int ShaderCount { get; init; }
    public int ShaderLoadErrors { get; init; }
    public int OpenedGuiCount { get; init; }
    public string ShadowFarRenderersSummary { get; init; } = "";
    public IReadOnlyList<ProfilerMeshPoolPassStat> MeshPools { get; init; } = Array.Empty<ProfilerMeshPoolPassStat>();
    public IReadOnlyList<KeyValuePair<string, int>> TopBlockEntities { get; init; } = Array.Empty<KeyValuePair<string, int>>();
    public IReadOnlyList<string> OpenedGuis { get; init; } = Array.Empty<string>();
    public IReadOnlyList<KeyValuePair<string, string>> OptimumToggles { get; init; } = Array.Empty<KeyValuePair<string, string>>();
    public IReadOnlyList<ProfilerOptimumCounterStat> OptimumCounters { get; init; } = Array.Empty<ProfilerOptimumCounterStat>();
    public IReadOnlyList<KeyValuePair<string, string>> OptimumSummaries { get; init; } = Array.Empty<KeyValuePair<string, string>>();
}

public sealed class OptimumProfilerSnapshot
{
    public int TotalFrames { get; init; }
    public double WindowSeconds { get; init; }
    public double AvgFps { get; init; }
    public double Low1Fps { get; init; }
    public ProfilerDistribution FrameDist { get; init; } = new();
    public ProfilerDistribution GpuSpanDist { get; init; } = new();
    public double AllocMbPerSec { get; init; }
    public long Gen0Count { get; init; }
    public long Gen1Count { get; init; }
    public long Gen2Count { get; init; }
    public double GcPausePct { get; init; }
    public double LockContentionsPerSec { get; init; }
    public bool GlCountersEnabled { get; init; }
    public bool IsRecording { get; init; }
    public double RecordingElapsedSec { get; init; }
    public double RecordingTargetSec { get; init; }
    public string LastExportPath { get; init; } = "";
    public double[] RecentFrameMs { get; init; } = Array.Empty<double>();
    public KeyValuePair<string, double>[] StageCpuMeansMs { get; init; } = Array.Empty<KeyValuePair<string, double>>();
    public KeyValuePair<string, double>[] StageGpuMeansMs { get; init; } = Array.Empty<KeyValuePair<string, double>>();
    public long[] GlPrimitivesMean { get; init; } = Array.Empty<long>();
    public long[] GlSamplesPassedMean { get; init; } = Array.Empty<long>();
    public ProfilerVerdict Verdict { get; init; } = new();
    public ProfilerRuntimeSnapshot Runtime { get; init; } = new();
    public ProfilerCensusSnapshot Census { get; init; } = new();
    public IReadOnlyList<ProfilerMarkerStat> FrameProfilerMarkers { get; init; } = Array.Empty<ProfilerMarkerStat>();
}
