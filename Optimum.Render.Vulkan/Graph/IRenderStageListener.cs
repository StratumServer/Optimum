using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>
/// Vulkan-native plan, Phase 2 (contract C3): receives the render-stage bracket
/// <c>ClientMain.TriggerRenderStage</c> issues around each stage's renderers, forwarded by
/// <see cref="Platform.VulkanClientPlatform" />. The frame graph implements it to map
/// <c>(stage, target)</c> to passes. Called on the render thread only.
/// </summary>
internal interface IRenderStageListener
{
    /// <summary>Before the stage's renderers run.</summary>
    void OnBeginRenderStage(EnumRenderStage stage);

    /// <summary>After the stage's renderers ran, before the GL error check.</summary>
    void OnEndRenderStage(EnumRenderStage stage);
}
