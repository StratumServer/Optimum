#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Vintagestory.GameContent;

/// <summary>
/// Render-thread probe points in vanilla pipeline order. Each probed stage gets a start probe
/// (lowest render order); Before and Done also get an end probe. ShadowFar is never probed so
/// OptimumShadowFarReuse keeps its renderer list untouched.
/// </summary>
public enum ProfilerMarker
{
    BeforeStart = 0,
    BeforeEnd = 1,
    ShadowNearStart = 2,
    OpaqueStart = 3,
    OitStart = 4,
    AfterOitStart = 5,
    AfterPostProcessingStart = 6,
    AfterFinalCompositionStart = 7,
    AfterBlitStart = 8,
    OrthoStart = 9,
    DoneStart = 10,
    DoneEnd = 11,
}

/// <summary>
/// Contiguous segments of the render span (Before start to Done end). A marker owns the time
/// until the next marker that fired this frame, so skipped stages (no shadows, no OIT, hidden
/// GUI) fold into their predecessor and no time is dropped.
/// </summary>
public static class ProfilerSegments
{
    public const int MarkerCount = 12;
    public const int Count = 10;

    public static readonly string[] Labels =
    {
        "Before", "ShadowFar", "ShadowNear", "Opaque", "OIT", "PostProc", "FinalComp", "Blit/FSR", "Ortho", "Done",
    };

    /// <summary>Segment owned by each marker; DoneEnd closes the span and owns nothing.</summary>
    private static readonly int[] SegmentOfMarker = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 8, 9, -1 };

    public static int SegmentOf(ProfilerMarker marker) => SegmentOfMarker[(int)marker];

    /// <summary>
    /// Splits one frame's marker timestamps into segment durations. Returns the render span
    /// (first to last visited marker) in milliseconds, or 0 when fewer than two markers fired.
    /// </summary>
    public static double Compute(ReadOnlySpan<long> stamps, int visitedMask, double unitsToMs, Span<double> segmentsMs)
    {
        segmentsMs.Clear();
        int first = -1;
        int prev = -1;
        for (int m = 0; m < MarkerCount; m++)
        {
            if ((visitedMask & (1 << m)) == 0)
            {
                continue;
            }
            if (prev >= 0)
            {
                segmentsMs[SegmentOfMarker[prev]] += Math.Max(0, stamps[m] - stamps[prev]) * unitsToMs;
            }
            else
            {
                first = m;
            }
            prev = m;
        }
        return first >= 0 && prev > first ? Math.Max(0, stamps[prev] - stamps[first]) * unitsToMs : 0.0;
    }
}

/// <summary>
/// Opt-in hardware counter windows. Opaque is split around vanilla SystemRenderSunMoon's
/// SAMPLES_PASSED occlusion query (Opaque, order 999): only one occlusion query may be active,
/// so a window spanning it would break the sun query and truncate ours.
/// </summary>
public static class ProfilerGlCounters
{
    public const int Count = 4;
    public const int WindowCount = 5;
    public const double SunQueryOrder = 999.0;

    public static readonly string[] Labels = { "ShadowNear", "Opaque", "OIT", "Ortho" };

    /// <summary>Reported counter for each window: ShadowNear, Opaque (before sun), Opaque (after sun), OIT, Ortho.</summary>
    public static readonly int[] CounterOfWindow = { 0, 1, 1, 2, 3 };
}

[InlineArray(ProfilerSegments.Count)]
public struct ProfilerSegmentTimes
{
    private double element0;
}

[InlineArray(ProfilerGlCounters.Count)]
public struct ProfilerCounterValues
{
    private long element0;
}

/// <summary>
/// One frame, stored by value in preallocated rings so sampling never allocates. GPU segments
/// arrive two to four frames later and are written back by <see cref="FrameIndex"/>.
/// </summary>
public struct ProfilerFrameSample
{
    public long FrameIndex;
    /// <summary>Stopwatch ticks at the end of the previous frame (Done end), i.e. this frame's start.</summary>
    public long StartTicks;
    public double FrameMs;
    /// <summary>CPU time between frame start and Before start: swap, FPS limiter, input and game tick.</summary>
    public double OutsideRenderMs;
    public double CpuRenderMs;
    public ProfilerSegmentTimes Cpu;
    public bool HasGpu;
    public double GpuSpanMs;
    public ProfilerSegmentTimes Gpu;
    public bool HasCounters;
    public ProfilerCounterValues Primitives;
    public ProfilerCounterValues SamplesPassed;
    /// <summary>FrameProfiler waits (sleep = FPS limiter, swap = SwapBuffers/vsync/driver) when markers are on, else NaN.</summary>
    public double SleepMs;
    public double SwapMs;
    public long AllocBytes;
    public int Gen0Delta;
    public int Gen1Delta;
    public int Gen2Delta;
    public double GcPauseMs;
    public long LockContentionDelta;
    public int LoadedChunks;
    public double MeshPoolMb;
}

public sealed class ProfilerDistribution
{
    public int Count { get; init; }
    public double Mean { get; init; }
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double Max { get; init; }

    /// <summary>Sorts <paramref name="values"/> in place.</summary>
    public static ProfilerDistribution Compute(Span<double> values)
    {
        if (values.Length == 0)
        {
            return new ProfilerDistribution();
        }

        values.Sort();
        double sum = 0;
        foreach (double v in values)
        {
            sum += v;
        }

        return new ProfilerDistribution
        {
            Count = values.Length,
            Mean = sum / values.Length,
            P50 = Percentile(values, 0.50),
            P95 = Percentile(values, 0.95),
            P99 = Percentile(values, 0.99),
            Max = values[^1],
        };
    }

    public double OnePercentLowFps => P99 > 0.001 ? 1000.0 / P99 : 0.0;

    private static double Percentile(ReadOnlySpan<double> sorted, double q)
    {
        double idx = q * (sorted.Length - 1);
        int lo = (int)idx;
        int hi = Math.Min(sorted.Length - 1, lo + 1);
        double frac = idx - lo;
        return sorted[lo] * (1.0 - frac) + sorted[hi] * frac;
    }
}

/// <summary>Mean per-frame timing inputs for <see cref="ProfilerVerdict.Classify"/>.</summary>
public readonly record struct ProfilerVerdictInput(
    double FrameMs,
    double CpuRenderMs,
    double OutsideRenderMs,
    double GpuSpanMs,
    double SleepMs,
    double SwapMs,
    double TickMs,
    double GcPausePct,
    bool FrameLimiterActive,
    string DominantCpuSegment,
    double DominantCpuMs,
    string DominantGpuSegment,
    double DominantGpuMs);

public sealed class ProfilerVerdict
{
    public string Category { get; init; } = "WARMING UP";
    public string Reason { get; init; } = "";

    /// <summary>
    /// Classifies by where the render thread waits: frame limiter, SwapBuffers, GPU, GC, then CPU. GPU timestamps only track the GPU, and a GPU
    /// that waits for CPU submission still reports a span close to the CPU render time, so a
    /// GPU-bound frame must also show the GPU running behind the CPU (span above CPU render time)
    /// or a long SwapBuffers wait.
    /// </summary>
    public static ProfilerVerdict Classify(in ProfilerVerdictInput f)
    {
        if (f.FrameMs <= 0.01)
        {
            return new ProfilerVerdict { Reason = "Collecting frames" };
        }

        bool hasWaits = !double.IsNaN(f.SwapMs);
        double gpuRatio = f.GpuSpanMs / f.FrameMs;
        if (f.FrameLimiterActive)
        {
            double idleMs = hasWaits ? f.SleepMs + f.SwapMs : f.OutsideRenderMs;
            if (idleMs >= 0.25 * f.FrameMs && gpuRatio < 0.85)
            {
                return new ProfilerVerdict { Category = "FPS LIMIT", Reason = $"VSync/FPS cap idles {idleMs:F1}ms of {f.FrameMs:F1}ms" };
            }
        }

        // With no frame limiter, a long SwapBuffers wait is the driver holding the CPU back until the
        // GPU catches up, or, with an idle GPU, the presentation path (compositor, remoting, readback).
        if (hasWaits && !f.FrameLimiterActive && f.SwapMs >= 0.2 * f.FrameMs)
        {
            return new ProfilerVerdict
            {
                Category = gpuRatio >= 0.75 ? "GPU-BOUND" : "PRESENT-BOUND",
                Reason = $"SwapBuffers waits {f.SwapMs:F1}ms, GPU {f.GpuSpanMs:F1}ms / {f.FrameMs:F1}ms",
            };
        }

        if (f.GpuSpanMs > 0.1 && gpuRatio >= 0.75 && f.GpuSpanMs > 1.1 * f.CpuRenderMs)
        {
            string reason = !string.IsNullOrEmpty(f.DominantGpuSegment) && f.DominantGpuMs > 0.05
                ? $"{f.DominantGpuSegment} {f.DominantGpuMs:F1}ms, GPU {f.GpuSpanMs:F1}ms / {f.FrameMs:F1}ms"
                : $"GPU {f.GpuSpanMs:F1}ms / {f.FrameMs:F1}ms";
            return new ProfilerVerdict { Category = "GPU-BOUND", Reason = reason };
        }

        if (f.GcPausePct >= 5.0)
        {
            return new ProfilerVerdict { Category = "GC STALL", Reason = $"GC pauses {f.GcPausePct:F1}% of frame time" };
        }

        // A GPU span that only tracks CPU submission says nothing about GPU load: CPU-bound.
        string cpuTop = f.DominantCpuSegment;
        double cpuTopMs = f.DominantCpuMs;
        if (hasWaits && f.TickMs > cpuTopMs)
        {
            cpuTop = "Tick+Input";
            cpuTopMs = f.TickMs;
        }
        else if (!hasWaits && f.OutsideRenderMs > cpuTopMs)
        {
            // Without the window's FrameProfiler waits, game tick and SwapBuffers cannot be told apart.
            return new ProfilerVerdict
            {
                Category = "TICK OR SWAP",
                Reason = $"Outside rendering {f.OutsideRenderMs:F1}ms of {f.FrameMs:F1}ms; open the window to split tick and SwapBuffers",
            };
        }
        return new ProfilerVerdict
        {
            Category = "CPU-BOUND",
            Reason = $"{cpuTop} {cpuTopMs:F1}ms, CPU render {f.CpuRenderMs:F1}ms, GPU {f.GpuSpanMs:F1}ms / {f.FrameMs:F1}ms",
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

public sealed record ProfilerCensusSnapshot
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

/// <summary>
/// Session state a snapshot needs, captured on the render thread so the snapshot itself can be
/// built on a worker. The listener is thread-safe.
/// </summary>
public sealed record ProfilerSnapshotContext(
    bool FrameLimiterActive,
    bool GlCountersEnabled,
    bool IsRecording,
    bool RecordingTruncated,
    double RecordingElapsedSec,
    double RecordingTargetSec,
    string LastExportPath,
    string Instrumentation,
    OptimumRuntimeEventListener? Listener,
    IReadOnlyList<ProfilerMarkerStat> Markers);

public sealed class OptimumProfilerSnapshot
{
    public int TotalFrames { get; init; }
    public int GpuFrames { get; init; }
    public double WindowSeconds { get; init; }
    public double AvgFps { get; init; }
    public double Low1Fps { get; init; }
    public ProfilerDistribution FrameDist { get; init; } = new();
    public ProfilerDistribution GpuSpanDist { get; init; } = new();
    public double CpuRenderMeanMs { get; init; }
    public double OutsideRenderMeanMs { get; init; }
    /// <summary>
    /// Mean FrameProfiler waits (FPS limiter sleep, SwapBuffers) and the CPU work left between frames
    /// (game tick and input: time outside rendering minus both waits); null without the extended diagnostics.
    /// </summary>
    public double? SleepMeanMs { get; init; }
    public double? SwapMeanMs { get; init; }
    public double? TickMeanMs { get; init; }
    public double AllocMbPerSec { get; init; }
    public long Gen0Count { get; init; }
    public long Gen1Count { get; init; }
    public long Gen2Count { get; init; }
    public double GcPausePct { get; init; }
    public double LockContentionsPerSec { get; init; }
    public bool GlCountersEnabled { get; init; }
    public bool IsRecording { get; init; }
    public bool RecordingTruncated { get; init; }
    public double RecordingElapsedSec { get; init; }
    public double RecordingTargetSec { get; init; }
    public string LastExportPath { get; init; } = "";
    public string Instrumentation { get; init; } = "";
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
