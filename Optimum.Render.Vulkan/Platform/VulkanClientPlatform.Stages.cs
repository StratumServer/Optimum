using Optimum.Render.Vulkan.Graph;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Platform;

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

    public override void BeginRenderStage(EnumRenderStage stage)
    {
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
