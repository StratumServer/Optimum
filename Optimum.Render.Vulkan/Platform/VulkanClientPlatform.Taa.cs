using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 1A step 4: the device halves of the TAA motion windows and
// the FSR target selection, moved out of ClientPlatformWindows. The guards and the
// window state stay in the base (BeginMotionWrite, EndMotionWrite, BeginMotionOnlyWrite,
// BlitPrimaryToDefault); these are the calls the base makes once a window opens.
public partial class VulkanClientPlatform
{
    /// <summary>Primary's default colour set plus the motion attachment.</summary>
    public override void EnableMotionDrawBuffers()
    {
        device.SetDrawBuffers(FrameBuffers[0].FboId, (1 << (MotionAttachmentIndex + 1)) - 1);
    }

    /// <summary>
    /// Back to Primary's default colour set. MotionAttachmentIndex is also the size of
    /// the default set (2 without the SSAO G-buffer, 4 with it), because the attachment
    /// was appended after it.
    /// </summary>
    public override void RestorePrimaryDrawBuffers()
    {
        device.SetDrawBuffers(FrameBuffers[0].FboId, (1 << MotionAttachmentIndex) - 1);
    }

    /// <summary>The motion attachment alone; the device takes the mask directly.</summary>
    public override void EnableMotionOnlyDrawBuffers()
    {
        device.SetDrawBuffers(FrameBuffers[0].FboId, 1 << MotionAttachmentIndex);
    }

    /// <summary>Replace-blending on the motion attachment (TAA P3).</summary>
    public override void ApplyOptimumMotionBlendState()
    {
        if (!OptimumMotionWriteActive || MotionAttachmentIndex < 0) return;
        device.SetBlendEquation(MotionAttachmentIndex, 32774);
        device.SetBlendFuncSeparate(MotionAttachmentIndex, 1, 0, 1, 0);
    }

    /// <summary>Additive (ONE, ONE) blending on the motion attachment for the OIT merge (TAA P4).</summary>
    public override void ApplyOptimumMotionAccumulateBlendState()
    {
        if (!OptimumMotionWriteActive || MotionAttachmentIndex < 0) return;
        device.SetBlendEquation(MotionAttachmentIndex, 32774);
        device.SetBlendFuncSeparate(MotionAttachmentIndex, 1, 1, 1, 1);
    }

    /// <summary>The FSR EASU pass writes colour attachment 0 of the FSR target.</summary>
    public override void SelectFsrDrawBuffer(FrameBufferRef target)
    {
        device.SetDrawBuffers(target.FboId, 1);
    }
}
