using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Config;
using Optimum.Render.Vulkan.Graph;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 1A step 4: the frame bracket, the thick-line capability, the
// window-size notification and the parity-dump readback - the device calls that used to
// sit in ClientPlatformWindows.window_RenderFrame, Start, Window_Resize and
// OptimumParityDumpAttachment. The base keeps the frame pacing, the frame handler call and
// the parity dump itself.
public partial class VulkanClientPlatform
{
    /// <summary>The client retains ownership of its FPS limiter.</summary>
    public override bool LatencyOwnsFrameCap => false;

    /// <summary>The existing pre-input hook starts frame identity and CPU timing.</summary>
    public override void LatencySleep()
    {
        if (device == null) return;
        ulong frameId = device.BeginLatencyFrame();
        device.Latency.Marker(frameId, LatencyMarker.InputSample);
        device.Latency.Marker(frameId, LatencyMarker.SimulationStart);
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

/// <summary>
/// The first render stage of a frame, as the latency seams see it (seam S4): simulation is
/// over and the renderer starts recording. The bracket fires for every stage; the listener
/// decides which one is the frame's first, keyed on the latency frame id, so no marker of a
/// frame is ever stamped twice.
///
/// A second listener beside <see cref="VulkanClientPlatform.RenderStageListener" /> rather
/// than another call inside the frame graph's listener: the two have different lifetimes
/// (the graph listener exists only while a device is installed and is replaced with the
/// graph) and a test drives either on its own.
/// </summary>
internal interface ILatencyStageListener
{
    void OnFrameRenderStart();
}

// Vulkan-native plan, Phase 2 (contract C3): the render-stage bracket. ClientMain.TriggerRenderStage
// calls BeginRenderStage before the stage's renderers and EndRenderStage after them; the
// platform records the stage and forwards both to the frame graph once it listens.
public partial class VulkanClientPlatform
{
    /// <summary>
    /// The frame graph's hook; null until the frame graph sets it, in which case the bracket
    /// only updates <see cref="CurrentRenderStage" />.
    /// </summary>
    internal IRenderStageListener? RenderStageListener;

    /// <summary>The stage most recently begun (still valid after it ended).</summary>
    internal EnumRenderStage CurrentRenderStage { get; private set; }

    /// <summary>True between a stage's Begin and End.</summary>
    internal bool InRenderStage { get; private set; }

    /// <summary>
    /// The latency hook (seam S4), told at the first stage of each frame. Null means the
    /// installed device is used, which is what the client does; a test sets this to watch
    /// the bracket without a device.
    /// </summary>
    internal ILatencyStageListener? LatencyStageListener;

    /// <summary>
    /// The explicit listener, or the installed device, which is the latency listener in the
    /// client: it owns the frame id the markers belong to.
    /// </summary>
    private ILatencyStageListener? ActiveLatencyStageListener() => LatencyStageListener ?? device;

    public override void BeginRenderStage(EnumRenderStage stage)
    {
        // Before the stage's own work: the markers say where rendering began, and every
        // stage asks, because which stage comes first is the client's business, not the
        // renderer's.
        ActiveLatencyStageListener()?.OnFrameRenderStart();
        CurrentRenderStage = stage;
        InRenderStage = true;
        RenderStageListener?.OnBeginRenderStage(stage);
    }

    public override void EndRenderStage(EnumRenderStage stage)
    {
        // Phase 5: the passes mods declared for this slot run after the stage's renderers,
        // still inside the stage (VulkanClientPlatform.ModPasses.cs).
        RunModPasses(stage);
        InRenderStage = false;
        RenderStageListener?.OnEndRenderStage(stage);
    }
}

// Vulkan-native plan, Phase 1A step 4: the device halves of the TAA motion windows and
// the FSR target selection, moved out of ClientPlatformWindows. The guards and the
// window state stay in the base (BeginMotionWrite, EndMotionWrite, BeginMotionOnlyWrite,
// BlitPrimaryToDefault); these are the calls the base makes once a window opens.
public partial class VulkanClientPlatform
{
    /// <summary>Primary's default colour set plus the motion attachment.</summary>
    public override void EnableMotionDrawBuffers()
    {
        StateDrawBuffers(FrameBuffers[0].FboId, (1 << (MotionAttachmentIndex + 1)) - 1);
    }

    /// <summary>
    /// Back to Primary's default colour set. MotionAttachmentIndex is also the size of
    /// the default set (2 without the SSAO G-buffer, 4 with it), because the attachment
    /// was appended after it.
    /// </summary>
    public override void RestorePrimaryDrawBuffers()
    {
        StateDrawBuffers(FrameBuffers[0].FboId, (1 << MotionAttachmentIndex) - 1);
    }

    /// <summary>The motion attachment alone; the device takes the mask directly.</summary>
    public override void EnableMotionOnlyDrawBuffers()
    {
        StateDrawBuffers(FrameBuffers[0].FboId, 1 << MotionAttachmentIndex);
    }

    /// <summary>Replace-blending on the motion attachment (TAA P3).</summary>
    public override void ApplyOptimumMotionBlendState()
    {
        if (!OptimumMotionWriteActive || MotionAttachmentIndex < 0) return;
        StateSlotBlend(MotionAttachmentIndex, 32774, 1, 0, 1, 0);
    }

    /// <summary>Additive (ONE, ONE) blending on the motion attachment for the OIT merge (TAA P4).</summary>
    public override void ApplyOptimumMotionAccumulateBlendState()
    {
        if (!OptimumMotionWriteActive || MotionAttachmentIndex < 0) return;
        StateSlotBlend(MotionAttachmentIndex, 32774, 1, 1, 1, 1);
    }

    /// <summary>The FSR EASU pass writes colour attachment 0 of the FSR target.</summary>
    public override void SelectFsrDrawBuffer(FrameBufferRef target)
    {
        StateDrawBuffers(target.FboId, 1);
    }
}
