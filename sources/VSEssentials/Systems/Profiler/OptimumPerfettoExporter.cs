#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Vintagestory.GameContent;

/// <summary>
/// Writes a self-contained profiling bundle: trace.perfetto.json.gz (Chrome Trace Event Format
/// for ui.perfetto.dev), summary.json (machine-readable) and report.md (for issues and PRs).
/// Runs on a worker thread from immutable inputs.
/// </summary>
public static class OptimumPerfettoExporter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly double TicksToUs = 1_000_000.0 / Stopwatch.Frequency;
    private static readonly string[] CpuSliceNames = ProfilerSegments.Labels.Select(l => "CPU:" + l).ToArray();
    private static readonly string[] GpuSliceNames = ProfilerSegments.Labels.Select(l => "GPU:" + l).ToArray();

    public static string ExportBundle(string outputDir, IReadOnlyList<ProfilerFrameSample> samples, OptimumProfilerSnapshot snapshot)
    {
        Directory.CreateDirectory(outputDir);
        WritePerfettoTraceGz(Path.Combine(outputDir, "trace.perfetto.json.gz"), samples);
        WriteSummaryJson(Path.Combine(outputDir, "summary.json"), snapshot);
        File.WriteAllText(Path.Combine(outputDir, "report.md"), BuildReportMarkdown(snapshot, outputDir), Utf8NoBom);
        return outputDir;
    }

    /// <summary>
    /// CPU slices sit at their measured offsets: each frame starts at the previous Done end, the
    /// Tick+Swap slice runs until Before starts, and the segments are contiguous after it. GPU
    /// slices are drawn from the same frame's Before start; the GPU clock is not synchronized
    /// with the CPU clock, so their absolute position is approximate and their durations exact.
    /// </summary>
    public static void WritePerfettoTraceGz(string tracePath, IReadOnlyList<ProfilerFrameSample> samples)
    {
        using var fs = new FileStream(tracePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var w = new Utf8JsonWriter(gz);

        w.WriteStartObject();
        w.WriteString("displayTimeUnit", "ms");
        w.WriteStartArray("traceEvents");
        WriteMetadata(w, 0, "process_name", "Optimum Client Profile");
        WriteMetadata(w, 1, "thread_name", "Render Thread (CPU)");
        WriteMetadata(w, 2, "thread_name", "GPU Pipeline (Timestamps)");
        WriteMetadata(w, 3, "thread_name", "CLR Runtime (GC & Locks)");

        long baseTicks = samples.Count > 0 ? samples[0].StartTicks : 0;
        for (int i = 0; i < samples.Count; i++)
        {
            ProfilerFrameSample f = samples[i];
            double frameUs = (f.StartTicks - baseTicks) * TicksToUs;
            WriteSlice(w, "Frame", "frame", frameUs, f.FrameMs, 1, f.FrameIndex);

            double renderUs = frameUs + f.OutsideRenderMs * 1000.0;
            WriteSlice(w, "CPU:Tick+Swap", "cpu", frameUs, f.OutsideRenderMs, 1, f.FrameIndex);
            double cursor = renderUs;
            for (int k = 0; k < ProfilerSegments.Count; k++)
            {
                WriteSlice(w, CpuSliceNames[k], "cpu", cursor, f.Cpu[k], 1, f.FrameIndex);
                cursor += f.Cpu[k] * 1000.0;
            }

            if (f.HasGpu)
            {
                cursor = renderUs;
                for (int k = 0; k < ProfilerSegments.Count; k++)
                {
                    WriteSlice(w, GpuSliceNames[k], "gpu", cursor, f.Gpu[k], 2, f.FrameIndex);
                    cursor += f.Gpu[k] * 1000.0;
                }
            }

            if (f.Gen0Delta + f.Gen1Delta + f.Gen2Delta > 0)
            {
                string gcName = f.Gen2Delta > 0 ? "GC Gen2" : f.Gen1Delta > 0 ? "GC Gen1" : "GC Gen0";
                WriteSlice(w, gcName, "gc", frameUs, Math.Max(0.05, f.GcPauseMs), 3, f.FrameIndex);
            }
            if (f.LockContentionDelta > 0)
            {
                w.WriteStartObject();
                w.WriteString("name", "LockContention");
                w.WriteString("ph", "i");
                w.WriteString("s", "t");
                w.WriteNumber("ts", Math.Round(frameUs, 1));
                w.WriteNumber("pid", 1);
                w.WriteNumber("tid", 3);
                w.WriteStartObject("args");
                w.WriteNumber("count", f.LockContentionDelta);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            WriteCounter(w, "FrameMs", frameUs, f.FrameMs);
            if (f.HasGpu) WriteCounter(w, "GpuSpanMs", frameUs, f.GpuSpanMs);
            if (f.FrameMs > 0.01) WriteCounter(w, "AllocMBs", frameUs, f.AllocBytes / 1048576.0 / (f.FrameMs / 1000.0));
            if (f.LoadedChunks > 0) WriteCounter(w, "LoadedChunks", frameUs, f.LoadedChunks);
            if (f.MeshPoolMb > 0) WriteCounter(w, "MeshPoolMB", frameUs, f.MeshPoolMb);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static void WriteSummaryJson(string summaryPath, OptimumProfilerSnapshot s)
    {
        var payload = new
        {
            exported_utc = DateTime.UtcNow.ToString("O"),
            instrumentation = s.Instrumentation,
            verdict = new { s.Verdict.Category, s.Verdict.Reason },
            gl = new { s.Census.GlVendor, s.Census.GlRenderer, s.Census.GlVersion },
            optimum_version = s.Census.OptimumVersion,
            frame = new
            {
                count = s.TotalFrames,
                gpu_count = s.GpuFrames,
                truncated = s.RecordingTruncated,
                window_seconds = Math.Round(s.WindowSeconds, 3),
                avg_fps = Math.Round(s.AvgFps, 2),
                low1_fps = Math.Round(s.Low1Fps, 2),
                mean_ms = Math.Round(s.FrameDist.Mean, 3),
                p50_ms = Math.Round(s.FrameDist.P50, 3),
                p95_ms = Math.Round(s.FrameDist.P95, 3),
                p99_ms = Math.Round(s.FrameDist.P99, 3),
                max_ms = Math.Round(s.FrameDist.Max, 3),
                cpu_render_ms = Math.Round(s.CpuRenderMeanMs, 3),
                outside_render_ms = Math.Round(s.OutsideRenderMeanMs, 3),
            },
            waits_ms = s.SwapMeanMs.HasValue
                ? new { sleep = Math.Round(s.SleepMeanMs!.Value, 3), swap = Math.Round(s.SwapMeanMs.Value, 3), tick_and_input = Math.Round(s.TickMeanMs!.Value, 3) }
                : null,
            gpu_span_ms = new
            {
                mean = Math.Round(s.GpuSpanDist.Mean, 3),
                p50 = Math.Round(s.GpuSpanDist.P50, 3),
                p95 = Math.Round(s.GpuSpanDist.P95, 3),
                p99 = Math.Round(s.GpuSpanDist.P99, 3),
                max = Math.Round(s.GpuSpanDist.Max, 3),
            },
            stages_cpu_ms = s.StageCpuMeansMs.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3)),
            stages_gpu_ms = s.StageGpuMeansMs.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3)),
            gl_counters = s.GlPrimitivesMean.Length == ProfilerGlCounters.Count
                ? ProfilerGlCounters.Labels.Select((label, i) => new { stage = label, primitives = s.GlPrimitivesMean[i], samples_passed = s.GlSamplesPassedMean[i] }).ToArray()
                : null,
            gc = new
            {
                alloc_mb_per_sec = Math.Round(s.AllocMbPerSec, 3),
                gen0 = s.Gen0Count,
                gen1 = s.Gen1Count,
                gen2 = s.Gen2Count,
                gc_pause_pct = Math.Round(s.GcPausePct, 3),
                lock_contentions_per_sec = Math.Round(s.LockContentionsPerSec, 2),
                event_listener = s.Runtime,
            },
            census = s.Census,
            frame_profiler_markers = s.FrameProfilerMarkers,
        };
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);
    }

    public static string BuildReportMarkdown(OptimumProfilerSnapshot s, string bundleDir)
    {
        var sb = new StringBuilder();
        void Line(FormattableString text) => sb.AppendLine(text.ToString(CultureInfo.InvariantCulture));

        Line($"# Optimum Integrated Profiler Report");
        Line($"");
        Line($"- Exported UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        Line($"- Bundle: `{Path.GetFileName(bundleDir)}`");
        Line($"- Optimum: `v{s.Census.OptimumVersion}` | GL: `{s.Census.GlRenderer}` (`{s.Census.GlVersion}`)");
        Line($"- Instrumentation: {s.Instrumentation}");
        Line($"- Bottleneck Verdict: **{s.Verdict.Category}** ({s.Verdict.Reason})");
        Line($"- Performance: **{s.AvgFps:F2} FPS** (1% Low **{s.Low1Fps:F2} FPS**), Frame `{s.FrameDist.Mean:F2} ms` (P50 `{s.FrameDist.P50:F2}`, P95 `{s.FrameDist.P95:F2}`, Max `{s.FrameDist.Max:F2}`), CPU render `{s.CpuRenderMeanMs:F2} ms`, GPU span `{s.GpuSpanDist.Mean:F2} ms` over `{s.TotalFrames}` frames (`{s.WindowSeconds:F1} s`)");
        if (s.SwapMeanMs.HasValue)
        {
            Line($"- Waits: FPS limiter `{s.SleepMeanMs:F2} ms`, SwapBuffers `{s.SwapMeanMs:F2} ms`, tick and input `{s.TickMeanMs:F2} ms`");
        }
        if (s.RecordingTruncated)
        {
            Line($"- Recording stopped early at the frame cap.");
        }
        Line($"- Memory & Locks: Alloc `{s.AllocMbPerSec:F1} MB/s`, Gen0/1/2 `{s.Gen0Count}/{s.Gen1Count}/{s.Gen2Count}`, GC pause `{s.GcPausePct:F2}%`, Locks `{s.LockContentionsPerSec:F1}/s` (all threads)");
        Line($"");
        Line($"## Pipeline Stages (Mean ms)");
        Line($"");
        Line($"| Stage | GPU ms | CPU ms |");
        Line($"| --- | ---: | ---: |");
        for (int i = 0; i < s.StageCpuMeansMs.Length; i++)
        {
            double gpu = i < s.StageGpuMeansMs.Length ? s.StageGpuMeansMs[i].Value : 0.0;
            Line($"| `{s.StageCpuMeansMs[i].Key}` | {gpu:F3} | {s.StageCpuMeansMs[i].Value:F3} |");
        }

        if (s.Runtime.TopTypes.Count > 0)
        {
            Line($"");
            Line($"## Top Sampled Allocation Types");
            Line($"");
            Line($"| Type | Sampled MB | Share | LOH MB |");
            Line($"| --- | ---: | ---: | ---: |");
            foreach (ProfilerAllocTypeEntry t in s.Runtime.TopTypes)
            {
                Line($"| `{t.TypeName}` | {t.Bytes / 1048576.0:F2} | {t.SharePct:F1}% | {t.LohBytes / 1048576.0:F2} |");
            }
        }

        ProfilerCensusSnapshot c = s.Census;
        Line($"");
        Line($"## World Census");
        Line($"");
        Line($"- Chunks: total `{c.ChunksTotal}`, empty `{c.ChunksEmpty}`, unpacked `{c.ChunksUnpacked}`, awaiting tess `{c.ChunksAwaitingTessellation}`");
        Line($"- Entities: loaded `{c.EntitiesLoaded}`, rendered `{c.EntitiesRendered}`, shadow `{c.EntitiesShadowRendered}`, active animators `{c.EntitiesActiveAnimators}`");
        Line($"- Shaders: `{c.ShaderCount}` (`{c.ShaderLoadErrors}` errors) | Opened GUIs: `{c.OpenedGuiCount}`");
        if (c.ShadowFarRenderersSummary.Length > 0)
        {
            Line($"- ShadowFar Renderers: `{c.ShadowFarRenderersSummary}`");
        }
        Line($"");
        Line($"Open `trace.perfetto.json.gz` in `https://ui.perfetto.dev` to inspect the full frame, GPU, and counter timeline.");
        return sb.ToString();
    }

    private static void WriteMetadata(Utf8JsonWriter w, int tid, string metaName, string label)
    {
        w.WriteStartObject();
        w.WriteString("ph", "M");
        w.WriteNumber("pid", 1);
        w.WriteNumber("tid", tid);
        w.WriteString("name", metaName);
        w.WriteStartObject("args");
        w.WriteString("name", label);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteSlice(Utf8JsonWriter w, string name, string cat, double tsUs, double ms, int tid, long frameIndex)
    {
        if (ms <= 0.001)
        {
            return;
        }
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("cat", cat);
        w.WriteString("ph", "X");
        w.WriteNumber("ts", Math.Round(tsUs, 1));
        w.WriteNumber("dur", Math.Round(ms * 1000.0, 1));
        w.WriteNumber("pid", 1);
        w.WriteNumber("tid", tid);
        w.WriteStartObject("args");
        w.WriteNumber("frame", frameIndex);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteCounter(Utf8JsonWriter w, string name, double tsUs, double value)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("ph", "C");
        w.WriteNumber("ts", Math.Round(tsUs, 1));
        w.WriteNumber("pid", 1);
        w.WriteStartObject("args");
        w.WriteNumber("value", Math.Round(value, 3));
        w.WriteEndObject();
        w.WriteEndObject();
    }
}
