using System;
using Optimum.Render.Vulkan.Core;

namespace Optimum.Render.Vulkan;

/// <summary>
/// The frame-marking foundation (plan section "Latency seams", S1-S7): one latency
/// frame id per rendered frame, the phase markers around simulation, render submit
/// and present, the present id per present, and the backend the markers go to.
///
/// Where each piece is stamped:
/// <list type="bullet">
/// <item><description>Frame id and InputSample/SimulationStart:
/// <c>VulkanClientPlatform.LatencySleep</c>, before the client samples input.</description></item>
/// <item><description>SimulationEnd/RenderSubmitStart: the frame's first
/// <c>BeginRenderStage</c>, through <see cref="Platform.ILatencyStageListener" />.</description></item>
/// <item><description>Submit tag: every <c>FrameSlot.Submit</c> of the frame.</description></item>
/// <item><description>RenderSubmitEnd, PresentStart/PresentEnd, the present id and the
/// frame report: <see cref="Present" />.</description></item>
/// </list>
///
/// Only the None backend exists on this branch: it never sleeps and owns no frame cap,
/// so the frame is paced exactly as before and the markers become CPU-timestamp reports
/// on the <c>stats.latency</c> line. Pinned by <c>LatencyMarkerOrderTests</c>,
/// <c>PresentIdentityTests</c> and <c>latency-foundation-coverage-tests.cs</c>.
/// </summary>
public sealed partial class VulkanDevice : Platform.ILatencyStageListener
{
    /// <summary>The platform's stage bracket reaches the latency markers here (seam S4).</summary>
    void Platform.ILatencyStageListener.OnFrameRenderStart() => NoteRenderStageStarted();

    /// <summary>
    /// The active latency backend. Never null, so every call site is a plain virtual
    /// call with no branch; the None backend unless a test installed its own.
    /// </summary>
    internal ILatencyBackend Latency { get; private set; } = new NoneLatencyBackend();

    /// <summary>Someone has installed a backend, so device setup must not overwrite it.</summary>
    private bool _latencyBackendInstalled;

    /// <summary>
    /// Installs the latency backend every marker, tag and swapchain callback goes to
    /// (seams S2-S5). A test calls it before <see cref="Initialize" /> to watch the
    /// frame; the stats line reports whichever is installed.
    /// </summary>
    internal void SetLatencyBackend(ILatencyBackend backend)
    {
        Latency = backend ?? throw new ArgumentNullException(nameof(backend));
        _latencyBackendInstalled = true;
        VulkanStats.LatencySource = Latency;
        if (_frames != null) _frames.Latency.Backend = Latency;
        if (_swapchain != null) _swapchain.Latency = Latency;
    }

    /// <summary>
    /// Installs the backend the capabilities selected (None on this branch) unless one
    /// was installed before the device came up. Called once, right after the context
    /// exists and before the frame ring and the first swapchain, so the ring's submit
    /// tag, the stats source and every swapchain creation see the same instance.
    /// </summary>
    private void InstallSelectedLatencyBackend()
    {
        VulkanStats.LatencyRevision = 0;
        if (!_latencyBackendInstalled)
        {
            Latency = new NoneLatencyBackend(MirrorValidationMessage);
        }
        VulkanStats.LatencySource = Latency;
    }

    /// <summary>
    /// The latency identity of the frame being recorded (seam S2): one monotonic value
    /// per rendered frame, allocated by <see cref="BeginLatencyFrame" /> before the
    /// client samples input, and used by every marker, every submit tag and the present
    /// map of that frame.
    ///
    /// Deliberately not the Frame timeline value, which advances two or three times per
    /// frame (Submit A, Submit B, any partial submit), and deliberately not
    /// <c>_frameCounter</c>, which stays a 32-bit counter because the GPU checkpoint
    /// markers pack it into a pointer-sized word.
    /// </summary>
    internal ulong LatencyFrameId => _latencyFrameId;

    private ulong _latencyFrameId;

    /// <summary>An id was allocated by the platform hook and no frame has consumed it yet.</summary>
    private bool _latencyFrameIdPending;

    /// <summary>The frame whose render start has already been stamped; 0 before the first.</summary>
    private ulong _latencyRenderStartFrame;

    /// <summary>
    /// Allocates the next latency frame id. The lib hook calls this from
    /// <c>VulkanClientPlatform.LatencySleep</c>, before input is sampled and therefore
    /// before <see cref="BeginFrame" />; a frame that starts without it (a headless test,
    /// a path with no platform) allocates its own id in <see cref="BeginFrame" />, so the
    /// identity exists exactly once either way.
    /// </summary>
    public ulong BeginLatencyFrame()
    {
        _latencyFrameId++;
        _latencyFrameIdPending = true;
        return _latencyFrameId;
    }

    /// <summary>
    /// Seam S2 at the frame start: takes the id the platform hook allocated, or
    /// allocates one, and tags every submit of the frame with it (seam S4).
    /// </summary>
    private void BeginLatencyFrameIdentity()
    {
        if (!_latencyFrameIdPending) BeginLatencyFrame();
        _latencyFrameIdPending = false;
        _frames.Latency.FrameId = _latencyFrameId;
    }

    /// <summary>
    /// The frame's first render stage has begun: simulation is over and the renderer
    /// starts recording (seam S4). Called from <c>VulkanClientPlatform.BeginRenderStage</c>
    /// on every stage; only the first of a frame stamps anything, so no marker of a
    /// frame is ever stamped twice.
    /// </summary>
    internal void NoteRenderStageStarted()
    {
        if (_latencyRenderStartFrame == _latencyFrameId) return;
        _latencyRenderStartFrame = _latencyFrameId;
        Latency.Marker(_latencyFrameId, LatencyMarker.SimulationEnd);
        Latency.Marker(_latencyFrameId, LatencyMarker.RenderSubmitStart);
    }

    /// <summary>The present id of the last present, 0 before the first. Tests only.</summary>
    internal ulong LastPresentIdForTests { get; private set; }

    /// <summary>Clears the stats source if it is this device's backend, then ends the backend.</summary>
    private void DisposeLatency()
    {
        if (ReferenceEquals(VulkanStats.LatencySource, Latency))
        {
            VulkanStats.LatencySource = null;
            VulkanStats.LatencyRevision = 0;
        }
        // The device installed it, so the device ends it; the None backend and the
        // test fake have nothing to release.
        Latency.Dispose();
    }
}
