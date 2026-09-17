using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Config;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 1A step 4: the frame bracket, the thick-line capability, the
// window-size notification and the parity-dump readback - the device calls that used to
// sit in ClientPlatformWindows.window_RenderFrame, Start, Window_Resize and
// OptimumParityDumpAttachment. The base keeps the frame pacing, the frame handler call and
// the parity dump itself.
public partial class VulkanClientPlatform
{
    /// <summary>
    /// Test seam: the backend the latency overrides drive. Null uses the device's, which is
    /// where it comes from in the client; a headless test sets it with no device present.
    /// </summary>
    internal ILatencyBackend? LatencyBackendOverride;

    /// <summary>The backend <see cref="LatencySleep" /> drives; null before the device is up.</summary>
    internal ILatencyBackend? LatencyBackend => LatencyBackendOverride ?? device?.Latency;

    /// <summary>Frame ids while no device owns the counter (headless tests).</summary>
    private ulong latencyFrameIdFallback;

    /// <summary>
    /// "Latency seams" S3: true when the active backend paces the frame itself, so the lib's
    /// own FPS limiter in <c>window_RenderFrame</c> stands down. Always false on this branch,
    /// whose only backend is <see cref="NoneLatencyBackend" />; the pacing backends live on
    /// <c>feat/latency</c>.
    /// </summary>
    public override bool LatencyOwnsFrameCap
    {
        get
        {
            ILatencyBackend? backend = LatencyBackend;
            return backend != null && backend.OwnsFrameCap;
        }
    }

    /// <summary>
    /// "Latency seams" S3: the frame's one wait, called by the lib immediately before it
    /// samples the mouse. Allocates the frame's latency id, sleeps, and stamps the two
    /// markers this site owns - InputSample (the point latency is measured from) and
    /// SimulationStart. Every other marker belongs to a site further down the frame
    /// (see <see cref="ILatencyBackend" /> for the full map).
    /// </summary>
    public override void LatencySleep()
    {
        ILatencyBackend? backend = LatencyBackend;
        if (backend == null) return;

        ulong frameId = NextLatencyFrameId();
        // Counted only when the backend actually waited, so the None backend's sleep_n stays 0.
        long sleepStart = VulkanStats.WaitStart();
        if (backend.Sleep(frameId) > 0) VulkanStats.NoteWait(WaitSite.LatencySleep, sleepStart);
        backend.Marker(frameId, LatencyMarker.InputSample);
        backend.Marker(frameId, LatencyMarker.SimulationStart);
    }

    /// <summary>
    /// "Latency seams" S3: the client's effective frame cap for this frame, handed over by
    /// <c>window_RenderFrame</c> immediately before <see cref="LatencySleep" />. The lib
    /// decides when a cap applies at all (vsync off, MaxFps in the 10..241 window) and folds
    /// in the background-window reduction, so a backend that owns the cap paces an unfocused
    /// window to the reduced number too.
    ///
    /// Applied only when it changed: an Apply per frame would re-arm a driver heuristic
    /// every frame. Off is off: a backend whose mode is Off is never touched, so the frame
    /// with latency work off is the frame Milestone 1 delivered.
    /// </summary>
    /// <param name="maxFps">Frames per second, or 0 for uncapped.</param>
    public override void SetLatencyFrameCap(int maxFps)
    {
        ILatencyBackend? backend = LatencyBackend;
        if (backend == null) return;

        LatencySettings current = backend.Settings;
        if (current.Mode == LatencyMode.Off) return;

        ulong interval = FrameCapIntervalUs(maxFps);
        if (current.MinimumIntervalUs == interval) return;
        backend.Apply(new LatencySettings(current.Mode, interval));
    }

    /// <summary>
    /// The cap as a minimum frame interval in microseconds; 0 in, 0 out, which is what every
    /// backend reads as "do not pace to an interval". Negative values mean the same, so a
    /// garbage cap can never turn into a pacing interval.
    /// </summary>
    internal static ulong FrameCapIntervalUs(int maxFps)
    {
        if (maxFps <= 0) return 0;
        return LatencySettings.IntervalUsForFps(maxFps);
    }

    /// <summary>
    /// The frame id for the frame about to start. The device owns the counter (seam S2);
    /// the fallback only runs headless, where there is no device to own it.
    /// </summary>
    private ulong NextLatencyFrameId()
    {
        VulkanDevice? owner = device;
        if (owner != null) return owner.BeginLatencyFrame();
        return ++latencyFrameIdFallback;
    }

    /// <summary>Recycles the frame slot and opens a command buffer.</summary>
    public override void BeginFrame()
    {
        // World/UI separation: a GUI renderer that threw out of the AfterBlit or Ortho stage
        // unwound past both compose call sites; the new frame starts with the scope closed, so the
        // world pass draws to the targets it names under the factors it states.
        CloseUiScope();
        device.BeginFrame();
        // Until a stage or a post method says otherwise, passes are named after the frame.
        passContext = "Frame";
        passContextFlags = Graph.PassFlags.AllowSplit;
    }

    /// <summary>
    /// Closes the rendering scope, submits, and blits the result into the swapchain. Runs
    /// after the frame handler and the parity dump, exactly where GL swaps buffers.
    /// </summary>
    public override void EndFrame()
    {
        device.Present();
    }

    /// <summary>GL probes by setting a width; the device answers it as a capability.</summary>
    public override bool ProbeThickLineSupport()
    {
        // GL leaves the probed width set, so the stated line width is 1.5 from here on too.
        stated.LineWidth = 1.5f;
        return device.SupportsThickLines;
    }

    /// <summary>
    /// The device owns the default render target and the swapchain, and neither follows
    /// the window on its own. The base calls this before RebuildFrameBuffers.
    /// </summary>
    public override void OnWindowSizeChanged(int width, int height)
    {
        device.Resize(width, height);
    }

    /// <summary>The single call site of the device's parity readback.</summary>
    public override OptimumTextureReadback ReadTextureForParity(int textureId)
    {
        return device.ReadTextureForParity(textureId);
    }
}
