using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Every place the backend makes the CPU wait on the GPU or the presentation
/// engine. Fixed and small so the counters are two flat arrays; the order is the
/// order of the tokens on the <c>stats.waits</c> line.
/// </summary>
internal enum WaitSite
{
    /// <summary>
    /// vkWaitSemaphores on the Frame timeline for value n - FramesInFlight at the
    /// start of frame n; exactly one per frame start, the ring's only steady-state wait.
    /// </summary>
    FramePacing = 0,
    /// <summary>
    /// An upload that waited for the GPU. Retired in Phase 1B step 3: uploads ride
    /// the next frame submission (UploadManager) and never wait, so this stays
    /// zero; the token stays for log compatibility and the pacing gate.
    /// </summary>
    UploadSubmit = 1,
    /// <summary>
    /// The Frame timeline wait inside a mid-frame flush. Retired in Phase 1B:
    /// readbacks and uploads submit partially without waiting and queries never
    /// flush, so this stays zero; the token stays for log compatibility.
    /// </summary>
    FlushFrame = 2,
    /// <summary>vkDeviceWaitIdle, wherever it is called.</summary>
    DeviceWaitIdle = 3,
    /// <summary>
    /// A readback the caller needs now: the Frame timeline value of the partial
    /// submission that carried the copy, or a between-frames setup fence.
    /// </summary>
    Readback = 4,
    /// <summary>
    /// Polling an occlusion query until its result is available. Retired in
    /// Phase 1B (QueryRing reads results without waiting); stays zero.
    /// </summary>
    OcclusionQuery = 5,
    /// <summary>vkAcquireNextImageKHR.</summary>
    SwapchainAcquire = 6,
    /// <summary>vkQueuePresentKHR, including the queue lock.</summary>
    Present = 7,
    /// <summary>
    /// vkQueueSubmit of a frame slot, including the queue lock. A worker's
    /// synchronous upload holds that lock through its fence wait, so the render
    /// thread can stall here on GPU work it did not issue.
    /// </summary>
    QueueSubmit = 8,
    /// <summary>
    /// The latency backend's one sleep before the frame's input is sampled
    /// (plan section "Latency seams", seam S6). Zero with the None backend, which
    /// never sleeps; with a backend active this is where the frame waits, and
    /// <see cref="FramePacing" /> should find its value already signalled.
    /// </summary>
    LatencySleep = 9,
}

/// <summary>
/// Per-second counters for the work the backend does, so a slow phase can be
/// attributed rather than guessed at.
///
/// A frame time on its own says the renderer is slow; it does not say whether
/// the cost is device allocations, blocking uploads waiting on the GPU, or
/// descriptor churn. These count each of those and report them together.
///
/// Off unless OPTIMUM_VULKAN_STATS names a file. The counters themselves are
/// always live - they are interlocked increments on paths that already cost
/// microseconds apiece, so they do not need gating, and having them unconditional
/// means a report can be turned on for a session that is already misbehaving.
///
/// One sample is four lines. The first is the original human-readable line and
/// keeps its format byte for byte (older logs and readers depend on it); the
/// other three carry stable <c>key=value</c> tokens for scripts
/// (<c>scripts/dev/pacing-gate.sh</c>):
/// <code>
/// stats 1.0s: 60 frames (16.7 ms/frame), ...
/// stats.pacing samples=512 p50_ms=16.667 p95_ms=17.100 p99_ms=18.300 stddev_ms=0.420 stutters=0
/// stats.waits frame_pacing_n=60 frame_pacing_ms=812.4 upload_submit_n=0 upload_submit_ms=0.0 ... queue_submit_n=60 queue_submit_ms=1.9
/// stats.counters blocking_uploads=0 uploads=0 scopes=900 barriers=12 rebar_fallbacks=0 dynamic_state=12600 uniform_ring_used=412800 uniform_ring_capacity=16777216
/// stats.latency backend=off mode=off sleep_n=0 sleep_ms=0.0 frames=60 input_mean_ms=0.02 input_p99_ms=0.04 ... total_mean_ms=16.60 total_p99_ms=18.20
/// </code>
/// </summary>
internal static class VulkanStats
{
    /// <summary>Token stems of <see cref="WaitSite" />, indexed by its value.</summary>
    public static readonly string[] WaitSiteTokens =
    {
        "frame_pacing",
        "upload_submit",
        "flush_frame",
        "device_wait_idle",
        "readback",
        "occlusion_query",
        "swapchain_acquire",
        "present",
        "queue_submit",
        "latency_sleep",
    };

    public const int WaitSiteCount = 10;

    /// <summary>
    /// Dynamic-state commands <c>VulkanDevice.ApplyDynamicState</c> can record for
    /// one draw: all of them, at the first draw of a command buffer. Later draws
    /// record only the ones whose value changed (Phase 1B step 6). A source test
    /// keeps this equal to the calls in that method.
    /// </summary>
    public const int DynamicStateCommandsPerDraw = 14;

    private static long _allocations;
    private static long _uploads;
    private static long _uploadWaitTicks;
    private static long _texturesCreated;
    private static long _texturesDeleted;
    private static long _frames;
    private static long _droppedMeshWrites;
    private static long _uniformOverflows;

    private static long _blockingUploads;
    private static long _uploadRequests;
    private static long _scopesOpened;
    private static long _imageBarriers;
    private static long _barrierCommands;
    private static long _rebarFallbacks;
    private static long _dynamicStateCommands;
    private static long _uniformRingPeak;
    private static long _uniformRingCapacity;
    private static readonly long[] _waitCounts = new long[WaitSiteCount];
    private static readonly long[] _waitTicks = new long[WaitSiteCount];

    /// <summary>CPU frame intervals of the last 512 frames, any device.</summary>
    public static readonly FrameIntervalRing FrameIntervals = new(FrameIntervalRing.DefaultCapacity);

    /// <summary>A mesh write that could not land; see MeshManager.Write.</summary>
    public static void NoteDroppedMeshWrite() => Interlocked.Increment(ref _droppedMeshWrites);

    public static long DroppedMeshWrites => Interlocked.Read(ref _droppedMeshWrites);

    /// <summary>A named uniform block that did not fit the frame ring and took a transient buffer instead.</summary>
    public static void NoteUniformOverflow() => Interlocked.Increment(ref _uniformOverflows);

    public static long UniformOverflows => Interlocked.Read(ref _uniformOverflows);

    public static void NoteAllocation() => Interlocked.Increment(ref _allocations);
    public static void NoteTextureCreated() => Interlocked.Increment(ref _texturesCreated);
    public static void NoteTextureDeleted() => Interlocked.Increment(ref _texturesDeleted);
    public static void NoteFrame() => Interlocked.Increment(ref _frames);

    /// <summary>Textures deleted since the last <see cref="SampleIfDue" />.</summary>
    public static long TexturesDeleted => Interlocked.Read(ref _texturesDeleted);

    /// <summary>
    /// A synchronous setup submission of any kind (uploads and readbacks alike).
    /// Feeds the original line's "blocking uploads" figure, whose meaning is kept.
    /// </summary>
    public static void NoteUpload(long elapsedTicks)
    {
        Interlocked.Increment(ref _uploads);
        Interlocked.Add(ref _uploadWaitTicks, elapsedTicks);
    }

    /// <summary>A texture upload or mip generation was requested, whether or not it waited.</summary>
    public static void NoteUploadRequest() => Interlocked.Increment(ref _uploadRequests);

    /// <summary>An upload that really waited on a fence or the queue lock.</summary>
    public static void NoteBlockingUpload() => Interlocked.Increment(ref _blockingUploads);

    public static long BlockingUploads => Interlocked.Read(ref _blockingUploads);
    public static long UploadRequests => Interlocked.Read(ref _uploadRequests);

    private static long _inlineUploads;
    private static long _stagingOverflows;
    private static long _uploadBatchGrowths;

    /// <summary>
    /// An upload recorded into the frame command buffer because that command
    /// buffer already used its destination (GL order), instead of the upload batch.
    /// </summary>
    public static void NoteInlineUpload() => Interlocked.Increment(ref _inlineUploads);

    /// <summary>An upload that did not fit its batch's staging region and took a dedicated staging buffer.</summary>
    public static void NoteStagingOverflow() => Interlocked.Increment(ref _stagingOverflows);

    /// <summary>An upload batch created beyond the staging ring's regions (more batches in flight than frames).</summary>
    public static void NoteUploadBatchGrowth() => Interlocked.Increment(ref _uploadBatchGrowths);

    public static long InlineUploads => Interlocked.Read(ref _inlineUploads);
    public static long StagingOverflows => Interlocked.Read(ref _stagingOverflows);
    public static long UploadBatchGrowths => Interlocked.Read(ref _uploadBatchGrowths);

    /// <summary>One vkCmdBeginRendering.</summary>
    public static void NoteScopeOpened() => Interlocked.Increment(ref _scopesOpened);

    public static long ScopesOpened => Interlocked.Read(ref _scopesOpened);

    private static long _maskRestarts;
    private static long _feedbackSplits;

    /// <summary>
    /// A scope restart that reopened exactly the attachment set it closed (same
    /// views, same layouts). Draw-buffer and colour-mask changes only alter write
    /// masks (Phase 2, C4), so this must stay 0.
    /// </summary>
    public static void NoteMaskRestart() => Interlocked.Increment(ref _maskRestarts);

    /// <summary>
    /// A scope restart because a draw samples a bound colour attachment its draw
    /// buffers exclude (the composition pass reads Primary 1), or because such a
    /// slot rejoins the scope once its draw buffer is enabled again.
    /// </summary>
    public static void NoteFeedbackSplit() => Interlocked.Increment(ref _feedbackSplits);

    private static long _passes;
    private static long _planHits;
    private static long _planMisses;
    private static long _inPassClears;
    private static long _promotedClears;
    private static long _standaloneClears;
    private static long _passSplits;

    /// <summary>A frame-graph pass opened its scope (a declared pass, or a scope no declaration covered).</summary>
    public static void NotePass() => Interlocked.Increment(ref _passes);

    /// <summary>A frame whose passes matched the plan solved from the previous frame exactly.</summary>
    public static void NotePlanHit() => Interlocked.Increment(ref _planHits);

    /// <summary>A frame recorded conservatively because it did not match the plan.</summary>
    public static void NotePlanMiss() => Interlocked.Increment(ref _planMisses);

    /// <summary>A clear recorded as vkCmdClearAttachments inside an open pass.</summary>
    public static void NoteInPassClear() => Interlocked.Increment(ref _inPassClears);

    /// <summary>A clear issued with no pass open that became LOAD_OP_CLEAR.</summary>
    public static void NotePromotedClear() => Interlocked.Increment(ref _promotedClears);

    /// <summary>A promoted clear recorded as a clear-image command (its image was used before a pass attached it).</summary>
    public static void NoteStandaloneClear() => Interlocked.Increment(ref _standaloneClears);

    /// <summary>A second rendering scope inside one declared pass.</summary>
    public static void NotePassSplit() => Interlocked.Increment(ref _passSplits);

    private static long _transientBytes;
    private static long _aliasedBytesPeak;
    private static long _transientLeases;
    private static long _aliasedLeases;
    private static long _readSelfCopies;
    private static long _readSelfPool;

    /// <summary>
    /// One finished frame's transients (Phase 2 step 4): bytes of transient images
    /// (the opted-in textures plus the allocator's physical images), bytes of leases
    /// served by an image an earlier lease of the frame used, the lease counts, and the
    /// ReadSelf copies the pool holds.
    /// </summary>
    public static void NoteTransientFrame(ulong transientBytes, ulong aliasedBytes, int leases, int aliasedLeases,
        int readSelfPool)
    {
        Interlocked.Exchange(ref _transientBytes, (long)Math.Min(transientBytes, long.MaxValue));
        long aliased = (long)Math.Min(aliasedBytes, long.MaxValue);
        long peak = Interlocked.Read(ref _aliasedBytesPeak);
        while (aliased > peak)
        {
            long seen = Interlocked.CompareExchange(ref _aliasedBytesPeak, aliased, peak);
            if (seen == peak) break;
            peak = seen;
        }
        Interlocked.Add(ref _transientLeases, leases);
        Interlocked.Add(ref _aliasedLeases, aliasedLeases);
        Interlocked.Exchange(ref _readSelfPool, readSelfPool);
    }

    /// <summary>A draw sampled a colour attachment it writes and took a pooled ReadSelf copy.</summary>
    public static void NoteReadSelfCopy() => Interlocked.Increment(ref _readSelfCopies);

    public static long ReadSelfCopies => Interlocked.Read(ref _readSelfCopies);

    public static long MaskRestarts => Interlocked.Read(ref _maskRestarts);
    public static long FeedbackSplits => Interlocked.Read(ref _feedbackSplits);

    /// <summary>Image memory barriers recorded into a command buffer.</summary>
    public static void NoteImageBarriers(int count) => Interlocked.Add(ref _imageBarriers, count);

    public static long ImageBarriers => Interlocked.Read(ref _imageBarriers);

    /// <summary>One vkCmdPipelineBarrier2 carrying image barriers (a BarrierBatcher flush).</summary>
    public static void NoteBarrierCommand() => Interlocked.Increment(ref _barrierCommands);

    public static long BarrierCommands => Interlocked.Read(ref _barrierCommands);

    /// <summary>A buffer that asked for ReBAR (device-local and host-visible) and fell back to plain host memory.</summary>
    public static void NoteRebarFallback() => Interlocked.Increment(ref _rebarFallbacks);

    public static long RebarFallbacks => Interlocked.Read(ref _rebarFallbacks);

    /// <summary>A multi-draw that did not fit its frame slot's indirect buffer and took an overflow buffer.</summary>
    public static void NoteIndirectOverflow() => Interlocked.Increment(ref _indirectOverflows);

    public static long IndirectOverflows => Interlocked.Read(ref _indirectOverflows);

    private static long _indirectOverflows;

    public static void NoteDynamicStateCommands(int count) => Interlocked.Add(ref _dynamicStateCommands, count);

    public static long DynamicStateCommands => Interlocked.Read(ref _dynamicStateCommands);

    /// <summary>
    /// Uniform-ring bytes one frame slot used by the time it was submitted, and
    /// that slot's capacity. The sample reports the peak since the last sample.
    /// </summary>
    public static void NoteUniformRingUse(ulong used, ulong capacity)
    {
        long value = (long)Math.Min(used, long.MaxValue);
        long peak = Interlocked.Read(ref _uniformRingPeak);
        while (value > peak)
        {
            long seen = Interlocked.CompareExchange(ref _uniformRingPeak, value, peak);
            if (seen == peak) break;
            peak = seen;
        }
        Interlocked.Exchange(ref _uniformRingCapacity, (long)Math.Min(capacity, long.MaxValue));
    }

    public static long UniformRingPeak => Interlocked.Read(ref _uniformRingPeak);

    /// <summary>A timestamp to hand to <see cref="NoteWait" /> once the wait returns.</summary>
    public static long WaitStart() => Stopwatch.GetTimestamp();

    /// <summary>Counts one wait at <paramref name="site" /> that began at <paramref name="startTimestamp" />.</summary>
    public static void NoteWait(WaitSite site, long startTimestamp)
    {
        int index = (int)site;
        Interlocked.Increment(ref _waitCounts[index]);
        Interlocked.Add(ref _waitTicks[index], Stopwatch.GetTimestamp() - startTimestamp);
    }

    public static long WaitCount(WaitSite site) => Interlocked.Read(ref _waitCounts[(int)site]);

    public static double WaitMilliseconds(WaitSite site) =>
        Interlocked.Read(ref _waitTicks[(int)site]) * 1000.0 / Stopwatch.Frequency;

    /// <summary>vkDeviceWaitIdle, counted at <see cref="WaitSite.DeviceWaitIdle" />. Every call site goes through here.</summary>
    public static Result WaitDeviceIdle(Vk api, Device device)
    {
        long start = Stopwatch.GetTimestamp();
        Result result = api.DeviceWaitIdle(device);
        NoteWait(WaitSite.DeviceWaitIdle, start);
        return result;
    }

    /// <summary>One CPU frame interval (start of a frame to start of the next), in milliseconds.</summary>
    public static void NoteFrameInterval(double milliseconds) => FrameIntervals.Add(milliseconds);

    /// <summary>
    /// Takes and clears the counters, formatted as one sample (four lines joined
    /// by '\n', no trailing newline), or null when the interval has not elapsed.
    /// Called once per frame by the render thread.
    /// </summary>
    public static string? SampleIfDue(TimeSpan interval)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _lastSample);
        if (last == 0)
        {
            Interlocked.CompareExchange(ref _lastSample, now, 0);
            return null;
        }

        double elapsed = (now - last) / (double)Stopwatch.Frequency;
        if (elapsed < interval.TotalSeconds) return null;
        if (Interlocked.CompareExchange(ref _lastSample, now, last) != last) return null;

        long frames = Interlocked.Exchange(ref _frames, 0);
        long allocations = Interlocked.Exchange(ref _allocations, 0);
        long uploads = Interlocked.Exchange(ref _uploads, 0);
        long uploadTicks = Interlocked.Exchange(ref _uploadWaitTicks, 0);
        long created = Interlocked.Exchange(ref _texturesCreated, 0);
        long deleted = Interlocked.Exchange(ref _texturesDeleted, 0);
        long dropped = Interlocked.Exchange(ref _droppedMeshWrites, 0);
        long overflows = Interlocked.Exchange(ref _uniformOverflows, 0);

        var waitCounts = new long[WaitSiteCount];
        var waitMs = new double[WaitSiteCount];
        for (int i = 0; i < WaitSiteCount; i++)
        {
            waitCounts[i] = Interlocked.Exchange(ref _waitCounts[i], 0);
            waitMs[i] = Interlocked.Exchange(ref _waitTicks[i], 0) * 1000.0 / Stopwatch.Frequency;
        }

        var counters = new CounterSample(
            BlockingUploads: Interlocked.Exchange(ref _blockingUploads, 0),
            Uploads: Interlocked.Exchange(ref _uploadRequests, 0),
            Scopes: Interlocked.Exchange(ref _scopesOpened, 0),
            Barriers: Interlocked.Exchange(ref _imageBarriers, 0),
            RebarFallbacks: Interlocked.Exchange(ref _rebarFallbacks, 0),
            DynamicState: Interlocked.Exchange(ref _dynamicStateCommands, 0),
            UniformRingUsed: Interlocked.Exchange(ref _uniformRingPeak, 0),
            UniformRingCapacity: Interlocked.Read(ref _uniformRingCapacity),
            BarrierCommands: Interlocked.Exchange(ref _barrierCommands, 0),
            Frames: frames,
            MaskRestarts: Interlocked.Exchange(ref _maskRestarts, 0),
            FeedbackSplits: Interlocked.Exchange(ref _feedbackSplits, 0),
            Passes: Interlocked.Exchange(ref _passes, 0),
            PlanHits: Interlocked.Exchange(ref _planHits, 0),
            PlanMisses: Interlocked.Exchange(ref _planMisses, 0),
            InPassClears: Interlocked.Exchange(ref _inPassClears, 0),
            PromotedClears: Interlocked.Exchange(ref _promotedClears, 0),
            StandaloneClears: Interlocked.Exchange(ref _standaloneClears, 0),
            PassSplits: Interlocked.Exchange(ref _passSplits, 0));

        double uploadMs = uploadTicks * 1000.0 / Stopwatch.Frequency;

        VulkanAllocator? memory = MemorySource;
        MemorySnapshot memorySnapshot = memory == null ? default : memory.Snapshot();

        return FormatIntervalLine(elapsed, frames, allocations, VulkanMemory.LiveAllocations,
                   uploads, uploadMs, created, deleted, dropped, overflows) + "\n" +
               FormatPacingLine(FrameIntervals.Snapshot()) + "\n" +
               FormatWaitsLine(waitCounts, waitMs) + "\n" +
               FormatCountersLine(counters) + "\n" +
               LatencyLine(waitCounts[(int)WaitSite.LatencySleep], waitMs[(int)WaitSite.LatencySleep]) + "\n" +
               VulkanAllocator.FormatMemoryLine(memorySnapshot) + "\n" +
               FormatTransientsLine(new TransientSample(
                   TransientBytes: (ulong)Interlocked.Read(ref _transientBytes),
                   AliasedBytes: (ulong)Interlocked.Exchange(ref _aliasedBytesPeak, 0),
                   HeapPeakBytes: memory?.TakeTransientHeapPeak() ?? 0,
                   Leases: Interlocked.Exchange(ref _transientLeases, 0),
                   AliasedLeases: Interlocked.Exchange(ref _aliasedLeases, 0),
                   ReadSelfCopies: Interlocked.Exchange(ref _readSelfCopies, 0),
                   ReadSelfPool: Interlocked.Read(ref _readSelfPool)));
    }

    private static double Mib(ulong bytes) => bytes / (1024.0 * 1024.0);

    /// <summary>
    /// <c>stats.transients</c>: transient image MiB at the last frame boundary, the
    /// interval's largest aliased MiB in one frame, the Transient pool class's peak
    /// block MiB, leases and aliased leases over the interval, ReadSelf copies taken
    /// over the interval and the copies the pool holds.
    /// </summary>
    public static string FormatTransientsLine(TransientSample sample) =>
        string.Format(CultureInfo.InvariantCulture,
            "stats.transients transient_mib={0:F1} aliased_mib={1:F1} heap_peak_mib={2:F1} leases={3} " +
            "aliased_leases={4} readself_copies={5} readself_pool={6}",
            Mib(sample.TransientBytes), Mib(sample.AliasedBytes), Mib(sample.HeapPeakBytes), sample.Leases,
            sample.AliasedLeases, sample.ReadSelfCopies, sample.ReadSelfPool);

    /// <summary>
    /// The allocator whose pool classes and heaps the <c>stats.memory</c> line
    /// reports; the device sets it at init and clears it at dispose.
    /// </summary>
    public static volatile VulkanAllocator? MemorySource;

    /// <summary>
    /// The latency backend the <c>stats.latency</c> line reports (seam S7); the
    /// device sets it whenever the active backend changes and clears it at
    /// dispose. Null means the line still appears, with the "off" backend and no
    /// frames - the line is always present so a parser never has to branch.
    /// </summary>
    public static volatile ILatencyBackend? LatencySource;

    /// <summary>
    /// The eight intervals of <see cref="LatencyFrameReport" />, in the order they
    /// appear on the <c>stats.latency</c> line.
    /// </summary>
    public static readonly string[] LatencyIntervalTokens =
    {
        "input",
        "sim",
        "render_submit",
        "present",
        "driver",
        "os_queue",
        "gpu",
        "total",
    };

    public const int LatencyIntervalCount = 8;

    /// <summary>The intervals of one report, in <see cref="LatencyIntervalTokens" /> order, in microseconds.</summary>
    private static void IntervalsOf(in LatencyFrameReport report, ulong[] into)
    {
        into[0] = report.InputUs;
        into[1] = report.SimulationUs;
        into[2] = report.RenderSubmitUs;
        into[3] = report.PresentUs;
        into[4] = report.DriverUs;
        into[5] = report.OsRenderQueueUs;
        into[6] = report.GpuUs;
        into[7] = report.TotalUs;
    }

    /// <summary>
    /// Reduces the frame reports of one interval to a mean and a p99 per interval,
    /// in milliseconds - the same reduction <see cref="FramePacingSnapshot" />
    /// applies to frame intervals, and the same nearest-rank percentile
    /// (index ceil(0.99 * n) - 1), so the two lines are read the same way.
    /// </summary>
    public static void ReduceReports(LatencyFrameReport[] reports, double[] meanMs, double[] p99Ms)
    {
        for (int i = 0; i < LatencyIntervalCount; i++)
        {
            meanMs[i] = 0;
            p99Ms[i] = 0;
        }
        if (reports.Length == 0) return;

        var values = new double[LatencyIntervalCount][];
        for (int i = 0; i < LatencyIntervalCount; i++) values[i] = new double[reports.Length];

        var scratch = new ulong[LatencyIntervalCount];
        for (int r = 0; r < reports.Length; r++)
        {
            IntervalsOf(reports[r], scratch);
            for (int i = 0; i < LatencyIntervalCount; i++) values[i][r] = scratch[i] / 1000.0;
        }

        for (int i = 0; i < LatencyIntervalCount; i++)
        {
            double sum = 0;
            for (int r = 0; r < reports.Length; r++) sum += values[i][r];
            meanMs[i] = sum / reports.Length;

            Array.Sort(values[i]);
            int index = (int)Math.Ceiling(0.99 * reports.Length) - 1;
            if (index < 0) index = 0;
            if (index > reports.Length - 1) index = reports.Length - 1;
            p99Ms[i] = values[i][index];
        }
    }

    /// <summary>
    /// <c>stats.latency</c> (seam S7): which backend is active, the mode asked of
    /// it, the sleep it made in the interval, and each report interval reduced to
    /// a mean and a p99. Always emitted, so "off" is as visible as "on".
    /// </summary>
    public static string FormatLatencyLine(string backend, string mode, long sleepCount, double sleepMs,
        int frames, double[] meanMs, double[] p99Ms)
    {
        var line = new StringBuilder("stats.latency backend=");
        line.Append(backend).Append(" mode=").Append(mode);
        line.Append(" sleep_n=").Append(sleepCount.ToString(CultureInfo.InvariantCulture));
        line.Append(" sleep_ms=").Append(sleepMs.ToString("F1", CultureInfo.InvariantCulture));
        line.Append(" frames=").Append(frames.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < LatencyIntervalCount; i++)
        {
            line.Append(' ').Append(LatencyIntervalTokens[i]).Append("_mean_ms=")
                .Append(meanMs[i].ToString("F2", CultureInfo.InvariantCulture));
            line.Append(' ').Append(LatencyIntervalTokens[i]).Append("_p99_ms=")
                .Append(p99Ms[i].ToString("F2", CultureInfo.InvariantCulture));
        }
        return line.ToString();
    }

    /// <summary>The <c>stats.latency</c> line for the backend currently set as <see cref="LatencySource" />.</summary>
    private static string LatencyLine(long sleepCount, double sleepMs)
    {
        ILatencyBackend? latency = LatencySource;
        LatencyFrameReport[] reports = latency == null
            ? Array.Empty<LatencyFrameReport>()
            : latency.TakeReports();

        var meanMs = new double[LatencyIntervalCount];
        var p99Ms = new double[LatencyIntervalCount];
        ReduceReports(reports, meanMs, p99Ms);

        string backend = LatencyBackends.Token(latency == null ? LatencyBackendKind.None : latency.Kind);
        string mode = latency == null ? "off" : ModeToken(latency.Settings.Mode);
        return FormatLatencyLine(backend, mode, sleepCount, sleepMs, reports.Length, meanMs, p99Ms);
    }

    /// <summary>The token of one <see cref="LatencyMode" /> on the stats line.</summary>
    public static string ModeToken(LatencyMode mode) => mode switch
    {
        LatencyMode.On => "on",
        LatencyMode.Boost => "boost",
        _ => "off",
    };

    /// <summary>The original stats line. Its format must not change.</summary>
    public static string FormatIntervalLine(double elapsed, long frames, long allocations, int liveAllocations,
        long uploads, double uploadMs, long created, long deleted, long dropped, long overflows)
    {
        double frameMs = frames > 0 ? elapsed * 1000.0 / frames : 0;

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "stats {0:F1}s: {1} frames ({2:F1} ms/frame), {3} allocations ({4} live), " +
            "{5} blocking uploads costing {6:F0} ms ({7:F0}% of the interval), " +
            "textures +{8}/-{9}, mesh writes dropped {10}, uniform overflows {11}",
            elapsed, frames, frameMs, allocations, liveAllocations,
            uploads, uploadMs, uploadMs / (elapsed * 1000.0) * 100.0, created, deleted, dropped, overflows);
    }

    public static string FormatPacingLine(FramePacingSnapshot pacing) =>
        string.Format(CultureInfo.InvariantCulture,
            "stats.pacing samples={0} p50_ms={1:F3} p95_ms={2:F3} p99_ms={3:F3} stddev_ms={4:F3} stutters={5}",
            pacing.Samples, pacing.P50, pacing.P95, pacing.P99, pacing.StdDev, pacing.Stutters);

    public static string FormatWaitsLine(long[] counts, double[] milliseconds)
    {
        var line = new StringBuilder("stats.waits");
        for (int i = 0; i < WaitSiteCount; i++)
        {
            line.Append(' ').Append(WaitSiteTokens[i]).Append("_n=")
                .Append(counts[i].ToString(CultureInfo.InvariantCulture));
            line.Append(' ').Append(WaitSiteTokens[i]).Append("_ms=")
                .Append(milliseconds[i].ToString("F1", CultureInfo.InvariantCulture));
        }
        return line.ToString();
    }

    public static string FormatCountersLine(CounterSample counters) =>
        string.Format(CultureInfo.InvariantCulture,
            "stats.counters blocking_uploads={0} uploads={1} scopes={2} barriers={3} rebar_fallbacks={4} " +
            "dynamic_state={5} uniform_ring_used={6} uniform_ring_capacity={7} " +
            "barrier_commands={8} barriers_per_frame={9:F1} mask_restarts={10} feedback_splits={11} " +
            "passes={12} plan_hits={13} plan_misses={14} in_pass_clears={15} promoted_clears={16} " +
            "standalone_clears={17} pass_splits={18}",
            counters.BlockingUploads, counters.Uploads, counters.Scopes, counters.Barriers,
            counters.RebarFallbacks, counters.DynamicState, counters.UniformRingUsed, counters.UniformRingCapacity,
            counters.BarrierCommands, counters.Frames > 0 ? counters.Barriers / (double)counters.Frames : 0.0,
            counters.MaskRestarts, counters.FeedbackSplits, counters.Passes, counters.PlanHits, counters.PlanMisses,
            counters.InPassClears, counters.PromotedClears, counters.StandaloneClears, counters.PassSplits);

    private static long _lastSample;
}

/// <summary>The per-interval counters on the <c>stats.counters</c> line.</summary>
internal readonly record struct CounterSample(
    long BlockingUploads,
    long Uploads,
    long Scopes,
    long Barriers,
    long RebarFallbacks,
    long DynamicState,
    long UniformRingUsed,
    long UniformRingCapacity,
    long BarrierCommands = 0,
    long Frames = 0,
    long MaskRestarts = 0,
    long FeedbackSplits = 0,
    long Passes = 0,
    long PlanHits = 0,
    long PlanMisses = 0,
    long InPassClears = 0,
    long PromotedClears = 0,
    long StandaloneClears = 0,
    long PassSplits = 0);

/// <summary>The values on the <c>stats.transients</c> line.</summary>
internal readonly record struct TransientSample(
    ulong TransientBytes,
    ulong AliasedBytes,
    ulong HeapPeakBytes,
    long Leases,
    long AliasedLeases,
    long ReadSelfCopies,
    long ReadSelfPool);

/// <summary>Percentiles and spread of the frame-interval ring at one moment.</summary>
internal readonly record struct FramePacingSnapshot(
    int Samples, double P50, double P95, double P99, double StdDev, int Stutters);

/// <summary>
/// The last N CPU frame intervals, for p50/p95/p99, standard deviation and the
/// stutter count (intervals above 2 x p50).
///
/// Both arrays are allocated once; adding a frame is a store under a lock, and a
/// snapshot sorts into the preallocated scratch array, so neither allocates.
/// Percentiles are nearest-rank (index ceil(p * n) - 1), the same rule the
/// client's OPTIMUM_FPS_LOG uses for p99, so the two logs are comparable.
/// </summary>
internal sealed class FrameIntervalRing
{
    public const int DefaultCapacity = 512;

    private readonly double[] _values;
    private readonly double[] _scratch;
    private readonly object _lock = new();
    private int _next;
    private int _count;

    public FrameIntervalRing(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _values = new double[capacity];
        _scratch = new double[capacity];
    }

    public int Capacity => _values.Length;

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    public void Add(double milliseconds)
    {
        if (!(milliseconds >= 0) || double.IsInfinity(milliseconds)) return;
        lock (_lock)
        {
            _values[_next] = milliseconds;
            _next = (_next + 1) % _values.Length;
            if (_count < _values.Length) _count++;
        }
    }

    public FramePacingSnapshot Snapshot()
    {
        lock (_lock)
        {
            int n = _count;
            if (n == 0) return default;

            // The live values are the first n slots until the ring wraps, and all
            // of them afterwards; order does not matter once they are sorted.
            Array.Copy(_values, _scratch, n);
            Array.Sort(_scratch, 0, n);

            double sum = 0;
            for (int i = 0; i < n; i++) sum += _scratch[i];
            double mean = sum / n;
            double squares = 0;
            for (int i = 0; i < n; i++)
            {
                double delta = _scratch[i] - mean;
                squares += delta * delta;
            }

            double p50 = NearestRank(_scratch, n, 0.50);
            int stutters = 0;
            for (int i = n - 1; i >= 0 && _scratch[i] > 2.0 * p50; i--) stutters++;

            return new FramePacingSnapshot(n, p50, NearestRank(_scratch, n, 0.95),
                NearestRank(_scratch, n, 0.99), Math.Sqrt(squares / n), stutters);
        }
    }

    private static double NearestRank(double[] sorted, int n, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * n) - 1;
        if (index < 0) index = 0;
        if (index > n - 1) index = n - 1;
        return sorted[index];
    }
}
