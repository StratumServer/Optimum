using System;
using System.Collections.Generic;
using System.Threading;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// "Completion pacing": the vendor-neutral latency backend, the tier that works
/// on every GPU, every driver and both operating systems (plan section "Latency
/// seams", backend <c>Native</c>).
///
/// It is the algorithm the Korthos <c>low_latency_layer</c> measured as matching
/// or beating the proprietary implementations, done inside the renderer instead
/// of in a layer, because we own the frame timeline and therefore need neither an
/// interposed layer nor timestamp queries:
///
/// <list type="number">
/// <item><description>The frame's one wait moves in front of the input sample
/// (<c>VulkanClientPlatform.LatencySleep</c>, seam S3). It waits on the Frame
/// timeline until the <em>previous</em> frame's present submission (Submit B) has
/// completed on the GPU - a real <c>vkWaitSemaphores</c>, never a spin on a
/// stopwatch. That is the whole latency win: the queued frames the client used to
/// accumulate before <c>FrameRing.BeginFrame</c> are gone, so the input the player
/// gives is sampled against a GPU that has just finished, not one that is two
/// frames behind.</description></item>
/// <item><description>Then, if a frame cap is set, it holds until
/// <see cref="LatencySettings.MinimumIntervalUs" /> has passed since the previous
/// release. The cap is measured release to release - the point the sleep hands
/// control back, which is the frame's real start - so the interval the player
/// sees is the interval that is capped, and jitter in any later phase of the
/// frame does not accumulate.</description></item>
/// </list>
///
/// Because the pacing point is now the sleep, the ring's own pacing wait
/// (<c>FrameRing.BeginFrame</c>, <see cref="WaitSite.FramePacing" />) finds its
/// value already signalled and drops to near zero; that is the plan's acceptance
/// number for this backend, and <c>NativeLatencyBackendTests</c> reports it.
///
/// Cost, stated honestly: the GPU idles while the CPU records the next frame.
/// This trades throughput for latency, which is what every latency technology
/// does; it is why the mode ships off by default.
///
/// Reports are the CPU marker timestamps (<see cref="LatencyPhaseTracker" />)
/// plus the completion this backend observes for free: when the sleep's timeline
/// wait returns, the previous frame's GPU work is known to be done, so that
/// frame's <see cref="LatencyFrameReport.GpuUs" /> is filled in from present
/// submission to observed completion without a single timestamp query.
/// </summary>
internal sealed class NativeLatencyBackend : ILatencyBackend
{
    /// <summary>
    /// The tail of the frame cap that is spun rather than slept, in microseconds.
    ///
    /// <see cref="Thread.Sleep(int)" /> has millisecond granularity and routinely
    /// overshoots by a whole scheduler tick, which would show up as pacing jitter
    /// exactly where this backend is supposed to remove it. So the hold sleeps
    /// down to this much of the target and spins the rest. The spin is bounded by
    /// construction: it can only ever run over the last millisecond of a hold that
    /// is itself bounded by <see cref="MaxHoldUs" />, and it yields the core
    /// (<see cref="Thread.SpinWait" />) rather than burning it.
    /// </summary>
    public const long SpinTailUs = 1_000;

    /// <summary>
    /// The longest the cap will ever hold, in microseconds (100 ms). A hold is
    /// computed from the previous release timestamp; a stalled frame, a suspended
    /// process or a clock that jumped can make that arbitrarily stale, and the
    /// answer to "the cap says wait a minute" is to release now and re-anchor, not
    /// to freeze the client.
    /// </summary>
    public const long MaxHoldUs = 100_000;

    /// <summary>
    /// The Frame timeline, fetched on use rather than captured: the backend is
    /// installed before the frame ring exists (<c>VulkanDevice</c> installs the
    /// selected backend while the device comes up, so the ring and the first
    /// swapchain both see the same instance).
    /// </summary>
    private readonly Func<FrameTimeline?> _timeline;

    private readonly LatencyPhaseTracker _tracker;
    private readonly List<LatencyFrameReport> _reports = new();

    /// <summary>Reports are drained by the stats sample, which is not the frame thread.</summary>
    private readonly object _reportLock = new();

    /// <summary>A frame's worth of reports is plenty; a client that never samples must not grow this.</summary>
    private const int MaxReports = 256;

    public NativeLatencyBackend(Func<FrameTimeline?> timeline, Action<string>? log = null)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _tracker = new LatencyPhaseTracker(log);
    }

    public LatencyBackendKind Kind => LatencyBackendKind.Native;

    public LatencySettings Settings { get; private set; } = LatencySettings.Disabled;

    /// <summary>
    /// Mode and cap. Turning the backend off drops the cap anchor as well, so a
    /// later re-enable paces from the first frame after it rather than from a
    /// release that happened minutes ago.
    /// </summary>
    public void Apply(in LatencySettings settings)
    {
        Settings = settings;
        if (!settings.Enabled) _lastReleaseUs = 0;
    }

    /// <summary>
    /// True whenever the mode is not Off: the sleep is the pacing point, so the
    /// client's own FPS limiter must stand down (seam S3) - two limiters would
    /// fight, and the client's runs after the input sample, which is the latency
    /// this backend exists to remove.
    /// </summary>
    public bool OwnsFrameCap => Settings.Enabled;

    // ------------------------------------------------------------------ state

    /// <summary>The Frame timeline value the last present submission (Submit B) signals.</summary>
    private ulong _lastPresentValue;

    /// <summary>The frame id that submission belonged to.</summary>
    private ulong _lastPresentFrameId;

    /// <summary>When that present was submitted, for the observed GPU interval.</summary>
    private long _lastPresentUs;

    /// <summary>The previous release of the sleep; the anchor the cap measures from.</summary>
    private long _lastReleaseUs;

    /// <summary>The Frame timeline value the last sleep waited for; for the tests.</summary>
    internal ulong LastWaitedValue { get; private set; }

    /// <summary>The frame id whose completion the last sleep waited for; 0 when it waited for none.</summary>
    internal ulong LastWaitedFrameId { get; private set; }

    /// <summary>The value the next sleep will wait for (the newest present submission).</summary>
    internal ulong PendingPresentValue => _lastPresentValue;

    /// <summary>The clock reading at which the last sleep released the frame.</summary>
    internal long LastReleaseUs => _lastReleaseUs;

    /// <summary>How many sleeps actually waited on the timeline.</summary>
    internal int TimelineWaits { get; private set; }

    /// <summary>
    /// The Frame value the GPU has finished, read through the same timeline the
    /// sleep waits on. The acceptance assert is against this, never a wall clock.
    /// </summary>
    internal ulong TimelineCompleted
    {
        get
        {
            FrameTimeline? timeline = _timeline();
            return timeline == null ? 0UL : timeline.FrameCompleted;
        }
    }

    // ------------------------------------------------------------------ sleep

    /// <summary>
    /// The frame's one wait, immediately before the client samples input.
    ///
    /// Off is off: with <see cref="LatencyMode.Off" /> this returns without
    /// touching the timeline or the clock, so a disabled Native backend is the
    /// same frame the None backend produces.
    /// </summary>
    public ulong Sleep(ulong frameId)
    {
        // Cleared first, so "what did the last sleep wait for" answers for this
        // sleep and never for an older one - including when this sleep is the
        // no-op of a disabled backend.
        LastWaitedValue = 0;
        LastWaitedFrameId = 0;

        if (!Settings.Enabled) return 0;

        long start = LatencyClock.NowUs();

        // 1. Completion. The previous frame's present submission is the newest
        //    Frame value of that frame, so waiting for it waits for all of it.
        //    FrameTimeline clamps to what was actually signalled, so a value that
        //    no submission carries (a frame that failed to acquire) cannot hang.
        FrameTimeline? timeline = _timeline();
        if (timeline != null && _lastPresentValue != 0)
        {
            LastWaitedValue = _lastPresentValue;
            LastWaitedFrameId = _lastPresentFrameId;
            TimelineWaits++;
            timeline.WaitForFrame(_lastPresentValue, WaitSite.LatencySleep);
            NoteCompletionObserved(LatencyClock.NowUs());
        }

        // 2. Cap, measured release to release.
        if (Settings.MinimumIntervalUs != 0 && _lastReleaseUs != 0)
        {
            HoldUntil(_lastReleaseUs + (long)Settings.MinimumIntervalUs);
        }

        long release = LatencyClock.NowUs();
        _lastReleaseUs = release;
        return release > start ? (ulong)(release - start) : 0;
    }

    /// <summary>
    /// Holds until <paramref name="targetUs" />: coarse sleeping down to
    /// <see cref="SpinTailUs" /> of the target, then the bounded spin tail. A
    /// target further away than <see cref="MaxHoldUs" /> is treated as a stale
    /// anchor and not waited for at all.
    /// </summary>
    private static void HoldUntil(long targetUs)
    {
        long now = LatencyClock.NowUs();
        if (targetUs <= now) return;
        if (targetUs - now > MaxHoldUs) return;

        while (targetUs - now > SpinTailUs)
        {
            int ms = (int)((targetUs - now - SpinTailUs) / 1000);
            if (ms <= 0) break;
            Thread.Sleep(ms);
            now = LatencyClock.NowUs();
        }

        // The tail: at most SpinTailUs of the hold, and only ever the tail.
        while (LatencyClock.NowUs() < targetUs) Thread.SpinWait(64);
    }

    /// <summary>
    /// The sleep's timeline wait just returned, so the frame whose present
    /// submission it waited for is finished on the GPU. Fill that frame's report
    /// in, if it is still in the queue - the stats sample may already have taken
    /// it, in which case the interval stays 0 rather than being guessed at.
    /// </summary>
    private void NoteCompletionObserved(long completedUs)
    {
        if (_lastPresentUs <= 0 || completedUs <= _lastPresentUs) return;
        var gpuUs = (ulong)(completedUs - _lastPresentUs);

        lock (_reportLock)
        {
            for (int i = _reports.Count - 1; i >= 0; i--)
            {
                if (_reports[i].FrameId != _lastPresentFrameId) continue;
                _reports[i] = _reports[i] with { GpuUs = gpuUs };
                return;
            }
        }
    }

    // ---------------------------------------------------------------- markers

    public void Marker(ulong frameId, LatencyMarker marker) =>
        _tracker.Mark(frameId, marker, LatencyClock.NowUs());

    /// <summary>
    /// Nothing to re-apply: this backend holds no driver-side sleep mode, and the
    /// Frame timeline outlives every swapchain, so the pacing value stays valid
    /// across a resize, a vsync toggle and an OUT_OF_DATE rebuild.
    /// </summary>
    public void OnSwapchainCreated(SwapchainKHR swapchain)
    {
    }

    /// <summary>Adds nothing to the submit chain; per-submit attribution is an NV concept.</summary>
    public unsafe void* TagSubmit(ulong frameId, void* pNext) => pNext;

    /// <summary>
    /// The frame has been presented. Two things happen here: the frame's report
    /// closes, and the Frame value that present submission carries is remembered
    /// as what the next sleep waits for. <c>FrameSignalled</c> is read rather than
    /// passed in because Submit B is the newest submission of the frame at this
    /// point by construction - the present command buffer is the last thing the
    /// frame submits.
    /// </summary>
    public void OnPresent(ulong frameId, ulong presentId)
    {
        if (_tracker.TryComplete(frameId, presentId, out LatencyFrameReport report))
        {
            lock (_reportLock)
            {
                if (_reports.Count >= MaxReports) _reports.RemoveAt(0);
                _reports.Add(report);
            }
        }

        FrameTimeline? timeline = _timeline();
        if (timeline == null) return;

        ulong signalled = timeline.FrameSignalled;
        if (signalled == 0) return;

        _lastPresentValue = signalled;
        _lastPresentFrameId = frameId;
        _lastPresentUs = LatencyClock.NowUs();
    }

    public LatencyFrameReport[] TakeReports()
    {
        lock (_reportLock)
        {
            if (_reports.Count == 0) return Array.Empty<LatencyFrameReport>();
            LatencyFrameReport[] taken = _reports.ToArray();
            _reports.Clear();
            return taken;
        }
    }

    public void Dispose()
    {
    }
}
