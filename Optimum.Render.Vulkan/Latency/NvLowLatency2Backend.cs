using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// NVIDIA Reflex through the driver extension VK_NV_low_latency2 - never the
/// Reflex SDK DLL and never Streamline, so nothing is shipped beside the client
/// and nothing is signed by a vendor.
///
/// The frame, seam by seam (plan section "Latency seams"):
/// <list type="bullet">
/// <item><description><see cref="Apply" /> and <see cref="OnSwapchainCreated" />:
/// <c>vkSetLatencySleepModeNV</c> with lowLatencyMode, lowLatencyBoost and
/// minimumIntervalUs. That call is also the frame cap, which is why
/// <see cref="OwnsFrameCap" /> is true whenever the mode is not Off; the client's
/// own limiter stands down then (lib seam S3).</description></item>
/// <item><description><see cref="Sleep" />: <c>vkLatencySleepNV</c> followed by a
/// wait on the timeline semaphore this backend owns, whose value increases by one
/// per frame. The driver signals it when the frame may start.</description></item>
/// <item><description><see cref="Marker" />: <c>vkSetLatencyMarkerNV</c> with
/// presentID set to the present id this frame will present with, so the driver's
/// report can be matched back to the frame.</description></item>
/// <item><description><see cref="TakeReports" />: <c>vkGetLatencyTimingsNV</c>,
/// drained into <see cref="LatencyFrameReport" /> for the stats line (seam S7).
/// The driver hands back the last up-to-64 frames on every call, so only reports
/// newer than the last drained present id are returned.</description></item>
/// <item><description><see cref="SwapchainCreateChain" />: chains
/// <c>VkSwapchainLatencyCreateInfoNV</c> onto every swapchain creation (seam S5),
/// and <see cref="OnSwapchainCreated" /> re-applies the sleep mode afterwards,
/// because nothing carries over through oldSwapchain.</description></item>
/// <item><description><see cref="TagSubmit" />: <c>VkLatencySubmissionPresentIdNV</c>
/// exists only from extension revision 3, and then has to be on every submit of
/// the frame or none of them. The driver on this machine (615.71.09) advertises
/// revision 2, so the code path is here behind the revision check and stays
/// off.</description></item>
/// </list>
///
/// Everything but <see cref="TakeReports" /> runs on the one client thread;
/// TakeReports is the stats sample and takes the report lock.
/// </summary>
internal sealed unsafe class NvLowLatency2Backend : ILatencyBackend
{
    /// <summary>The driver never returns more than this many timing reports per call.</summary>
    private const uint MaxTimings = 64;

    /// <summary>
    /// One <c>VkLatencySubmissionPresentIdNV</c> per frame that can be in flight;
    /// a tag handed to a submit has to stay valid until that submit was made.
    /// </summary>
    private const int SubmitTagSlots = 8;

    /// <summary>A frame's sleep never legitimately takes this long; a longer wait is a bug, not pacing.</summary>
    private const ulong SleepTimeoutNs = 1_000_000_000UL;

    private readonly VulkanContext _context;
    private readonly NvLowLatency2Functions _functions;
    private readonly Action<string>? _log;

    /// <summary>The driver's advertised revision; per-submit tagging needs 3.</summary>
    private readonly uint _specVersion;

    /// <summary>The semaphore <c>vkLatencySleepNV</c> signals; owned here, one value per frame.</summary>
    private readonly Semaphore _sleepSemaphore;

    /// <summary>The value the next sleep asks for; increases by one per sleep.</summary>
    private ulong _sleepValue;

    /// <summary>The swapchain every call is made against; default until the first creation.</summary>
    private SwapchainKHR _swapchain;

    /// <summary>The create-info struct chained on every swapchain creation, in stable memory.</summary>
    private readonly SwapchainLatencyCreateInfoNV* _swapchainCreateInfo;

    /// <summary>The submit tags, in stable memory; only used at revision 3 and up.</summary>
    private readonly LatencySubmissionPresentIdNV* _submitTags;

    /// <summary>The timing array handed to <c>vkGetLatencyTimingsNV</c>, in stable memory.</summary>
    private readonly LatencyTimingsFrameReportNV* _timings;

    /// <summary>Which frame produced which present id, for the driver's reports.</summary>
    private readonly PresentIdMap _presentIds = new();

    /// <summary>The reports are drained by the stats sample, which is not the frame thread.</summary>
    private readonly object _reportLock = new();

    /// <summary>The newest present id already returned by <see cref="TakeReports" />.</summary>
    private ulong _drainedPresentId;

    /// <summary>The frame currently stamping markers, and the present id predicted for it.</summary>
    private ulong _markerFrameId;
    private ulong _markerPresentId;

    /// <summary>Failures are logged once each: a broken frame repeats every frame.</summary>
    private bool _sleepFailureLogged;
    private bool _sleepTimeoutLogged;
    private bool _modeFailureLogged;
    private bool _presentIdMismatchLogged;
    private bool _disposed;

    /// <summary>How often the predicted present id was not the one the frame presented with.</summary>
    public int PresentIdMismatches { get; private set; }

    /// <summary>How many sleeps actually reached <c>vkLatencySleepNV</c>.</summary>
    public int SleepCount { get; private set; }

    /// <summary>How often the sleep mode was applied to a swapchain.</summary>
    public int SleepModeApplications { get; private set; }

    private NvLowLatency2Backend(
        VulkanContext context, NvLowLatency2Functions functions, uint specVersion, Action<string>? log)
    {
        _context = context;
        _functions = functions;
        _specVersion = specVersion;
        _log = log;

        _sleepSemaphore = CreateTimeline(context);

        _swapchainCreateInfo = (SwapchainLatencyCreateInfoNV*)Marshal.AllocHGlobal(sizeof(SwapchainLatencyCreateInfoNV));
        *_swapchainCreateInfo = new SwapchainLatencyCreateInfoNV
        {
            SType = StructureType.SwapchainLatencyCreateInfoNV,
            PNext = null,
            // The swapchain is created latency-capable whenever this backend is
            // the one running, so the mode can be turned on and off at runtime
            // without rebuilding it.
            LatencyModeEnable = true,
        };

        _submitTags = (LatencySubmissionPresentIdNV*)Marshal.AllocHGlobal(
            sizeof(LatencySubmissionPresentIdNV) * SubmitTagSlots);
        for (int i = 0; i < SubmitTagSlots; i++)
        {
            _submitTags[i] = new LatencySubmissionPresentIdNV
            {
                SType = StructureType.LatencySubmissionPresentIDNV,
                PNext = null,
                PresentID = 0,
            };
        }

        _timings = (LatencyTimingsFrameReportNV*)Marshal.AllocHGlobal(
            sizeof(LatencyTimingsFrameReportNV) * (int)MaxTimings);
    }

    /// <summary>
    /// Builds the backend for a device that enabled VK_NV_low_latency2. False when
    /// the entry points do not resolve, which is not an error: the caller then
    /// runs the backend below it, exactly as a refused extension request does.
    /// </summary>
    public static bool TryCreate(VulkanContext context, Action<string>? log, out NvLowLatency2Backend? backend)
    {
        backend = null;
        if (context == null) return false;

        NvLowLatency2Functions? functions = NvLowLatency2Functions.Load(context.Api, context.Device);
        if (functions == null)
        {
            log?.Invoke("latency: " + LatencyBackendSelector.NvLowLatency2ExtensionName +
                " is enabled but its entry points did not resolve; not using it");
            return false;
        }

        backend = new NvLowLatency2Backend(
            context, functions, context.Capabilities.LatencySupport.NvLowLatency2SpecVersion, log);
        return true;
    }

    public LatencyBackendKind Kind => LatencyBackendKind.NvLowLatency2;

    public LatencySettings Settings { get; private set; } = LatencySettings.Disabled;

    /// <summary>
    /// True whenever the mode is not Off: <c>vkSetLatencySleepModeNV</c>'s
    /// minimumIntervalUs is the frame cap, so the client's own limiter must stand
    /// down or the two would pace against each other.
    /// </summary>
    public bool OwnsFrameCap => Settings.Mode != LatencyMode.Off;

    /// <summary>
    /// Whether per-submit attribution is honoured. <c>VkLatencySubmissionPresentIdNV</c>
    /// arrives at revision 3 and then has to tag every submit of a frame or none;
    /// below that the driver attributes by present alone.
    /// </summary>
    public bool PerSubmitAttribution => _specVersion >= LatencyBackendSelector.NvPerSubmitAttributionRevision;

    /// <summary>The hook for <c>VkSwapchainCreateInfoKHR.pNext</c> (seam S5).</summary>
    public SwapchainCreateChain SwapchainCreateChain => ChainSwapchainCreateInfo;

    private void* ChainSwapchainCreateInfo(void* pNext)
    {
        _swapchainCreateInfo->PNext = pNext;
        return _swapchainCreateInfo;
    }

    public void Apply(in LatencySettings settings)
    {
        Settings = settings;
        ApplySleepMode();
    }

    /// <summary>
    /// A new swapchain exists. The sleep mode is per swapchain and nothing carries
    /// over through oldSwapchain, so it is set again here - on the first creation
    /// exactly as on every resize, vsync toggle, OUT_OF_DATE rebuild and
    /// FIFO_RELAXED promotion.
    /// </summary>
    public void OnSwapchainCreated(SwapchainKHR swapchain)
    {
        _swapchain = swapchain;
        ApplySleepMode();
    }

    private void ApplySleepMode()
    {
        if (_disposed || _swapchain.Handle == 0) return;

        var info = new LatencySleepModeInfoNV
        {
            SType = StructureType.LatencySleepModeInfoNV,
            PNext = null,
            LowLatencyMode = Settings.Enabled,
            LowLatencyBoost = Settings.Boost,
            // The cap lives in the same call: 0 is uncapped, and the driver reads
            // it as the minimum interval between two frame starts.
            MinimumIntervalUs = Settings.MinimumIntervalUs > uint.MaxValue
                ? uint.MaxValue
                : (uint)Settings.MinimumIntervalUs,
        };

        Result result = _functions.SetLatencySleepMode(_context.Device, _swapchain, ref info);
        if (result != Result.Success)
        {
            if (!_modeFailureLogged)
            {
                _modeFailureLogged = true;
                _log?.Invoke("latency: vkSetLatencySleepModeNV failed with " + result +
                    "; reported once, however often it happens");
            }
            return;
        }

        SleepModeApplications++;
    }

    /// <summary>
    /// The frame's one sleep: <c>vkLatencySleepNV</c> asks the driver to signal
    /// the next value of this backend's timeline semaphore, and the wait on that
    /// value is the sleep itself.
    /// </summary>
    public ulong Sleep(ulong frameId)
    {
        // The frame's markers are stamped against the present id it will present
        // with, so the id is settled here, at the start of the frame.
        BeginMarkerFrame(frameId);

        if (_disposed || !Settings.Enabled || _swapchain.Handle == 0) return 0;

        ulong value = _sleepValue + 1;
        var info = new LatencySleepInfoNV
        {
            SType = StructureType.LatencySleepInfoNV,
            PNext = null,
            SignalSemaphore = _sleepSemaphore,
            Value = value,
        };

        Result result = _functions.LatencySleep(_context.Device, _swapchain, ref info);
        if (result != Result.Success)
        {
            // The value was never handed to the driver, so it will never be
            // signalled: do not wait for it, and do not consume it.
            if (!_sleepFailureLogged)
            {
                _sleepFailureLogged = true;
                _log?.Invoke("latency: vkLatencySleepNV failed with " + result +
                    "; the frame is not paced. Reported once, however often it happens");
            }
            return 0;
        }

        _sleepValue = value;
        SleepCount++;
        return WaitForSleep(value);
    }

    private ulong WaitForSleep(ulong value)
    {
        Semaphore handle = _sleepSemaphore;
        ulong target = value;
        var wait = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &handle,
            PValues = &target,
        };

        long startUs = LatencyClock.NowUs();
        long waitStart = VulkanStats.WaitStart();
        Result result = _context.Api.WaitSemaphores(_context.Device, &wait, SleepTimeoutNs);
        VulkanStats.NoteWait(WaitSite.LatencySleep, waitStart);
        long endUs = LatencyClock.NowUs();

        if (result == Result.Timeout && !_sleepTimeoutLogged)
        {
            _sleepTimeoutLogged = true;
            _log?.Invoke("latency: the wait for vkLatencySleepNV's semaphore timed out after " +
                (SleepTimeoutNs / 1_000_000UL) + " ms. Reported once, however often it happens");
        }
        else if (result != Result.Success && result != Result.Timeout)
        {
            VulkanResult.Check(result, "vkWaitSemaphores on the latency sleep semaphore");
        }

        return endUs > startUs ? (ulong)(endUs - startUs) : 0UL;
    }

    /// <summary>
    /// Stamps one phase of the frame. The driver matches a marker to a frame by
    /// its presentID, so every marker of a frame carries the id that frame will
    /// present with (<see cref="BeginMarkerFrame" />).
    /// </summary>
    public void Marker(ulong frameId, LatencyMarker marker)
    {
        if (_disposed || !Settings.Enabled || _swapchain.Handle == 0) return;

        if (frameId != _markerFrameId) BeginMarkerFrame(frameId);

        var info = new SetLatencyMarkerInfoNV
        {
            SType = StructureType.SetLatencyMarkerInfoNV,
            PNext = null,
            PresentID = _markerPresentId,
            // LatencyMarker's values are VkLatencyMarkerNV's values; a unit test
            // pins every one of them, so this is a cast and not a table.
            Marker = (LatencyMarkerNV)marker,
        };
        _functions.SetLatencyMarker(_context.Device, _swapchain, ref info);
    }

    /// <summary>
    /// Settles the present id the frame's markers and submit tags use. One frame
    /// presents once, and present ids are handed out in order by
    /// <see cref="PresentIdCounter" />, so the next one is this frame's;
    /// <see cref="OnPresent" /> checks that prediction against what the frame
    /// really presented with and counts every miss.
    /// </summary>
    private void BeginMarkerFrame(ulong frameId)
    {
        _markerFrameId = frameId;
        _markerPresentId = PresentIdCounter.Current + 1;
    }

    /// <summary>
    /// The frame's per-submit tag. Only at revision 3 and up, and then on every
    /// submit of the frame - the extension makes tagging all-or-nothing across a
    /// frame, so a driver below that revision gets no tag at all and is attributed
    /// by present.
    /// </summary>
    public void* TagSubmit(ulong frameId, void* pNext)
    {
        if (_disposed || !PerSubmitAttribution || !Settings.Enabled) return pNext;

        LatencySubmissionPresentIdNV* tag = _submitTags + (int)(frameId % SubmitTagSlots);
        tag->SType = StructureType.LatencySubmissionPresentIDNV;
        tag->PresentID = frameId == _markerFrameId ? _markerPresentId : PresentIdCounter.Current + 1;
        tag->PNext = pNext;
        return tag;
    }

    /// <summary>
    /// The frame presented. The pairing is what turns the driver's reports, which
    /// are keyed by present id, back into frames.
    /// </summary>
    public void OnPresent(ulong frameId, ulong presentId)
    {
        if (presentId == 0) return;

        if (frameId == _markerFrameId && presentId != _markerPresentId)
        {
            PresentIdMismatches++;
            if (!_presentIdMismatchLogged)
            {
                _presentIdMismatchLogged = true;
                _log?.Invoke("latency: frame " + frameId + " stamped its markers with present id " +
                    _markerPresentId + " but presented with " + presentId +
                    "; its report will be attributed to the stamped id. Reported once, however often it happens");
            }
        }

        lock (_reportLock) _presentIds.Record(presentId, frameId);
    }

    /// <summary>
    /// Drains <c>vkGetLatencyTimingsNV</c>. The driver answers with the last
    /// up-to-64 frames every time, so the ones already handed out are filtered by
    /// present id; what comes back is oldest first, as the interface asks.
    /// </summary>
    public LatencyFrameReport[] TakeReports()
    {
        if (_disposed || _swapchain.Handle == 0) return Array.Empty<LatencyFrameReport>();

        lock (_reportLock)
        {
            // The two-call form the extension asks for: once with a null array to
            // learn how many reports there are, once to fill it.
            var count = new GetLatencyMarkerInfoNV
            {
                SType = StructureType.GetLatencyMarkerInfoNV,
                PNext = null,
                TimingCount = 0,
                PTimings = null,
            };
            _functions.GetLatencyTimings(_context.Device, _swapchain, ref count);

            uint available = count.TimingCount;
            if (available == 0) return Array.Empty<LatencyFrameReport>();
            if (available > MaxTimings) available = MaxTimings;

            // Every element of the array is an input structure too: the layers
            // reject a call whose pTimings[i].sType the application did not fill
            // in (VUID-VkLatencyTimingsFrameReportNV-sType-sType), and the driver
            // overwrites the rest.
            for (uint i = 0; i < available; i++)
            {
                _timings[i].SType = StructureType.LatencyTimingsFrameReportNV;
                _timings[i].PNext = null;
            }

            var fill = new GetLatencyMarkerInfoNV
            {
                SType = StructureType.GetLatencyMarkerInfoNV,
                PNext = null,
                TimingCount = available,
                PTimings = _timings,
            };
            _functions.GetLatencyTimings(_context.Device, _swapchain, ref fill);

            uint written = fill.TimingCount <= available ? fill.TimingCount : available;
            if (written == 0) return Array.Empty<LatencyFrameReport>();

            var reports = new List<LatencyFrameReport>((int)written);
            ulong newest = _drainedPresentId;
            for (uint i = 0; i < written; i++)
            {
                LatencyTimingsFrameReportNV timing = _timings[i];
                if (timing.PresentID <= _drainedPresentId) continue;
                if (timing.PresentID > newest) newest = timing.PresentID;

                _presentIds.TryGetFrameId(timing.PresentID, out ulong frameId);
                reports.Add(Convert(timing, frameId));
            }

            _drainedPresentId = newest;
            return reports.Count == 0 ? Array.Empty<LatencyFrameReport>() : reports.ToArray();
        }
    }

    /// <summary>
    /// One driver report as the renderer's own shape. Every field of
    /// <see cref="LatencyFrameReport" /> is filled, including the three a
    /// CPU-timestamp backend has to leave at zero; TotalUs keeps the interface's
    /// meaning (input sample to present end), with the GPU's own span in GpuUs.
    /// </summary>
    private static LatencyFrameReport Convert(in LatencyTimingsFrameReportNV timing, ulong frameId) =>
        new(
            frameId,
            timing.PresentID,
            Span(timing.InputSampleTimeUs, timing.SimStartTimeUs),
            Span(timing.SimStartTimeUs, timing.SimEndTimeUs),
            Span(timing.RenderSubmitStartTimeUs, timing.RenderSubmitEndTimeUs),
            Span(timing.PresentStartTimeUs, timing.PresentEndTimeUs),
            Span(timing.DriverStartTimeUs, timing.DriverEndTimeUs),
            Span(timing.OsRenderQueueStartTimeUs, timing.OsRenderQueueEndTimeUs),
            Span(timing.GpuRenderStartTimeUs, timing.GpuRenderEndTimeUs),
            Span(timing.InputSampleTimeUs != 0 ? timing.InputSampleTimeUs : timing.SimStartTimeUs,
                timing.PresentEndTimeUs));

    /// <summary>0 when either end is missing or the pair is out of order.</summary>
    private static ulong Span(ulong fromUs, ulong toUs) =>
        fromUs == 0 || toUs == 0 || toUs <= fromUs ? 0UL : toUs - fromUs;

    private static Semaphore CreateTimeline(VulkanContext context)
    {
        var type = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };
        var info = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &type,
        };
        Semaphore semaphore;
        VulkanResult.Check(context.Api.CreateSemaphore(context.Device, &info, null, &semaphore),
            "vkCreateSemaphore for the latency sleep semaphore");
        return semaphore;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // A sleep the driver still owes a signal for would leave the semaphore
        // in use; the device is idle by the time a backend is disposed, because
        // the device tears the frame ring down first.
        if (_sleepSemaphore.Handle != 0) _context.Api.DestroySemaphore(_context.Device, _sleepSemaphore, null);
        Marshal.FreeHGlobal((nint)_swapchainCreateInfo);
        Marshal.FreeHGlobal((nint)_submitTags);
        Marshal.FreeHGlobal((nint)_timings);
    }
}
