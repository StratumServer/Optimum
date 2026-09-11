using Vintagestory.API.Config;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 1A step 4: the frame bracket, the thick-line capability, the
// window-size notification and the parity-dump readback - the device calls that used to
// sit in ClientPlatformWindows.window_RenderFrame, Start, Window_Resize and
// OptimumParityDumpAttachment. The base keeps the frame pacing, the frame handler call and
// the parity dump itself.
public partial class VulkanClientPlatform
{
    /// <summary>Recycles the frame slot and opens a command buffer.</summary>
    public override void BeginFrame()
    {
        device.BeginFrame();
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
        device.SetLineWidth(1.5f);
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
