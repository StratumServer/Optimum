using Optimum.Render.Vulkan.Graph;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// The first render stage of a frame, as the latency seams see it (plan section
/// "Latency seams", seam S4): simulation is over and the renderer starts
/// recording. The bracket fires for every stage; the listener decides which one
/// is the frame's first, keyed on the latency frame id, so no marker of a frame
/// is ever stamped twice.
///
/// A second listener beside <see cref="VulkanClientPlatform.RenderStageListener" />
/// rather than another call inside the frame graph's listener: the two have
/// different lifetimes (the graph listener exists only while a device is
/// installed and is replaced with the graph) and a test drives either on its own.
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

    /// <summary>
    /// The latency hook (seam S4), told at the first stage of each frame. Null
    /// means the installed device is used, which is what the client does; a test
    /// sets this to watch the bracket without a device.
    /// </summary>
    internal ILatencyStageListener? LatencyStageListener;

    /// <summary>The stage most recently begun (still valid after it ended).</summary>
    internal EnumRenderStage CurrentRenderStage { get; private set; }

    /// <summary>True between a stage's Begin and End.</summary>
    internal bool InRenderStage { get; private set; }

    /// <summary>
    /// The explicit listener, or the installed device, which is the latency
    /// listener in the client: it owns the frame id the markers belong to.
    /// </summary>
    private ILatencyStageListener? ActiveLatencyStageListener() => LatencyStageListener ?? device;

    public override void BeginRenderStage(EnumRenderStage stage)
    {
        // Before the stage's own work: the markers say where rendering began, and
        // every stage asks, because which stage comes first is the client's
        // business, not the renderer's.
        ActiveLatencyStageListener()?.OnFrameRenderStart();
        CurrentRenderStage = stage;
        InRenderStage = true;
        RenderStageListener?.OnBeginRenderStage(stage);
    }

    public override void EndRenderStage(EnumRenderStage stage)
    {
        InRenderStage = false;
        RenderStageListener?.OnEndRenderStage(stage);
    }
}
