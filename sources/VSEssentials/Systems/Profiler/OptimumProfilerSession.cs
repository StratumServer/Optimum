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
/// Active profiler instrumentation. Nothing is allocated or registered until the HUD, the
/// window or a recording needs it, and everything is torn down when the last consumer leaves.
/// The HUD only uses render-stage probes, GPU timestamps and the GC APIs; the window and
/// recordings add the extended diagnostics (EventListener, vanilla FrameProfiler, Optimum
/// metrics), which allocate per frame themselves.
/// </summary>
public sealed class OptimumProfilerSession : IDisposable
{
    private const int RollingCapacity = 240;
    private const int MaxRecordedFrames = 36_000;
    private const int InitialRecordFps = 250;
    private const int MaxMarkerNodes = 512;
    private const double CensusIntervalMs = 1000.0;
    private const double SnapshotReuseMs = 100.0;

    private sealed class Probe : IRenderer
    {
        private readonly OptimumProfilerSession session;
        private readonly int marker;
        private readonly int beginWindow;
        private readonly int endWindow;

        public Probe(OptimumProfilerSession session, double renderOrder, int marker, int beginWindow, int endWindow)
        {
            this.session = session;
            RenderOrder = renderOrder;
            this.marker = marker;
            this.beginWindow = beginWindow;
            this.endWindow = endWindow;
        }

        public double RenderOrder { get; }
        public int RenderRange => int.MaxValue;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage) => session.OnProbe(marker, beginWindow, endWindow);

        public void Dispose()
        {
        }
    }

    private sealed class MarkerNode
    {
        public readonly string Path;
        public Dictionary<string, MarkerNode>? Children;
        public long TotalTicks;
        public long MaxTicks;
        public int Calls;

        public MarkerNode(string path) => Path = path;

        public void Add(long ticks, int calls)
        {
            TotalTicks += ticks;
            Calls += calls;
            if (ticks > MaxTicks) MaxTicks = ticks;
        }
    }

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private readonly ICoreClientAPI capi;
    private readonly List<(IRenderer Renderer, EnumRenderStage Stage)> probes = new();
    private readonly ProfilerFrameSample[] rolling = new ProfilerFrameSample[RollingCapacity];
    private readonly ProfilerFrameSample[] orderedScratch = new ProfilerFrameSample[RollingCapacity];
    private readonly long[] cpuStamps = new long[ProfilerSegments.MarkerCount];
    private readonly double[] harvestSegments = new double[ProfilerSegments.Count];
    private readonly long[] harvestPrimitives = new long[ProfilerGlCounters.Count];
    private readonly long[] harvestSamples = new long[ProfilerGlCounters.Count];
    private readonly List<MemberInfo> flippedFlags = new();

    private OptimumGpuTimerRing? gpuRing;
    private OptimumRuntimeEventListener? runtimeListener;
    private OptimumWorldCensus? census;
    private ProfilerCensusSnapshot cachedCensus = new();
    private double cachedMeshPoolMb;
    private OptimumProfilerSnapshot? cachedSnapshot;
    private long cachedSnapshotFrame = -1;
    private long cachedSnapshotTicks;

    private ProfilerFrameSample[] recorded = Array.Empty<ProfilerFrameSample>();
    private int recordedCount;
    private long recordingFirstFrame;
    private long recordingStartTicks;
    private double recordingTargetSec;
    private bool recordingTruncated;
    private string? recordingOutDir;
    private Action<string>? recordingComplete;

    private MarkerNode? markerRoot;
    private int markerNodeCount;
    private ProfileEntryRange? lastFrameProfilerRoot;
    private bool frameProfilerEnabledByUs;

    private bool uiOpen;
    private bool hudOpen;
    private bool recordingActive;
    private bool detailsWanted;
    private bool glCountersWanted;
    private bool running;
    private bool extended;
    private bool disposed;
    private bool censusFailureLogged;

    private long frameIndex;
    private int cpuMask;
    private long prevDoneTicks;
    private long lastCensusTicks;
    private long prevAllocBytes;
    private int prevGen0;
    private int prevGen1;
    private int prevGen2;
    private long prevGcPauseTicks;
    private long prevLockCount;

    public OptimumProfilerSession(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public string LastExportPath { get; private set; } = "";
    public bool IsRunning => running;
    public bool IsUiOpen => uiOpen;
    public bool IsHudOpen => hudOpen;
    public bool IsRecording => recordingActive;
    public bool GlCountersEnabled => glCountersWanted;

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

    /// <summary>The window's Optimum and World tabs need the detailed census.</summary>
    public void SetDetailsWanted(bool wanted)
    {
        if (wanted && !detailsWanted)
        {
            lastCensusTicks = 0;
        }
        detailsWanted = wanted;
    }

    public void SetGlCountersEnabled(bool enabled)
    {
        glCountersWanted = enabled;
        if (gpuRing != null)
        {
            gpuRing.CountersEnabled = enabled;
        }
    }

    public bool StartRecording(int seconds, string? outDirOverride, Action<string>? onComplete)
    {
        if (disposed || recordingActive)
        {
            return false;
        }
        recordingTargetSec = Math.Clamp(seconds, 1, 120);
        int expected = (int)Math.Min(MaxRecordedFrames, recordingTargetSec * InitialRecordFps);
        if (recorded.Length < expected)
        {
            recorded = new ProfilerFrameSample[expected];
        }
        recordedCount = 0;
        recordingTruncated = false;
        recordingOutDir = outDirOverride;
        recordingComplete = onComplete;
        recordingStartTicks = Stopwatch.GetTimestamp();
        recordingActive = true;
        SyncLifecycle();
        return true;
    }

    public void StopAll()
    {
        uiOpen = false;
        hudOpen = false;
        recordingActive = false;
        recordingComplete = null;
        SyncLifecycle();
    }

    public void ResetCounters()
    {
        Array.Clear(rolling);
        cachedSnapshot = null;
        markerRoot = null;
        markerNodeCount = 0;
        runtimeListener?.Reset();
        typeof(ICoreClientAPI).Assembly.GetType("Vintagestory.API.Config.OptimumDiagnostics")?
            .GetMethod("ResetAllCounters", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
    }

    /// <summary>
    /// Exports the rolling window. The snapshot is built here on the render thread; the files
    /// are written on a worker so the game does not hitch. Returns the target directory, or
    /// null when the profiler is not running.
    /// </summary>
    public string? ExportSnapshotNow(string? outDirOverride, Action<string>? onComplete)
    {
        if (!running)
        {
            return null;
        }
        ProfilerFrameSample[] samples = orderedScratch.AsSpan(0, CopyRollingInOrder()).ToArray();
        return QueueExport(outDirOverride, samples, onComplete);
    }

    public OptimumProfilerSnapshot BuildSnapshot()
    {
        long now = Stopwatch.GetTimestamp();
        if (cachedSnapshot != null && (cachedSnapshotFrame == frameIndex || (now - cachedSnapshotTicks) * TicksToMs < SnapshotReuseMs))
        {
            return cachedSnapshot;
        }
        cachedSnapshot = BuildSnapshotFromSamples(orderedScratch.AsSpan(0, CopyRollingInOrder()), cachedCensus, CaptureContext(AllMarkers()));
        cachedSnapshotFrame = frameIndex;
        cachedSnapshotTicks = now;
        return cachedSnapshot;
    }

    private void SyncLifecycle()
    {
        bool needActive = !disposed && (uiOpen || hudOpen || recordingActive);
        if (needActive && !running)
        {
            StartCore();
        }
        else if (!needActive && running)
        {
            StopCore();
        }
        if (running)
        {
            SetExtended(uiOpen || recordingActive);
        }
    }

    private void StartCore()
    {
        gpuRing = new OptimumGpuTimerRing { CountersEnabled = glCountersWanted };
        census ??= new OptimumWorldCensus(capi);
        prevDoneTicks = 0;
        lastCensusTicks = 0;
        cachedSnapshot = null;
        RegisterProbes();
        running = true;
    }

    private void StopCore()
    {
        running = false;
        SetExtended(false);
        // LeaveWorld can arrive on a shutdown signal thread. The event API and the GL context are
        // main-thread only and go away with the world; the probes stay inert once running is false.
        if (Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId)
        {
            foreach ((IRenderer renderer, EnumRenderStage stage) in probes)
            {
                capi.Event.UnregisterRenderer(renderer, stage);
            }
            gpuRing?.Dispose();
        }
        probes.Clear();
        gpuRing = null;
        markerRoot = null;
        markerNodeCount = 0;
    }

    private void RegisterProbes()
    {
        const double first = double.NegativeInfinity;
        const double last = double.PositiveInfinity;
        AddProbe(EnumRenderStage.Before, first, ProfilerMarker.BeforeStart);
        AddProbe(EnumRenderStage.Before, last, ProfilerMarker.BeforeEnd);
        AddProbe(EnumRenderStage.ShadowNear, first, ProfilerMarker.ShadowNearStart, beginWindow: 0);
        AddProbe(EnumRenderStage.ShadowNear, last, endWindow: 0);
        AddProbe(EnumRenderStage.Opaque, first, ProfilerMarker.OpaqueStart, beginWindow: 1);
        AddProbe(EnumRenderStage.Opaque, ProfilerGlCounters.SunQueryOrder - 0.5, endWindow: 1);
        AddProbe(EnumRenderStage.Opaque, ProfilerGlCounters.SunQueryOrder + 0.5, beginWindow: 2);
        AddProbe(EnumRenderStage.Opaque, last, endWindow: 2);
        AddProbe(EnumRenderStage.OIT, first, ProfilerMarker.OitStart, beginWindow: 3);
        AddProbe(EnumRenderStage.OIT, last, endWindow: 3);
        AddProbe(EnumRenderStage.AfterOIT, first, ProfilerMarker.AfterOitStart);
        AddProbe(EnumRenderStage.AfterPostProcessing, first, ProfilerMarker.AfterPostProcessingStart);
        AddProbe(EnumRenderStage.AfterFinalComposition, first, ProfilerMarker.AfterFinalCompositionStart);
        AddProbe(EnumRenderStage.AfterBlit, first, ProfilerMarker.AfterBlitStart);
        AddProbe(EnumRenderStage.Ortho, first, ProfilerMarker.OrthoStart, beginWindow: 4);
        AddProbe(EnumRenderStage.Ortho, last, endWindow: 4);
        AddProbe(EnumRenderStage.Done, first, ProfilerMarker.DoneStart);
        AddProbe(EnumRenderStage.Done, last, ProfilerMarker.DoneEnd);
    }

    private void AddProbe(EnumRenderStage stage, double order, ProfilerMarker? marker = null, int beginWindow = -1, int endWindow = -1)
    {
        var probe = new Probe(this, order, marker.HasValue ? (int)marker.Value : -1, beginWindow, endWindow);
        capi.Event.RegisterRenderer(probe, stage, "optimum-profiler");
        probes.Add((probe, stage));
    }

    private void OnProbe(int marker, int beginWindow, int endWindow)
    {
        if (!running)
        {
            return;
        }
        if (endWindow >= 0)
        {
            gpuRing!.EndWindow(endWindow);
        }
        if (marker >= 0)
        {
            if (marker == (int)ProfilerMarker.BeforeStart)
            {
                OnFrameStart();
            }
            cpuStamps[marker] = Stopwatch.GetTimestamp();
            cpuMask |= 1 << marker;
            gpuRing!.Mark((ProfilerMarker)marker);
            if (marker == (int)ProfilerMarker.DoneEnd)
            {
                OnFrameEnd();
            }
        }
        if (beginWindow >= 0)
        {
            gpuRing!.BeginWindow(beginWindow);
        }
    }

    private void OnFrameStart()
    {
        frameIndex++;
        cpuMask = 0;
        gpuRing!.BeginFrame(frameIndex);
        ReadFrameProfiler();
    }

    private void OnFrameEnd()
    {
        gpuRing!.EndFrame();
        long now = cpuStamps[(int)ProfilerMarker.DoneEnd];
        long allocBytes = GC.GetTotalAllocatedBytes(false);
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        long pauseTicks = GC.GetTotalPauseDuration().Ticks;
        long locks = Monitor.LockContentionCount;

        bool stored = prevDoneTicks != 0;
        if (stored)
        {
            ref ProfilerFrameSample s = ref rolling[frameIndex % RollingCapacity];
            s = default;
            s.FrameIndex = frameIndex;
            s.StartTicks = prevDoneTicks;
            s.FrameMs = (now - prevDoneTicks) * TicksToMs;
            s.OutsideRenderMs = Math.Max(0, cpuStamps[(int)ProfilerMarker.BeforeStart] - prevDoneTicks) * TicksToMs;
            s.CpuRenderMs = ProfilerSegments.Compute(cpuStamps, cpuMask, TicksToMs, s.Cpu);
            s.SleepMs = s.SwapMs = double.NaN;
            s.AllocBytes = Math.Max(0, allocBytes - prevAllocBytes);
            s.Gen0Delta = gen0 - prevGen0;
            s.Gen1Delta = gen1 - prevGen1;
            s.Gen2Delta = gen2 - prevGen2;
            s.GcPauseMs = (pauseTicks - prevGcPauseTicks) / (double)TimeSpan.TicksPerMillisecond;
            s.LockContentionDelta = locks - prevLockCount;
            s.LoadedChunks = cachedCensus.ChunksTotal;
            s.MeshPoolMb = cachedMeshPoolMb;
        }

        prevDoneTicks = now;
        prevAllocBytes = allocBytes;
        prevGen0 = gen0;
        prevGen1 = gen1;
        prevGen2 = gen2;
        prevGcPauseTicks = pauseTicks;
        prevLockCount = locks;

        HarvestGpu();
        if (lastCensusTicks == 0 || (now - lastCensusTicks) * TicksToMs >= CensusIntervalMs)
        {
            lastCensusTicks = now;
            cachedCensus = CollectCensus(detailsWanted);
            if (detailsWanted)
            {
                cachedCensus = census!.WithDiagnostics(cachedCensus);
            }
            cachedMeshPoolMb = 0;
            foreach (ProfilerMeshPoolPassStat pool in cachedCensus.MeshPools)
            {
                // Chunk vertices are about 44 bytes (position, uv, color, flags, custom data).
                cachedMeshPoolMb += pool.UsedVertices * 44.0 / (1024.0 * 1024.0);
            }
        }
        if (stored && recordingActive)
        {
            Record(in rolling[frameIndex % RollingCapacity], now);
        }
    }

    private void Record(in ProfilerFrameSample sample, long now)
    {
        if (recordedCount == 0)
        {
            // Every frame after the first one of a session is stored, so frame N lands at index N - first.
            recordingFirstFrame = sample.FrameIndex;
        }
        if (recordedCount == recorded.Length)
        {
            if (recorded.Length >= MaxRecordedFrames)
            {
                recordingTruncated = true;
                FinishRecording();
                return;
            }
            Array.Resize(ref recorded, Math.Min(MaxRecordedFrames, recorded.Length * 2));
        }
        recorded[recordedCount++] = sample;
        if ((now - recordingStartTicks) * TicksToMs >= recordingTargetSec * 1000.0)
        {
            FinishRecording();
        }
    }

    private void FinishRecording()
    {
        recordingActive = false;
        QueueExport(recordingOutDir, recorded.AsSpan(0, recordedCount).ToArray(), recordingComplete);
        recordingComplete = null;
        recordingOutDir = null;
        // This runs inside the Done stage loop, which iterates the renderer list by index, so
        // unregistering the probes now would shift it under the loop.
        capi.Event.EnqueueMainThreadTask(SyncLifecycle, "optimum-profiler");
    }

    /// <summary>
    /// Captures world data and session state on the render thread; the Optimum diagnostics, the
    /// snapshot statistics and the files are produced on a worker so the game does not hitch.
    /// </summary>
    private string QueueExport(string? outDirOverride, ProfilerFrameSample[] samples, Action<string>? onComplete)
    {
        string dir = outDirOverride ?? Path.Combine(GamePaths.Logs, "OptimumProfiler", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        LastExportPath = dir;
        ProfilerCensusSnapshot world = CollectCensus(true);
        ProfilerSnapshotContext context = CaptureContext(AllMarkers());
        OptimumWorldCensus? collector = census;
        TyronThreadPool.QueueTask(() =>
        {
            try
            {
                ProfilerCensusSnapshot full = collector?.WithDiagnostics(world) ?? world;
                OptimumPerfettoExporter.ExportBundle(dir, samples, BuildSnapshotFromSamples(samples, full, context));
            }
            catch (Exception e)
            {
                capi.Logger.Error("[Optimum] Profiler export to {0} failed: {1}", dir, e);
                return;
            }
            capi.Event.EnqueueMainThreadTask(() => onComplete?.Invoke(dir), "optimum-profiler-export");
        }, "optimum-profiler-export");
        return dir;
    }

    private void HarvestGpu()
    {
        while (gpuRing!.TryHarvest(out long frame, harvestSegments, out double spanMs, out bool hasCounters, harvestPrimitives, harvestSamples))
        {
            WriteGpu(ref rolling[frame % RollingCapacity], frame, spanMs, hasCounters);
            long r = frame - recordingFirstFrame;
            if (r >= 0 && r < recordedCount)
            {
                WriteGpu(ref recorded[r], frame, spanMs, hasCounters);
            }
        }
    }

    private void WriteGpu(ref ProfilerFrameSample s, long frame, double spanMs, bool hasCounters)
    {
        if (s.FrameIndex != frame)
        {
            return;
        }
        s.HasGpu = true;
        s.GpuSpanMs = spanMs;
        harvestSegments.CopyTo((Span<double>)s.Gpu);
        s.HasCounters = hasCounters;
        if (hasCounters)
        {
            harvestPrimitives.CopyTo((Span<long>)s.Primitives);
            harvestSamples.CopyTo((Span<long>)s.SamplesPassed);
        }
    }

    private ProfilerCensusSnapshot CollectCensus(bool details)
    {
        try
        {
            return census?.Collect(gpuRing, details) ?? cachedCensus;
        }
        catch (Exception e)
        {
            if (!censusFailureLogged)
            {
                censusFailureLogged = true;
                capi.Logger.Warning("[Optimum] Profiler census failed, keeping the previous one: {0}", e);
            }
            return cachedCensus;
        }
    }

    /// <summary>
    /// Folds the previous frame's vanilla FrameProfiler tree into the marker totals and stores its
    /// waits (limiter sleep, SwapBuffers, game tick) on that frame. Vanilla rebuilds the tree each
    /// frame, so aggregation nodes are keyed by path and their path strings are built once.
    /// </summary>
    private void ReadFrameProfiler()
    {
        ProfileEntryRange? root = capi.World.FrameProfiler?.PrevRootEntry;
        if (!extended || root == null || ReferenceEquals(root, lastFrameProfilerRoot) || root.Marks == null ||
            !root.Marks.TryGetValue("sleep", out ProfileEntry? sleep))
        {
            // Trees without a "sleep" mark come from vanilla's mouse event Begin/End pairs.
            return;
        }
        lastFrameProfilerRoot = root;
        markerRoot ??= new MarkerNode("all");
        AccumulateMarkers(root, markerRoot);

        long prev = frameIndex - 1;
        ref ProfilerFrameSample s = ref rolling[prev % RollingCapacity];
        if (s.FrameIndex == prev)
        {
            s.SleepMs = sleep.ElapsedTicks * TicksToMs;
            // Vanilla's End() marks "end" right after SwapBuffers returns.
            s.SwapMs = root.Marks.TryGetValue("end", out ProfileEntry? swap) ? swap.ElapsedTicks * TicksToMs : 0.0;
            long r = prev - recordingFirstFrame;
            if (recordingActive && r >= 0 && r < recordedCount)
            {
                recorded[r].SleepMs = s.SleepMs;
                recorded[r].SwapMs = s.SwapMs;
            }
        }
    }

    private void AccumulateMarkers(ProfileEntryRange range, MarkerNode node)
    {
        node.Add(range.ElapsedTicks, range.CallCount);
        if (range.Marks != null)
        {
            foreach ((string key, ProfileEntry mark) in range.Marks)
            {
                ChildOf(node, key)?.Add(mark.ElapsedTicks, mark.CallCount);
            }
        }
        if (range.ChildRanges != null)
        {
            foreach ((string key, ProfileEntryRange child) in range.ChildRanges)
            {
                if (ChildOf(node, key) is MarkerNode childNode)
                {
                    AccumulateMarkers(child, childNode);
                }
            }
        }
    }

    private MarkerNode? ChildOf(MarkerNode node, string key)
    {
        node.Children ??= new Dictionary<string, MarkerNode>(StringComparer.Ordinal);
        if (!node.Children.TryGetValue(key, out MarkerNode? child) && markerNodeCount < MaxMarkerNodes)
        {
            child = new MarkerNode(node.Path + "/" + key);
            node.Children[key] = child;
            markerNodeCount++;
        }
        return child;
    }

    /// <summary>Copies the stored rolling frames, oldest first, into the reusable scratch and returns the count.</summary>
    private int CopyRollingInOrder()
    {
        int count = 0;
        for (long f = Math.Max(1, frameIndex - RollingCapacity + 1); f <= frameIndex; f++)
        {
            ref ProfilerFrameSample s = ref rolling[f % RollingCapacity];
            if (s.FrameIndex == f)
            {
                orderedScratch[count++] = s;
            }
        }
        return count;
    }

    private ProfilerSnapshotContext CaptureContext(IReadOnlyList<ProfilerMarkerStat> markers)
    {
        int vsync = capi.Settings.Int["vsyncMode"];
        int maxFps = capi.Settings.Int["maxFps"];
        return new ProfilerSnapshotContext(
            vsync != 0 || (maxFps > 10 && maxFps < 241),
            glCountersWanted,
            recordingActive,
            recordingTruncated,
            recordingActive ? (Stopwatch.GetTimestamp() - recordingStartTicks) * TicksToMs / 1000.0 : 0.0,
            recordingTargetSec,
            LastExportPath,
            extended ? "probes, gpu-timestamps, gc-apis, event-listener, frame-profiler, optimum-metrics" : "probes, gpu-timestamps, gc-apis",
            runtimeListener,
            markers);
    }

    private static OptimumProfilerSnapshot BuildSnapshotFromSamples(ReadOnlySpan<ProfilerFrameSample> samples, ProfilerCensusSnapshot censusSnapshot, ProfilerSnapshotContext context)
    {
        int n = samples.Length;
        double[] frameMs = new double[n];
        double[] gpuSpans = new double[n];
        Span<double> cpuSums = stackalloc double[ProfilerSegments.Count];
        Span<double> gpuSums = stackalloc double[ProfilerSegments.Count];
        long[] primSums = new long[ProfilerGlCounters.Count];
        long[] sampleSums = new long[ProfilerGlCounters.Count];
        int gpuFrames = 0, counterFrames = 0, waitFrames = 0;
        double totalMs = 0, cpuRender = 0, outside = 0, pauseMs = 0, sleepMs = 0, swapMs = 0, outsideWithWaits = 0;
        long allocBytes = 0, gen0 = 0, gen1 = 0, gen2 = 0, locks = 0;

        for (int i = 0; i < n; i++)
        {
            ref readonly ProfilerFrameSample s = ref samples[i];
            frameMs[i] = s.FrameMs;
            totalMs += s.FrameMs;
            cpuRender += s.CpuRenderMs;
            outside += s.OutsideRenderMs;
            allocBytes += s.AllocBytes;
            gen0 += s.Gen0Delta;
            gen1 += s.Gen1Delta;
            gen2 += s.Gen2Delta;
            pauseMs += s.GcPauseMs;
            locks += s.LockContentionDelta;
            for (int k = 0; k < ProfilerSegments.Count; k++)
            {
                cpuSums[k] += s.Cpu[k];
            }
            if (s.HasGpu)
            {
                gpuSpans[gpuFrames++] = s.GpuSpanMs;
                for (int k = 0; k < ProfilerSegments.Count; k++)
                {
                    gpuSums[k] += s.Gpu[k];
                }
            }
            if (s.HasCounters)
            {
                counterFrames++;
                for (int c = 0; c < ProfilerGlCounters.Count; c++)
                {
                    primSums[c] += s.Primitives[c];
                    sampleSums[c] += s.SamplesPassed[c];
                }
            }
            if (!double.IsNaN(s.SwapMs))
            {
                waitFrames++;
                sleepMs += s.SleepMs;
                swapMs += s.SwapMs;
                outsideWithWaits += s.OutsideRenderMs;
            }
        }

        double[] recent = (double[])frameMs.Clone();
        ProfilerDistribution frameDist = ProfilerDistribution.Compute(frameMs);
        ProfilerDistribution gpuDist = ProfilerDistribution.Compute(gpuSpans.AsSpan(0, gpuFrames));
        double windowSec = totalMs / 1000.0;
        double cpuDiv = Math.Max(1, n);
        double gpuDiv = Math.Max(1, gpuFrames);
        var cpuMeans = new KeyValuePair<string, double>[ProfilerSegments.Count + 1];
        var gpuMeans = new KeyValuePair<string, double>[ProfilerSegments.Count + 1];
        int cpuTop = 0, gpuTop = 0;
        for (int k = 0; k < ProfilerSegments.Count; k++)
        {
            cpuMeans[k] = new(ProfilerSegments.Labels[k], cpuSums[k] / cpuDiv);
            gpuMeans[k] = new(ProfilerSegments.Labels[k], gpuSums[k] / gpuDiv);
            if (cpuSums[k] > cpuSums[cpuTop]) cpuTop = k;
            if (gpuSums[k] > gpuSums[gpuTop]) gpuTop = k;
        }
        cpuMeans[ProfilerSegments.Count] = new("Tick+Swap", outside / cpuDiv);
        gpuMeans[ProfilerSegments.Count] = new("Tick+Swap", 0.0);

        double gcPct = totalMs > 0.001 ? pauseMs * 100.0 / totalMs : 0.0;
        double waitDiv = Math.Max(1, waitFrames);
        double? sleepMean = waitFrames > 0 ? sleepMs / waitDiv : null;
        double? swapMean = waitFrames > 0 ? swapMs / waitDiv : null;
        double? tickMean = waitFrames > 0 ? Math.Max(0, (outsideWithWaits - sleepMs - swapMs) / waitDiv) : null;
        var verdictInput = new ProfilerVerdictInput(
            frameDist.Mean,
            cpuRender / cpuDiv,
            outside / cpuDiv,
            gpuDist.Mean,
            sleepMean ?? double.NaN,
            swapMean ?? double.NaN,
            tickMean ?? double.NaN,
            gcPct,
            context.FrameLimiterActive,
            ProfilerSegments.Labels[cpuTop],
            cpuSums[cpuTop] / cpuDiv,
            ProfilerSegments.Labels[gpuTop],
            gpuSums[gpuTop] / gpuDiv);

        return new OptimumProfilerSnapshot
        {
            TotalFrames = n,
            GpuFrames = gpuFrames,
            WindowSeconds = windowSec,
            AvgFps = frameDist.Mean > 0.001 ? 1000.0 / frameDist.Mean : 0.0,
            Low1Fps = frameDist.OnePercentLowFps,
            FrameDist = frameDist,
            GpuSpanDist = gpuDist,
            CpuRenderMeanMs = cpuRender / cpuDiv,
            OutsideRenderMeanMs = outside / cpuDiv,
            SleepMeanMs = sleepMean,
            SwapMeanMs = swapMean,
            TickMeanMs = tickMean,
            AllocMbPerSec = windowSec > 0.001 ? allocBytes / 1048576.0 / windowSec : 0.0,
            Gen0Count = gen0,
            Gen1Count = gen1,
            Gen2Count = gen2,
            GcPausePct = gcPct,
            LockContentionsPerSec = windowSec > 0.001 ? locks / windowSec : 0.0,
            GlCountersEnabled = context.GlCountersEnabled,
            IsRecording = context.IsRecording,
            RecordingTruncated = context.RecordingTruncated,
            RecordingElapsedSec = context.RecordingElapsedSec,
            RecordingTargetSec = context.RecordingTargetSec,
            LastExportPath = context.LastExportPath,
            Instrumentation = context.Instrumentation,
            RecentFrameMs = recent,
            StageCpuMeansMs = cpuMeans,
            StageGpuMeansMs = gpuMeans,
            GlPrimitivesMean = counterFrames > 0 ? primSums.Select(v => v / counterFrames).ToArray() : Array.Empty<long>(),
            GlSamplesPassedMean = counterFrames > 0 ? sampleSums.Select(v => v / counterFrames).ToArray() : Array.Empty<long>(),
            Verdict = ProfilerVerdict.Classify(verdictInput),
            Runtime = context.Listener?.GetSnapshot(16) ?? new ProfilerRuntimeSnapshot(),
            Census = censusSnapshot,
            FrameProfilerMarkers = context.Markers.OrderByDescending(m => m.TotalMs).Take(15).ToList(),
        };
    }

    /// <summary>All aggregated FrameProfiler paths as value copies, so a worker can sort them.</summary>
    private List<ProfilerMarkerStat> AllMarkers()
    {
        var all = new List<ProfilerMarkerStat>(markerNodeCount + 1);
        if (markerRoot == null)
        {
            return all;
        }
        var stack = new Stack<MarkerNode>();
        stack.Push(markerRoot);
        while (stack.Count > 0)
        {
            MarkerNode node = stack.Pop();
            all.Add(new ProfilerMarkerStat { Path = node.Path, TotalMs = node.TotalTicks * TicksToMs, MaxMs = node.MaxTicks * TicksToMs, Calls = node.Calls });
            if (node.Children != null)
            {
                foreach (MarkerNode child in node.Children.Values)
                {
                    stack.Push(child);
                }
            }
        }
        return all;
    }

    /// <summary>
    /// Turns the extended diagnostics on for the window and recordings, and restores only the
    /// switches this session flipped, so a vanilla tick profiler opened meanwhile stays on.
    /// </summary>
    private void SetExtended(bool on)
    {
        if (on == extended)
        {
            return;
        }
        extended = on;
        FrameProfilerUtil? fp = capi.World.FrameProfiler;
        if (on)
        {
            runtimeListener = new OptimumRuntimeEventListener();
            if (fp != null && !fp.Enabled)
            {
                fp.Enabled = true;
                // Enabling mid-frame needs a root entry, or the frame's End() dereferences null.
                fp.Begin(null);
                frameProfilerEnabledByUs = true;
            }
            Assembly api = typeof(ICoreClientAPI).Assembly;
            Type? diagnostics = api.GetType("Vintagestory.API.Config.OptimumDiagnostics");
            FlipOn(api.GetType("Vintagestory.API.Config.OptimumGuiMetrics"), "Enabled");
            FlipOn(api.GetType("Vintagestory.API.Config.OptimumChunkStreamStats"), "Enabled");
            FlipOn(diagnostics, "MeshProfileEnabled");
            FlipOn(diagnostics, "ItemRenderProfileEnabled");
            return;
        }

        runtimeListener?.Dispose();
        runtimeListener = null;
        if (frameProfilerEnabledByUs && fp != null)
        {
            fp.Enabled = fp.PrintSlowTicks;
            frameProfilerEnabledByUs = false;
        }
        foreach (MemberInfo member in flippedFlags)
        {
            if (member is FieldInfo fi) fi.SetValue(null, false);
            else if (member is PropertyInfo pi) pi.SetValue(null, false);
        }
        flippedFlags.Clear();
        lastFrameProfilerRoot = null;
    }

    private void FlipOn(Type? type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        if (type?.GetField(name, flags) is FieldInfo fi && fi.FieldType == typeof(bool) && !(bool)fi.GetValue(null)!)
        {
            fi.SetValue(null, true);
            flippedFlags.Add(fi);
        }
        else if (type?.GetProperty(name, flags) is PropertyInfo pi && pi.PropertyType == typeof(bool) && pi.CanWrite && !(bool)pi.GetValue(null)!)
        {
            pi.SetValue(null, true);
            flippedFlags.Add(pi);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        uiOpen = hudOpen = recordingActive = false;
        recordingComplete = null;
        if (running)
        {
            StopCore();
        }
    }
}
