#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Vintagestory.GameContent;

/// <summary>
/// Manages active profiler instrumentation lifecycle. Remains completely unallocated and
/// unregistered until UI, HUD, or explicit recording is activated, and tears down all
/// probes, GL queries, EventListeners, and temporary flags as soon as no consumer remains.
/// </summary>
public sealed class OptimumProfilerSession : IDisposable
{
    private const int RollingCapacity = 240;
    private const int StageCount = OptimumGpuTimerRing.StageCount;
    private const int GapCount = 6;
    private const int GlCounterCount = OptimumGpuTimerRing.GlCounterCount;

    private sealed class StageProbe : IRenderer
    {
        private readonly OptimumProfilerSession session;
        private readonly ProfilerStageId stage;
        private readonly bool isStart;
        private readonly double renderOrder;
        private readonly int glCounterIdx;

        public StageProbe(OptimumProfilerSession session, ProfilerStageId stage, bool isStart, double renderOrder, int glCounterIdx = -1)
        {
            this.session = session;
            this.stage = stage;
            this.isStart = isStart;
            this.renderOrder = renderOrder;
            this.glCounterIdx = glCounterIdx;
        }

        public double RenderOrder => renderOrder;
        public int RenderRange => int.MaxValue;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stageEnum)
        {
            session.OnStageProbe(stage, isStart, glCounterIdx);
        }

        public void Dispose()
        {
        }
    }

    private sealed class MarkerAgg
    {
        public long TotalTicks;
        public long MaxTicks;
        public int Calls;
    }

    private readonly ICoreClientAPI capi;
    private readonly List<(IRenderer Renderer, EnumRenderStage Stage)> registeredProbes = new();
    private readonly ProfilerFrameSample[] rollingSamples = new ProfilerFrameSample[RollingCapacity];
    private readonly Dictionary<string, MarkerAgg> markerAggs = new(StringComparer.Ordinal);

    private readonly long[] curCpuStart = new long[StageCount];
    private readonly long[] curCpuEnd = new long[StageCount];
    private int curVisitedMask;

    private readonly double[] tmpGpuStages = new double[StageCount];
    private readonly double[] tmpGpuGaps = new double[GapCount];
    private readonly long[] tmpPrimitives = new long[GlCounterCount];
    private readonly long[] tmpSamplesPassed = new long[GlCounterCount];

    private OptimumGpuTimerRing? gpuRing;
    private OptimumRuntimeEventListener? runtimeListener;
    private OptimumWorldCensus? censusCollector;
    private ProfilerCensusSnapshot cachedCensus = new();

    private readonly List<ProfilerFrameSample> recordingSamples = new();
    private bool recordingActive;
    private long recordingStartTicks;
    private double recordingTargetSec;
    private string? recordingOutDirOverride;
    private Action<string>? recordingCompleteCallback;

    private bool uiOpen;
    private bool hudOpen;
    private bool running;
    private bool disposed;

    private long frameIndex;
    private int rollingWriteIdx;
    private int rollingCount;
    private long lastCensusTicks;

    private long prevFrameStartTicks;
    private long prevAllocBytes;
    private int prevGen0;
    private int prevGen1;
    private int prevGen2;
    private long prevGcPauseTicks;
    private long prevLockCount;

    private bool savedFrameProfilerEnabled;
    private bool hasSavedFrameProfiler;
    private readonly List<(object? Target, MemberInfo Member, bool PrevValue)> savedOptFlags = new();

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
    private static readonly double TicksToUs = 1_000_000.0 / Stopwatch.Frequency;

    public string LastExportPath { get; private set; } = "";
    public bool IsRunning => running;
    public bool IsUiOpen => uiOpen;
    public bool IsHudOpen => hudOpen;
    public bool IsRecording => recordingActive;
    public bool GlCountersEnabled => gpuRing?.GlCountersEnabled ?? false;

    public OptimumProfilerSession(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void SetUiOpen(bool open)
    {
        uiOpen = open;
        SyncLifecycle();
    }

    public void SetHudOpen(bool open)
    {
        hudOpen = open;
        SyncLifecycle();
    }

    public void SetGlCountersEnabled(bool enabled)
    {
        if (gpuRing != null)
        {
            gpuRing.GlCountersEnabled = enabled;
        }
    }

    public void StartRecording(int seconds, string? outDirOverride = null, Action<string>? onComplete = null)
    {
        recordingTargetSec = Math.Clamp(seconds, 1, 120);
        recordingOutDirOverride = outDirOverride;
        recordingCompleteCallback = onComplete;
        recordingSamples.Clear();
        recordingStartTicks = Stopwatch.GetTimestamp();
        recordingActive = true;
        SyncLifecycle();
    }

    public void StopAll()
    {
        uiOpen = false;
        hudOpen = false;
        recordingActive = false;
        recordingSamples.Clear();
        SyncLifecycle();
    }

    public void ResetCounters()
    {
        Array.Clear(rollingSamples, 0, rollingSamples.Length);
        rollingWriteIdx = 0;
        rollingCount = 0;
        frameIndex = 0;
        markerAggs.Clear();
        runtimeListener?.Reset();
        recordingSamples.Clear();

        try
        {
            Type? diagType = typeof(ICoreClientAPI).Assembly.GetType("Vintagestory.API.Config.OptimumDiagnostics");
            diagType?.GetMethod("ResetAllCounters", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }
        catch
        {
        }
    }

    public string ExportSnapshotNow(string? outDirOverride = null)
    {
        bool wasRunning = running;
        if (!wasRunning)
        {
            StartCore();
        }

        OptimumProfilerSnapshot snap = BuildSnapshot();
        var list = GetRollingSamplesInOrder();
        string targetDir = outDirOverride ?? Path.Combine(GamePaths.Logs, "OptimumProfiler", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        string exported = OptimumPerfettoExporter.ExportBundle(targetDir, list, snap);
        LastExportPath = exported;

        if (!wasRunning)
        {
            StopCore();
        }

        return exported;
    }

    private void SyncLifecycle()
    {
        bool needActive = uiOpen || hudOpen || recordingActive;
        if (needActive && !running && !disposed)
        {
            StartCore();
        }
        else if (!needActive && running)
        {
            StopCore();
        }
    }

    private void StartCore()
    {
        if (running || disposed)
        {
            return;
        }

        gpuRing = new OptimumGpuTimerRing();
        runtimeListener = new OptimumRuntimeEventListener();
        censusCollector = new OptimumWorldCensus(capi);

        prevFrameStartTicks = 0;
        prevAllocBytes = GC.GetTotalAllocatedBytes(false);
        prevGen0 = GC.CollectionCount(0);
        prevGen1 = GC.CollectionCount(1);
        prevGen2 = GC.CollectionCount(2);
        prevGcPauseTicks = GC.GetTotalPauseDuration().Ticks;
        prevLockCount = Monitor.LockContentionCount;
        lastCensusTicks = 0;

        EnableOptimumDiagnosticsTemporarily();
        RegisterStageProbes();
        running = true;
    }

    private void StopCore()
    {
        if (!running)
        {
            return;
        }
        running = false;

        foreach (var (renderer, stage) in registeredProbes)
        {
            try { capi.Event.UnregisterRenderer(renderer, stage); } catch { }
        }
        registeredProbes.Clear();

        if (gpuRing != null)
        {
            try { gpuRing.Dispose(); } catch { }
            gpuRing = null;
        }

        if (runtimeListener != null)
        {
            try { runtimeListener.Dispose(); } catch { }
            runtimeListener = null;
        }

        RestoreOptimumDiagnostics();
        markerAggs.Clear();
    }

    private void RegisterStageProbes()
    {
        // Never register on EnumRenderStage.ShadowFar so OptimumShadowFarReuse stays unperturbed.
        AddStage(EnumRenderStage.Before, ProfilerStageId.Before);
        AddStage(EnumRenderStage.ShadowNear, ProfilerStageId.ShadowNear, glCounterIdx: (int)GlCounterStageId.ShadowNear);
        AddStage(EnumRenderStage.Opaque, ProfilerStageId.Opaque, glCounterIdx: (int)GlCounterStageId.Opaque);
        AddStage(EnumRenderStage.OIT, ProfilerStageId.OIT, glCounterIdx: (int)GlCounterStageId.OIT);
        AddStage(EnumRenderStage.AfterOIT, ProfilerStageId.AfterOIT);
        AddStage(EnumRenderStage.AfterPostProcessing, ProfilerStageId.AfterPostProcessing);
        AddStage(EnumRenderStage.AfterFinalComposition, ProfilerStageId.AfterFinalComposition);
        AddStage(EnumRenderStage.AfterBlit, ProfilerStageId.AfterBlit);
        AddStage(EnumRenderStage.Ortho, ProfilerStageId.Ortho, glCounterIdx: (int)GlCounterStageId.Ortho);
        AddStage(EnumRenderStage.Done, ProfilerStageId.Done);
    }

    private void AddStage(EnumRenderStage vsStage, ProfilerStageId id, int glCounterIdx = -1)
    {
        var start = new StageProbe(this, id, true, double.NegativeInfinity, glCounterIdx);
        var end = new StageProbe(this, id, false, double.PositiveInfinity, glCounterIdx);
        capi.Event.RegisterRenderer(start, vsStage, $"optimum-prof-start-{id}");
        capi.Event.RegisterRenderer(end, vsStage, $"optimum-prof-end-{id}");
        registeredProbes.Add((start, vsStage));
        registeredProbes.Add((end, vsStage));
    }

    private void OnStageProbe(ProfilerStageId stage, bool isStart, int glCounterIdx)
    {
        if (!running || disposed)
        {
            return;
        }

        int s = (int)stage;
        if (stage == ProfilerStageId.Before && isStart)
        {
            OnFrameStart();
        }

        if (isStart)
        {
            curVisitedMask |= 1 << s;
            curCpuStart[s] = Stopwatch.GetTimestamp();
            if (glCounterIdx >= 0) gpuRing?.BracketGlCounter((GlCounterStageId)glCounterIdx, true);
            gpuRing?.MarkStage(stage, true);
        }
        else
        {
            gpuRing?.MarkStage(stage, false);
            if (glCounterIdx >= 0) gpuRing?.BracketGlCounter((GlCounterStageId)glCounterIdx, false);
            curCpuEnd[s] = Stopwatch.GetTimestamp();
            if (stage == ProfilerStageId.Done)
            {
                OnFrameEnd();
            }
        }
    }

    private void OnFrameStart()
    {
        curVisitedMask = 0;
        gpuRing?.BeginFrame();

        if (capi.World?.FrameProfiler?.PrevRootEntry != null)
        {
            AccumulateFrameProfiler(capi.World.FrameProfiler.PrevRootEntry, capi.World.FrameProfiler.PrevRootEntry.Code ?? "all");
        }
    }

    private void OnFrameEnd()
    {
        long nowTicks = Stopwatch.GetTimestamp();
        if (prevFrameStartTicks == 0)
        {
            prevFrameStartTicks = nowTicks;
            prevAllocBytes = GC.GetTotalAllocatedBytes(false);
            prevGen0 = GC.CollectionCount(0);
            prevGen1 = GC.CollectionCount(1);
            prevGen2 = GC.CollectionCount(2);
            prevGcPauseTicks = GC.GetTotalPauseDuration().Ticks;
            prevLockCount = Monitor.LockContentionCount;
            return;
        }

        double frameMs = (nowTicks - prevFrameStartTicks) * TicksToMs;
        prevFrameStartTicks = nowTicks;

        double[] cpuStages = new double[StageCount];
        for (int i = 0; i < StageCount; i++)
        {
            if ((curVisitedMask & (1 << i)) != 0)
            {
                cpuStages[i] = Math.Max(0.0, (curCpuEnd[i] - curCpuStart[i]) * TicksToMs);
            }
        }

        double[] cpuGaps = new double[GapCount];
        ComputeCpuGap(0, 1, cpuGaps, 0);
        ComputeCpuGap(2, 3, cpuGaps, 1);
        ComputeCpuGap(3, 4, cpuGaps, 2);
        ComputeCpuGap(4, 5, cpuGaps, 3);
        ComputeCpuGap(5, 6, cpuGaps, 4);
        ComputeCpuGap(6, 7, cpuGaps, 5);

        double gpuSpan = 0.0;
        if (gpuRing != null)
        {
            gpuRing.EndFrameAndHarvest(tmpGpuStages, tmpGpuGaps, out gpuSpan, tmpPrimitives, tmpSamplesPassed);
        }

        long curAlloc = GC.GetTotalAllocatedBytes(false);
        int g0 = GC.CollectionCount(0);
        int g1 = GC.CollectionCount(1);
        int g2 = GC.CollectionCount(2);
        long gcPauseTicks = GC.GetTotalPauseDuration().Ticks;
        long locks = Monitor.LockContentionCount;

        long allocDelta = Math.Max(0, curAlloc - prevAllocBytes);
        int d0 = Math.Max(0, g0 - prevGen0);
        int d1 = Math.Max(0, g1 - prevGen1);
        int d2 = Math.Max(0, g2 - prevGen2);
        double pauseMs = Math.Max(0, gcPauseTicks - prevGcPauseTicks) / (double)TimeSpan.TicksPerMillisecond;
        long lockDelta = Math.Max(0, locks - prevLockCount);

        prevAllocBytes = curAlloc;
        prevGen0 = g0;
        prevGen1 = g1;
        prevGen2 = g2;
        prevGcPauseTicks = gcPauseTicks;
        prevLockCount = locks;

        if (lastCensusTicks == 0 || (nowTicks - lastCensusTicks) * TicksToMs >= 1000.0)
        {
            lastCensusTicks = nowTicks;
            if (censusCollector != null)
            {
                cachedCensus = censusCollector.Collect(
                    gpuRing?.GlVendor ?? "",
                    gpuRing?.GlRenderer ?? "",
                    gpuRing?.GlVersion ?? "");
            }
        }

        double meshPoolMb = 0.0;
        foreach (ProfilerMeshPoolPassStat pool in cachedCensus.MeshPools)
        {
            meshPoolMb += (pool.UsedVertices * 44.0) / (1024.0 * 1024.0);
        }

        var sample = new ProfilerFrameSample
        {
            FrameIndex = ++frameIndex,
            StartUnixUs = (long)(nowTicks * TicksToUs),
            FrameMs = frameMs,
            CpuStagesMs = cpuStages,
            GpuStagesMs = (double[])tmpGpuStages.Clone(),
            CpuGapsMs = cpuGaps,
            GpuGapsMs = (double[])tmpGpuGaps.Clone(),
            GpuSpanMs = gpuSpan,
            AllocBytes = allocDelta,
            Gen0Delta = d0,
            Gen1Delta = d1,
            Gen2Delta = d2,
            GcPauseMs = pauseMs,
            LockContentionDelta = lockDelta,
            Primitives = GlCountersEnabled ? (long[])tmpPrimitives.Clone() : Array.Empty<long>(),
            SamplesPassed = GlCountersEnabled ? (long[])tmpSamplesPassed.Clone() : Array.Empty<long>(),
            LoadedChunks = cachedCensus.ChunksTotal,
            MeshPoolMb = meshPoolMb,
        };

        rollingSamples[rollingWriteIdx] = sample;
        rollingWriteIdx = (rollingWriteIdx + 1) % RollingCapacity;
        if (rollingCount < RollingCapacity)
        {
            rollingCount++;
        }

        if (recordingActive)
        {
            recordingSamples.Add(sample);
            double elapsedSec = (nowTicks - recordingStartTicks) * TicksToMs / 1000.0;
            if (elapsedSec >= recordingTargetSec || recordingSamples.Count >= 18000)
            {
                FinishRecording();
            }
        }
    }

    private void FinishRecording()
    {
        recordingActive = false;
        var captured = new List<ProfilerFrameSample>(recordingSamples);
        recordingSamples.Clear();

        OptimumProfilerSnapshot snap = BuildSnapshotFromSamples(captured);
        string targetDir = recordingOutDirOverride ?? Path.Combine(GamePaths.Logs, "OptimumProfiler", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        string exported = OptimumPerfettoExporter.ExportBundle(targetDir, captured, snap);
        LastExportPath = exported;

        recordingCompleteCallback?.Invoke(exported);
        recordingCompleteCallback = null;
        recordingOutDirOverride = null;

        SyncLifecycle();
    }

    private void ComputeCpuGap(int a, int b, double[] cpuGaps, int outIdx)
    {
        if ((curVisitedMask & (1 << a)) != 0 && (curVisitedMask & (1 << b)) != 0)
        {
            cpuGaps[outIdx] = Math.Max(0.0, (curCpuStart[b] - curCpuEnd[a]) * TicksToMs);
        }
    }

    private List<ProfilerFrameSample> GetRollingSamplesInOrder()
    {
        var list = new List<ProfilerFrameSample>(rollingCount);
        if (rollingCount == 0)
        {
            return list;
        }
        int start = rollingCount == RollingCapacity ? rollingWriteIdx : 0;
        for (int i = 0; i < rollingCount; i++)
        {
            list.Add(rollingSamples[(start + i) % RollingCapacity]);
        }
        return list;
    }

    public OptimumProfilerSnapshot BuildSnapshot()
    {
        return BuildSnapshotFromSamples(GetRollingSamplesInOrder());
    }

    private OptimumProfilerSnapshot BuildSnapshotFromSamples(IReadOnlyList<ProfilerFrameSample> list)
    {
        if (censusCollector != null && string.IsNullOrEmpty(cachedCensus.OptimumVersion))
        {
            cachedCensus = censusCollector.Collect(gpuRing?.GlVendor ?? "", gpuRing?.GlRenderer ?? "", gpuRing?.GlVersion ?? "");
        }

        int n = list.Count;
        double[] frameMs = new double[n];
        double[] gpuMs = new double[n];
        double totalMs = 0;
        long totalAlloc = 0;
        long g0 = 0, g1 = 0, g2 = 0, locks = 0;
        double pauseTotal = 0;

        string[] stageLabels = { "Before", "ShadowFar", "ShadowNear", "Opaque", "OIT", "PostProc", "FinalComp", "Blit/FSR", "Ortho" };
        double[] cpuSums = new double[stageLabels.Length];
        double[] gpuSums = new double[stageLabels.Length];
        long[] primSums = new long[GlCounterCount];
        long[] sampSums = new long[GlCounterCount];
        int gpuValidCount = 0;

        for (int i = 0; i < n; i++)
        {
            ProfilerFrameSample s = list[i];
            frameMs[i] = s.FrameMs;
            gpuMs[i] = s.GpuSpanMs;
            if (s.GpuSpanMs > 0.001) gpuValidCount++;
            totalMs += s.FrameMs;
            totalAlloc += s.AllocBytes;
            g0 += s.Gen0Delta;
            g1 += s.Gen1Delta;
            g2 += s.Gen2Delta;
            pauseTotal += s.GcPauseMs;
            locks += s.LockContentionDelta;

            AddStageSums(cpuSums, s.CpuStagesMs, s.CpuGapsMs);
            AddStageSums(gpuSums, s.GpuStagesMs, s.GpuGapsMs);

            if (s.Primitives.Length == GlCounterCount)
            {
                for (int c = 0; c < GlCounterCount; c++)
                {
                    primSums[c] += s.Primitives[c];
                    sampSums[c] += s.SamplesPassed[c];
                }
            }
        }

        double windowSec = totalMs / 1000.0;
        ProfilerDistribution fDist = ProfilerDistribution.Compute(frameMs);
        ProfilerDistribution gDist = ProfilerDistribution.Compute(gpuMs);
        double avgFps = fDist.Mean > 0.001 ? 1000.0 / fDist.Mean : 0.0;
        double low1Fps = ProfilerDistribution.ComputeOnePercentLowFps(frameMs);
        double allocMbSec = windowSec > 0.001 ? (totalAlloc / 1048576.0) / windowSec : 0.0;
        double gcPct = totalMs > 0.001 ? pauseTotal * 100.0 / totalMs : 0.0;
        double locksSec = windowSec > 0.001 ? locks / windowSec : 0.0;

        var cpuMeans = new KeyValuePair<string, double>[stageLabels.Length];
        var gpuMeans = new KeyValuePair<string, double>[stageLabels.Length];
        string domGpuName = "";
        double domGpuMs = 0.0;
        int gpuDiv = Math.Max(1, gpuValidCount);
        int cpuDiv = Math.Max(1, n);
        for (int i = 0; i < stageLabels.Length; i++)
        {
            cpuMeans[i] = new KeyValuePair<string, double>(stageLabels[i], cpuSums[i] / cpuDiv);
            double gMean = gpuSums[i] / gpuDiv;
            gpuMeans[i] = new KeyValuePair<string, double>(stageLabels[i], gMean);
            if (gMean > domGpuMs)
            {
                domGpuMs = gMean;
                domGpuName = stageLabels[i];
            }
        }

        long[] primMean = new long[GlCounterCount];
        long[] sampMean = new long[GlCounterCount];
        if (n > 0)
        {
            for (int c = 0; c < GlCounterCount; c++)
            {
                primMean[c] = primSums[c] / n;
                sampMean[c] = sampSums[c] / n;
            }
        }

        double recElapsed = recordingActive ? Math.Max(0.0, (Stopwatch.GetTimestamp() - recordingStartTicks) * TicksToMs / 1000.0) : 0.0;
        ProfilerVerdict verdict = ProfilerVerdict.ClassifyBottleneck(fDist.Mean, gDist.Mean, gcPct, locksSec, domGpuName, domGpuMs);

        var markers = markerAggs.Values
            .OrderByDescending(m => m.TotalTicks)
            .Take(15)
            .Select(m => new ProfilerMarkerStat
            {
                Path = markerAggs.First(kv => ReferenceEquals(kv.Value, m)).Key,
                TotalMs = m.TotalTicks * TicksToMs,
                MaxMs = m.MaxTicks * TicksToMs,
                Calls = m.Calls,
            })
            .ToList();

        return new OptimumProfilerSnapshot
        {
            TotalFrames = n,
            WindowSeconds = windowSec,
            AvgFps = avgFps,
            Low1Fps = low1Fps,
            FrameDist = fDist,
            GpuSpanDist = gDist,
            AllocMbPerSec = allocMbSec,
            Gen0Count = g0,
            Gen1Count = g1,
            Gen2Count = g2,
            GcPausePct = gcPct,
            LockContentionsPerSec = locksSec,
            GlCountersEnabled = GlCountersEnabled,
            IsRecording = recordingActive,
            RecordingElapsedSec = recElapsed,
            RecordingTargetSec = recordingTargetSec,
            LastExportPath = LastExportPath,
            RecentFrameMs = frameMs,
            StageCpuMeansMs = cpuMeans,
            StageGpuMeansMs = gpuMeans,
            GlPrimitivesMean = primMean,
            GlSamplesPassedMean = sampMean,
            Verdict = verdict,
            Runtime = runtimeListener?.GetSnapshot(16) ?? new ProfilerRuntimeSnapshot(),
            Census = cachedCensus,
            FrameProfilerMarkers = markers,
        };
    }

    private static void AddStageSums(double[] sums, double[] stages, double[] gaps)
    {
        if (stages.Length < 9 || gaps.Length < 6) return;
        sums[0] += stages[0];
        sums[1] += gaps[0];
        sums[2] += stages[1];
        sums[3] += stages[2];
        sums[4] += stages[3] + gaps[2];
        sums[5] += gaps[3];
        sums[6] += gaps[4];
        sums[7] += gaps[5];
        sums[8] += stages[8];
    }

    private void AccumulateFrameProfiler(ProfileEntryRange node, string path)
    {
        if (markerAggs.Count < 512 || markerAggs.ContainsKey(path))
        {
            if (!markerAggs.TryGetValue(path, out MarkerAgg? agg))
            {
                agg = new MarkerAgg();
                markerAggs[path] = agg;
            }
            agg.TotalTicks += node.ElapsedTicks;
            agg.Calls += node.CallCount;
            if (node.ElapsedTicks > agg.MaxTicks) agg.MaxTicks = node.ElapsedTicks;
        }

        if (node.Marks != null)
        {
            foreach (var kv in node.Marks)
            {
                string mPath = path + "/" + kv.Key;
                if (markerAggs.Count < 512 || markerAggs.ContainsKey(mPath))
                {
                    if (!markerAggs.TryGetValue(mPath, out MarkerAgg? mAgg))
                    {
                        mAgg = new MarkerAgg();
                        markerAggs[mPath] = mAgg;
                    }
                    mAgg.TotalTicks += kv.Value.ElapsedTicks;
                    mAgg.Calls += kv.Value.CallCount;
                    if (kv.Value.ElapsedTicks > mAgg.MaxTicks) mAgg.MaxTicks = kv.Value.ElapsedTicks;
                }
            }
        }

        if (node.ChildRanges != null)
        {
            foreach (var kv in node.ChildRanges)
            {
                AccumulateFrameProfiler(kv.Value, path + "/" + kv.Key);
            }
        }
    }

    private void EnableOptimumDiagnosticsTemporarily()
    {
        savedOptFlags.Clear();
        if (capi.World?.FrameProfiler != null)
        {
            FrameProfilerUtil fp = capi.World.FrameProfiler;
            savedFrameProfilerEnabled = fp.Enabled;
            hasSavedFrameProfiler = true;
            if (!fp.Enabled)
            {
                fp.Enabled = true;
                fp.Begin(null);
            }
        }

        Assembly apiAsm = typeof(ICoreClientAPI).Assembly;
        TryEnableBoolMember(apiAsm.GetType("Vintagestory.API.Config.OptimumGuiMetrics"), "Enabled");
        TryEnableBoolMember(apiAsm.GetType("Vintagestory.API.Config.OptimumChunkStreamStats"), "Enabled");
        Type? diag = apiAsm.GetType("Vintagestory.API.Config.OptimumDiagnostics");
        TryEnableBoolMember(diag, "CullWalkLogEnabled");
        TryEnableBoolMember(diag, "MeshProfileEnabled");
        TryEnableBoolMember(diag, "ItemRenderProfileEnabled");
    }

    private void TryEnableBoolMember(Type? type, string memberName)
    {
        if (type == null) return;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        FieldInfo? fi = type.GetField(memberName, flags);
        if (fi != null && fi.FieldType == typeof(bool))
        {
            bool prev = (bool)(fi.GetValue(null) ?? false);
            savedOptFlags.Add((null, fi, prev));
            fi.SetValue(null, true);
            return;
        }
        PropertyInfo? pi = type.GetProperty(memberName, flags);
        if (pi != null && pi.PropertyType == typeof(bool) && pi.CanRead && pi.CanWrite)
        {
            bool prev = (bool)(pi.GetValue(null) ?? false);
            savedOptFlags.Add((null, pi, prev));
            pi.SetValue(null, true);
        }
    }

    private void RestoreOptimumDiagnostics()
    {
        if (hasSavedFrameProfiler && capi.World?.FrameProfiler != null)
        {
            capi.World.FrameProfiler.Enabled = savedFrameProfilerEnabled;
            hasSavedFrameProfiler = false;
        }

        foreach (var (target, member, prev) in savedOptFlags)
        {
            try
            {
                if (member is FieldInfo fi) fi.SetValue(target, prev);
                else if (member is PropertyInfo pi) pi.SetValue(target, prev);
            }
            catch
            {
            }
        }
        savedOptFlags.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        uiOpen = false;
        hudOpen = false;
        recordingActive = false;
        StopCore();
    }
}
