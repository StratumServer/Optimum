using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// One recorded <c>vkAntiLagUpdateAMD</c> call, kept so the GPU test can assert
/// the INPUT/PRESENT pairing and the "mode only on change" rule without
/// intercepting the driver.
/// </summary>
/// <param name="FrameId">The frame index the call carried; 0 for a mode-only call.</param>
/// <param name="Stage">INPUT or PRESENT; meaningless when <paramref name="ModeOnly" /> is true.</param>
/// <param name="Mode">The mode the call carried.</param>
/// <param name="MaxFps">The frame cap the call carried; 0 is uncapped.</param>
/// <param name="ModeOnly">True when the call had no presentation info, i.e. it only set the mode.</param>
internal readonly record struct AmdAntiLagCall(
    ulong FrameId, AntiLagStageAMD Stage, AntiLagModeAMD Mode, uint MaxFps, bool ModeOnly);

/// <summary>
/// VK_AMD_anti_lag, the whole extension being one entry point
/// (<see cref="AmdAntiLagFunctions" />, <c>vkAntiLagUpdateAMD</c>).
///
/// The frame, exactly as the plan's seams place it:
/// <list type="bullet">
/// <item><see cref="Sleep" />, immediately before input is sampled, issues the
/// INPUT stage. That call is the sleep: the driver blocks inside it for as long
/// as it decides the CPU should wait.</item>
/// <item><see cref="Marker" /> with <see cref="LatencyMarker.PresentStart" />,
/// which the device stamps immediately before <c>vkQueuePresentKHR</c>, issues
/// the PRESENT stage with the same frame index.</item>
/// </list>
/// INPUT and PRESENT must pair for every frame or the driver's attribution
/// breaks, so the PRESENT stage is driven by whether this frame's INPUT was
/// issued, never by the current setting: a mode change between the two halves of
/// a frame takes effect on the next frame, not in the middle of this one.
///
/// There is no swapchain state (unlike VK_NV_low_latency2 nothing is bound to a
/// <c>VkSwapchainKHR</c>, so <see cref="OnSwapchainCreated" /> has nothing to
/// re-apply) and no per-submit tagging. The mode itself is set by a call with no
/// presentation info, and only when it changes - the per-frame calls carry the
/// mode because the struct has the field, but they are not what switches it.
///
/// Reports are the same CPU-timestamp reports the None backend produces
/// (<see cref="LatencyPhaseTracker" />): the extension gives back no timings of
/// its own, unlike NV's <c>vkGetLatencyTimingsNV</c>.
/// </summary>
internal sealed class AmdAntiLagBackend : ILatencyBackend
{
    /// <summary>A frame's worth of reports; a client that never samples must not grow this.</summary>
    private const int MaxReports = 256;

    /// <summary>The recorded-call ring the tests read; the frame never looks at it.</summary>
    private const int MaxCalls = 512;

    private readonly Device _device;
    private readonly AmdAntiLagFunctions _functions;
    private readonly LatencyPhaseTracker _tracker;
    private readonly Action<string>? _log;

    private readonly List<LatencyFrameReport> _reports = new();
    private readonly List<AmdAntiLagCall> _calls = new();

    /// <summary>Reports and the call log are drained by the stats sample, which is not the frame thread.</summary>
    private readonly object _reportLock = new();

    /// <summary>The mode and cap the driver has been told about; null until the first call.</summary>
    private AntiLagModeAMD? _appliedMode;
    private uint _appliedMaxFps;

    /// <summary>The frame whose INPUT stage was issued and whose PRESENT stage is still owed.</summary>
    private ulong _pendingFrameId;
    private bool _presentOwed;

    /// <summary>A dropped or doubled PRESENT is logged once, never every frame.</summary>
    private bool _pairingLogged;

    private AmdAntiLagBackend(Device device, AmdAntiLagFunctions functions, Action<string>? log)
    {
        _device = device;
        _functions = functions;
        _tracker = new LatencyPhaseTracker(log);
        _log = log;
    }

    /// <summary>
    /// The backend for a device that enabled VK_AMD_anti_lag, or null when the
    /// entry point did not resolve - in which case the caller keeps whatever it
    /// would have used instead.
    /// </summary>
    public static AmdAntiLagBackend? TryCreate(Vk api, Device device, Action<string>? log = null)
    {
        AmdAntiLagFunctions? functions = AmdAntiLagFunctions.Load(api, device);
        return functions == null ? null : new AmdAntiLagBackend(device, functions, log);
    }

    /// <summary>The backend over an already loaded entry point (the context loads it once).</summary>
    public static AmdAntiLagBackend Create(Device device, AmdAntiLagFunctions functions, Action<string>? log = null)
    {
        if (functions == null) throw new ArgumentNullException(nameof(functions));
        return new AmdAntiLagBackend(device, functions, log);
    }

    public LatencyBackendKind Kind => LatencyBackendKind.AmdAntiLag;

    public LatencySettings Settings { get; private set; } = LatencySettings.Disabled;

    /// <summary>
    /// The extension takes the cap as <c>maxFPS</c> on every update, so whenever
    /// anti-lag is on the driver is pacing and the client's own FPS limiter must
    /// stand down (lib seam S3).
    /// </summary>
    public bool OwnsFrameCap => Settings.Enabled;

    /// <summary>How many <c>vkAntiLagUpdateAMD</c> calls carried the INPUT stage.</summary>
    public int InputStageCalls { get; private set; }

    /// <summary>How many carried the PRESENT stage. Equal to <see cref="InputStageCalls" /> between frames.</summary>
    public int PresentStageCalls { get; private set; }

    /// <summary>How often the mode was actually set, which must be once per change and no more.</summary>
    public int ModeUpdates { get; private set; }

    /// <summary>How often a frame's INPUT and PRESENT stages failed to pair.</summary>
    public int PairingFaults { get; private set; }

    /// <summary>The calls this backend made, oldest first (capped).</summary>
    public AmdAntiLagCall[] Calls
    {
        get { lock (_reportLock) return _calls.ToArray(); }
    }

    /// <summary>
    /// Remembers what the client asks for. The driver is not told here: the mode
    /// is set at the next frame's sleep, so a change never lands between a
    /// frame's INPUT and PRESENT stages.
    /// </summary>
    public void Apply(in LatencySettings settings) => Settings = settings;

    /// <summary>
    /// The INPUT stage, which is the sleep: the driver blocks inside
    /// <c>vkAntiLagUpdateAMD</c>. Returns what it cost in microseconds.
    /// </summary>
    public ulong Sleep(ulong frameId)
    {
        // A frame whose PRESENT never came (a skipped or failed present) would
        // otherwise pair with this frame's INPUT.
        if (_presentOwed) NotePairingFault("frame " + _pendingFrameId + " never reached its PRESENT stage");

        EnsureMode();

        if (!Settings.Enabled)
        {
            _presentOwed = false;
            return 0;
        }

        long before = LatencyClock.NowUs();
        Update(AntiLagStageAMD.InputAmd, frameId, modeOnly: false);
        long after = LatencyClock.NowUs();

        _pendingFrameId = frameId;
        _presentOwed = true;

        return after > before ? (ulong)(after - before) : 0UL;
    }

    /// <summary>
    /// Stamps the CPU timestamp of the phase and, at
    /// <see cref="LatencyMarker.PresentStart" />, issues the PRESENT stage - the
    /// device stamps that marker immediately before <c>vkQueuePresentKHR</c>,
    /// which is where the extension wants the call.
    /// </summary>
    public void Marker(ulong frameId, LatencyMarker marker)
    {
        _tracker.Mark(frameId, marker, LatencyClock.NowUs());

        if (marker != LatencyMarker.PresentStart) return;

        if (!_presentOwed)
        {
            // Anti-lag was switched on mid-frame, or this frame never slept:
            // attribution needs both halves, so this frame gets neither.
            return;
        }

        if (frameId != _pendingFrameId)
        {
            NotePairingFault("PRESENT of frame " + frameId + " does not match the INPUT of frame " + _pendingFrameId);
            _presentOwed = false;
            return;
        }

        Update(AntiLagStageAMD.PresentAmd, frameId, modeOnly: false);
        _presentOwed = false;
    }

    /// <summary>
    /// Nothing to do: unlike VK_NV_low_latency2 the extension holds no per
    /// swapchain state, so a resize or a vsync toggle re-applies nothing.
    /// </summary>
    public void OnSwapchainCreated(SwapchainKHR swapchain)
    {
    }

    /// <summary>The extension attributes by its own frame index; submits are never tagged.</summary>
    public unsafe void* TagSubmit(ulong frameId, void* pNext) => pNext;

    public void OnPresent(ulong frameId, ulong presentId)
    {
        if (!_tracker.TryComplete(frameId, presentId, out LatencyFrameReport report)) return;
        lock (_reportLock)
        {
            if (_reports.Count >= MaxReports) _reports.RemoveAt(0);
            _reports.Add(report);
        }
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

    /// <summary>
    /// Switches the driver back off, so a device that outlives this backend (the
    /// client swapping backends at runtime) is not left pacing.
    /// </summary>
    public void Dispose()
    {
        if (_appliedMode == null || _appliedMode == AntiLagModeAMD.OffAmd) return;
        _appliedMode = AntiLagModeAMD.OffAmd;
        _appliedMaxFps = 0;
        ModeUpdates++;
        Update(AntiLagStageAMD.InputAmd, 0, modeOnly: true);
    }

    /// <summary>The mode the current settings ask for; AMD has no boost, so Boost is On.</summary>
    private AntiLagModeAMD DesiredMode() =>
        Settings.Enabled ? AntiLagModeAMD.OnAmd : AntiLagModeAMD.OffAmd;

    /// <summary>
    /// The cap as frames per second, rounded to nearest (the settings carry a
    /// minimum interval in microseconds; 0 is uncapped).
    /// </summary>
    private uint DesiredMaxFps()
    {
        if (!Settings.Enabled || Settings.MinimumIntervalUs == 0) return 0;
        double fps = 1_000_000.0 / Settings.MinimumIntervalUs;
        if (fps < 1.0) return 1;
        return (uint)Math.Round(fps, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Sets the mode, and only when it changed: a call per frame would ask the
    /// driver to re-arm its heuristic every frame, which is exactly what the
    /// extension says not to do.
    /// </summary>
    private void EnsureMode()
    {
        AntiLagModeAMD mode = DesiredMode();
        uint maxFps = DesiredMaxFps();
        if (_appliedMode == mode && _appliedMaxFps == maxFps) return;

        _appliedMode = mode;
        _appliedMaxFps = maxFps;
        ModeUpdates++;
        Update(AntiLagStageAMD.InputAmd, 0, modeOnly: true);
    }

    /// <summary>
    /// One <c>vkAntiLagUpdateAMD</c>. A mode-only call passes no presentation
    /// info; a staged call passes the stage and this frame's index, and both
    /// halves of a frame carry the same index.
    /// </summary>
    private unsafe void Update(AntiLagStageAMD stage, ulong frameId, bool modeOnly)
    {
        AntiLagModeAMD mode = _appliedMode ?? DesiredMode();
        uint maxFps = _appliedMaxFps;

        var presentation = new AntiLagPresentationInfoAMD
        {
            SType = StructureType.AntiLagPresentationInfoAmd,
            Stage = stage,
            FrameIndex = frameId,
        };

        var data = new AntiLagDataAMD
        {
            SType = StructureType.AntiLagDataAmd,
            Mode = mode,
            MaxFps = maxFps,
            PPresentationInfo = modeOnly ? null : &presentation,
        };

        _functions.AntiLagUpdate(_device, ref data);

        if (!modeOnly)
        {
            if (stage == AntiLagStageAMD.InputAmd) InputStageCalls++;
            else PresentStageCalls++;
        }

        lock (_reportLock)
        {
            if (_calls.Count >= MaxCalls) _calls.RemoveAt(0);
            _calls.Add(new AmdAntiLagCall(modeOnly ? 0UL : frameId, stage, mode, maxFps, modeOnly));
        }
    }

    /// <summary>Counts a broken pair and logs the first one; a dropped marker repeats.</summary>
    private void NotePairingFault(string what)
    {
        PairingFaults++;
        _presentOwed = false;
        if (_pairingLogged) return;
        _pairingLogged = true;
        _log?.Invoke("latency: anti-lag " + what + ". Reported once, however often it happens.");
    }
}
