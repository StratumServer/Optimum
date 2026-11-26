#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Vintagestory.GameContent;

/// <summary>
/// Writes a Spark-style self-contained profiling bundle containing:
/// 1) trace.perfetto.json.gz (Chrome Trace Event Format for ui.perfetto.dev)
/// 2) summary.json (machine-readable hardware, Optimum diagnostics, frame/GPU stats, censuses)
/// 3) report.md (human-readable summary for issues/PRs)
/// </summary>
public static class OptimumPerfettoExporter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static string ExportBundle(
        string outputDir,
        IReadOnlyList<ProfilerFrameSample> samples,
        OptimumProfilerSnapshot snapshot)
    {
        Directory.CreateDirectory(outputDir);

        string tracePath = Path.Combine(outputDir, "trace.perfetto.json.gz");
        string summaryPath = Path.Combine(outputDir, "summary.json");
        string reportPath = Path.Combine(outputDir, "report.md");

        WritePerfettoTraceGz(tracePath, samples);
        WriteSummaryJson(summaryPath, snapshot);
        WriteReportMarkdown(reportPath, snapshot, outputDir);

        return outputDir;
    }

    public static void WritePerfettoTraceGz(string tracePath, IReadOnlyList<ProfilerFrameSample> samples)
    {
        using var fs = new FileStream(tracePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var writer = new Utf8JsonWriter(gz, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("displayTimeUnit", "ms");
        writer.WriteStartArray("traceEvents");

        WriteMetadata(writer, 1, 0, "process_name", "Optimum Client Profile");
        WriteMetadata(writer, 1, 1, "thread_name", "Render Thread (CPU)");
        WriteMetadata(writer, 1, 2, "thread_name", "GPU Pipeline (Timestamps)");
        WriteMetadata(writer, 1, 3, "thread_name", "CLR Runtime (GC & Locks)");

        if (samples.Count > 0)
        {
            long baseUs = samples[0].StartUnixUs;
            for (int i = 0; i < samples.Count; i++)
            {
                ProfilerFrameSample f = samples[i];
                long frameTs = Math.Max(0, f.StartUnixUs - baseUs);

                // Frame slice on CPU thread
                long frameDurUs = (long)Math.Max(1.0, f.FrameMs * 1000.0);
                WriteCompleteEvent(writer, "Frame", "frame", frameTs, frameDurUs, 1, 1, f.FrameIndex);

                // CPU pass slices (sequential within frame)
                long cpuCursor = frameTs;
                WriteStageIfNonZero(writer, "CPU:Before", "cpu", ref cpuCursor, Get(f.CpuStagesMs, 0), 1, 1);
                WriteStageIfNonZero(writer, "CPU:ShadowFar", "cpu", ref cpuCursor, Get(f.CpuGapsMs, 0), 1, 1);
                WriteStageIfNonZero(writer, "CPU:ShadowNear", "cpu", ref cpuCursor, Get(f.CpuStagesMs, 1), 1, 1);
                WriteStageIfNonZero(writer, "CPU:Opaque", "cpu", ref cpuCursor, Get(f.CpuStagesMs, 2), 1, 1);
                WriteStageIfNonZero(writer, "CPU:OIT", "cpu", ref cpuCursor, Get(f.CpuStagesMs, 3) + Get(f.CpuGapsMs, 2), 1, 1);
                WriteStageIfNonZero(writer, "CPU:PostProc", "cpu", ref cpuCursor, Get(f.CpuGapsMs, 3), 1, 1);
                WriteStageIfNonZero(writer, "CPU:FinalComp", "cpu", ref cpuCursor, Get(f.CpuGapsMs, 4), 1, 1);
                WriteStageIfNonZero(writer, "CPU:Blit", "cpu", ref cpuCursor, Get(f.CpuGapsMs, 5), 1, 1);
                WriteStageIfNonZero(writer, "CPU:Ortho", "cpu", ref cpuCursor, Get(f.CpuStagesMs, 8), 1, 1);

                // GPU pass slices (sequential on GPU track to keep timeline readable in Perfetto)
                long gpuCursor = frameTs;
                WriteStageIfNonZero(writer, "GPU:Before", "gpu", ref gpuCursor, Get(f.GpuStagesMs, 0), 1, 2);
                WriteStageIfNonZero(writer, "GPU:ShadowFar", "gpu", ref gpuCursor, Get(f.GpuGapsMs, 0), 1, 2);
                WriteStageIfNonZero(writer, "GPU:ShadowNear", "gpu", ref gpuCursor, Get(f.GpuStagesMs, 1), 1, 2);
                WriteStageIfNonZero(writer, "GPU:Opaque", "gpu", ref gpuCursor, Get(f.GpuStagesMs, 2), 1, 2);
                WriteStageIfNonZero(writer, "GPU:OIT", "gpu", ref gpuCursor, Get(f.GpuStagesMs, 3) + Get(f.GpuGapsMs, 2), 1, 2);
                WriteStageIfNonZero(writer, "GPU:PostProc", "gpu", ref gpuCursor, Get(f.GpuGapsMs, 3), 1, 2);
                WriteStageIfNonZero(writer, "GPU:FinalComp", "gpu", ref gpuCursor, Get(f.GpuGapsMs, 4), 1, 2);
                WriteStageIfNonZero(writer, "GPU:Blit", "gpu", ref gpuCursor, Get(f.GpuGapsMs, 5), 1, 2);
                WriteStageIfNonZero(writer, "GPU:Ortho", "gpu", ref gpuCursor, Get(f.GpuStagesMs, 8), 1, 2);

                if (f.Gen0Delta + f.Gen1Delta + f.Gen2Delta > 0 || f.GcPauseMs > 0.01)
                {
                    string gcName = f.Gen2Delta > 0 ? "GC Gen2" : f.Gen1Delta > 0 ? "GC Gen1" : "GC Gen0";
                    long durUs = (long)Math.Max(50.0, f.GcPauseMs * 1000.0);
                    WriteCompleteEvent(writer, gcName, "gc", frameTs, durUs, 1, 3, f.FrameIndex);
                }

                if (f.LockContentionDelta > 0)
                {
                    WriteInstantEvent(writer, "LockContention", "lock", frameTs, 1, 3, f.LockContentionDelta);
                }

                double fps = f.FrameMs > 0.01 ? 1000.0 / f.FrameMs : 0.0;
                double allocMbSec = f.FrameMs > 0.01 ? (f.AllocBytes / (1024.0 * 1024.0)) / (f.FrameMs / 1000.0) : 0.0;
                WriteCounterEvent(writer, "FPS", frameTs, 1, fps);
                WriteCounterEvent(writer, "FrameMs", frameTs, 1, f.FrameMs);
                WriteCounterEvent(writer, "GpuSpanMs", frameTs, 1, f.GpuSpanMs);
                WriteCounterEvent(writer, "AllocMBs", frameTs, 1, allocMbSec);
                if (f.LoadedChunks > 0) WriteCounterEvent(writer, "LoadedChunks", frameTs, 1, f.LoadedChunks);
                if (f.MeshPoolMb > 0) WriteCounterEvent(writer, "MeshPoolMB", frameTs, 1, f.MeshPoolMb);
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    public static void WriteSummaryJson(string summaryPath, OptimumProfilerSnapshot s)
    {
        var payload = new
        {
            exported_utc = DateTime.UtcNow.ToString("O"),
            verdict = new { s.Verdict.Category, s.Verdict.Reason },
            gl = new { s.Census.GlVendor, s.Census.GlRenderer, s.Census.GlVersion },
            optimum_version = s.Census.OptimumVersion,
            frame = new
            {
                count = s.TotalFrames,
                window_seconds = Math.Round(s.WindowSeconds, 3),
                avg_fps = Math.Round(s.AvgFps, 2),
                low1_fps = Math.Round(s.Low1Fps, 2),
                mean_ms = Math.Round(s.FrameDist.Mean, 3),
                p50_ms = Math.Round(s.FrameDist.P50, 3),
                p95_ms = Math.Round(s.FrameDist.P95, 3),
                p99_ms = Math.Round(s.FrameDist.P99, 3),
                max_ms = Math.Round(s.FrameDist.Max, 3),
            },
            gpu_span_ms = new
            {
                mean = Math.Round(s.GpuSpanDist.Mean, 3),
                p50 = Math.Round(s.GpuSpanDist.P50, 3),
                p95 = Math.Round(s.GpuSpanDist.P95, 3),
                p99 = Math.Round(s.GpuSpanDist.P99, 3),
                max = Math.Round(s.GpuSpanDist.Max, 3),
            },
            stages_cpu_ms = s.StageCpuMeansMs,
            stages_gpu_ms = s.StageGpuMeansMs,
            gl_counters = s.GlCountersEnabled ? new { primitives_mean = s.GlPrimitivesMean, samples_passed_mean = s.GlSamplesPassedMean } : null,
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

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(payload, opts), Utf8NoBom);
    }

    public static void WriteReportMarkdown(string reportPath, OptimumProfilerSnapshot s, string bundleDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Optimum Integrated Profiler Report");
        sb.AppendLine();
        sb.AppendLine($"- Exported UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- Bundle: `{Path.GetFileName(bundleDir)}`");
        sb.AppendLine($"- Optimum: `v{s.Census.OptimumVersion}` | GL: `{s.Census.GlRenderer}` (`{s.Census.GlVersion}`)");
        sb.AppendLine($"- Bottleneck Verdict: **{s.Verdict.Category}** ({s.Verdict.Reason})");
        sb.AppendLine($"- Performance: **{s.AvgFps:F2} FPS** (1% Low **{s.Low1Fps:F2} FPS**), Frame `{s.FrameDist.Mean:F2} ms` (P50 `{s.FrameDist.P50:F2}`, P95 `{s.FrameDist.P95:F2}`, Max `{s.FrameDist.Max:F2}`), GPU Span `{s.GpuSpanDist.Mean:F2} ms` over `{s.TotalFrames}` frames (`{s.WindowSeconds:F1} s`)");
        sb.AppendLine($"- Memory & Locks: Alloc `{s.AllocMbPerSec:F1} MB/s`, Gen0/1/2 `{s.Gen0Count}/{s.Gen1Count}/{s.Gen2Count}`, GC pause `{s.GcPausePct:F2}%`, Locks `{s.LockContentionsPerSec:F1}/s`");
        sb.AppendLine();
        sb.AppendLine("## Pipeline Stages (Mean ms)");
        sb.AppendLine();
        sb.AppendLine("| Stage | GPU ms | CPU ms |");
        sb.AppendLine("| --- | ---: | ---: |");
        for (int i = 0; i < s.StageGpuMeansMs.Length; i++)
        {
            string name = s.StageGpuMeansMs[i].Key;
            double gpu = s.StageGpuMeansMs[i].Value;
            double cpu = i < s.StageCpuMeansMs.Length ? s.StageCpuMeansMs[i].Value : 0.0;
            sb.AppendLine($"| `{name}` | {gpu:F3} | {cpu:F3} |");
        }

        if (s.Runtime.TopTypes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Top Sampled Allocation Types");
            sb.AppendLine();
            sb.AppendLine("| Type | Sampled MB | Share | LOH MB |");
            sb.AppendLine("| --- | ---: | ---: | ---: |");
            foreach (ProfilerAllocTypeEntry t in s.Runtime.TopTypes)
            {
                sb.AppendLine($"| `{t.TypeName}` | {t.Bytes / 1048576.0:F2} | {t.SharePct:F1}% | {t.LohBytes / 1048576.0:F2} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## World Census");
        sb.AppendLine();
        sb.AppendLine($"- Chunks: total `{s.Census.ChunksTotal}`, empty `{s.Census.ChunksEmpty}`, unpacked `{s.Census.ChunksUnpacked}`, awaiting tess `{s.Census.ChunksAwaitingTessellation}`");
        sb.AppendLine($"- Entities: loaded `{s.Census.EntitiesLoaded}`, rendered `{s.Census.EntitiesRendered}`, shadow `{s.Census.EntitiesShadowRendered}`, active animators `{s.Census.EntitiesActiveAnimators}`");
        sb.AppendLine($"- Shaders: `{s.Census.ShaderCount}` (`{s.Census.ShaderLoadErrors}` errors) | Opened GUIs: `{s.Census.OpenedGuiCount}`");
        if (!string.IsNullOrEmpty(s.Census.ShadowFarRenderersSummary))
        {
            sb.AppendLine($"- ShadowFar Renderers: `{s.Census.ShadowFarRenderersSummary}`");
        }

        sb.AppendLine();
        sb.AppendLine("Open `trace.perfetto.json.gz` in `https://ui.perfetto.dev` to inspect the full frame, GPU, and counter timeline.");
        File.WriteAllText(reportPath, sb.ToString(), Utf8NoBom);
    }

    private static double Get(double[] arr, int idx) => idx >= 0 && idx < arr.Length ? arr[idx] : 0.0;

    private static void WriteMetadata(Utf8JsonWriter w, int pid, int tid, string metaName, string label)
    {
        w.WriteStartObject();
        w.WriteString("ph", "M");
        w.WriteNumber("pid", pid);
        w.WriteNumber("tid", tid);
        w.WriteString("name", metaName);
        w.WriteStartObject("args");
        w.WriteString("name", label);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteStageIfNonZero(Utf8JsonWriter w, string name, string cat, ref long cursorUs, double ms, int pid, int tid)
    {
        if (ms <= 0.001)
        {
            return;
        }
        long durUs = (long)Math.Max(1.0, ms * 1000.0);
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("cat", cat);
        w.WriteString("ph", "X");
        w.WriteNumber("ts", cursorUs);
        w.WriteNumber("dur", durUs);
        w.WriteNumber("pid", pid);
        w.WriteNumber("tid", tid);
        w.WriteEndObject();
        cursorUs += durUs;
    }

    private static void WriteCompleteEvent(Utf8JsonWriter w, string name, string cat, long ts, long dur, int pid, int tid, long frameIdx)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("cat", cat);
        w.WriteString("ph", "X");
        w.WriteNumber("ts", ts);
        w.WriteNumber("dur", dur);
        w.WriteNumber("pid", pid);
        w.WriteNumber("tid", tid);
        w.WriteStartObject("args");
        w.WriteNumber("frame", frameIdx);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteInstantEvent(Utf8JsonWriter w, string name, string cat, long ts, int pid, int tid, long count)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("cat", cat);
        w.WriteString("ph", "i");
        w.WriteString("s", "t");
        w.WriteNumber("ts", ts);
        w.WriteNumber("pid", pid);
        w.WriteNumber("tid", tid);
        w.WriteStartObject("args");
        w.WriteNumber("count", count);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteCounterEvent(Utf8JsonWriter w, string name, long ts, int pid, double value)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("ph", "C");
        w.WriteNumber("ts", ts);
        w.WriteNumber("pid", pid);
        w.WriteStartObject("args");
        w.WriteNumber("value", Math.Round(value, 3));
        w.WriteEndObject();
        w.WriteEndObject();
    }
}
