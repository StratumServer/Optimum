using System;
using Vintagestory.API.Config;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The passthrough upscaler: a diagnostic that occupies exactly the DLSS slot and
/// reconstructs nothing.
///
/// <para><b>Why it exists.</b> DLSS on an RTX 4070 at 2560x1490 shimmers on foliage at
/// Performance (1280x745) and Ultra Performance (853x497) while DLAA, Quality and
/// Balanced look clean. Two explanations were ruled out with evidence - the texture LOD
/// bias (sweeping the sharpness slider from -2.00 to -1.00 at a shimmering preset changed
/// nothing) and the jitter phase count (it follows the vendor ratio). The question left is
/// whether the shimmer is DLSS's or our own rendering path's at a low render ratio, and
/// no amount of reading DLSS's output answers it. This does: it plans the frame exactly as
/// DLSS would for the chosen preset, renders it at exactly that size, takes the identical
/// path through the render/display split, the jitter, the motion attachment, the TAA and
/// FSR stand-down and the display-resolution post chain - and then simply magnifies the
/// scene colour instead of reconstructing it.</para>
///
/// <para><b>The measurement.</b> Two states, with <c>OptimumConfig.UpscalerJitter</c>:
/// off, the frame is a still magnification and nothing temporal moves, so any shimmer is
/// content or shading at the reduced render size; on, the render grid moves every frame
/// with nothing reconstructing it, so the shimmer is the jitter arriving unreconstructed.
/// Whatever DLSS adds on top of those two is DLSS's.</para>
///
/// <para><b>It touches no vendor runtime.</b> There is no NGX call on this path - no
/// session, no feature, no evaluate, not even the optimal-settings query - which is what
/// lets it run on a machine with no NGX and on GPUs with no vendor path at all. The render
/// size therefore comes from the preset's nominal ratio
/// (<c>OptimumConfig.NominalUpscalerRenderScale</c>), rounded the way the SDK's own query
/// rounds it, so the comparison is size-for-size;
/// <c>PassthroughPlanMatchesTheVendorPlanSizeForSize</c> in the GPU suite is what holds
/// that claim to the real driver's answer.</para>
///
/// <para>It may well be deleted once the question is answered, or kept as a cheap upscaler
/// for GPUs with no vendor path. Everything it needs lives in this file and one blit on
/// the device (<c>VulkanDevice.BlitColorScaled</c>).</para>
/// </summary>
internal static class PassthroughUpscaler
{
    /// <summary>Whether the config asks for the passthrough comparison upscaler.</summary>
    public static bool Requested => OptimumConfig.EffectiveUpscalerIsPassthrough;

    /// <summary>
    /// What to render at for this display size and preset. The vendor's own answer,
    /// reproduced without asking the vendor: the preset's nominal ratio, rounded to
    /// nearest on each axis, which is what NGX's optimal-settings query returns for
    /// every preset it was measured against.
    ///
    /// False leaves <paramref name="plan" /> at its default, which happens only for a
    /// degenerate display size.
    /// </summary>
    public static bool TryPlan(int displayWidth, int displayHeight, string preset, out UpscalePlan plan)
    {
        plan = default;
        if (displayWidth <= 0 || displayHeight <= 0) return false;

        float scale = OptimumConfig.NominalUpscalerRenderScale(preset);
        if (!(scale > 0f) || scale > 1f) scale = 1f;

        int renderWidth = Round(displayWidth * scale);
        int renderHeight = Round(displayHeight * scale);
        if (renderWidth > displayWidth) renderWidth = displayWidth;
        if (renderHeight > displayHeight) renderHeight = displayHeight;

        plan = new UpscalePlan(
            renderWidth, renderHeight, displayWidth, displayHeight, DlssUpscaler.QualityOf(preset));
        return plan.IsValid;
    }

    /// <summary>
    /// The frame's sizing question, in the same shape and with the same rule as
    /// <c>DlssUpscaler.TryPlanForFrame</c>: the setting in force decides, not any
    /// host's liveness, and false leaves both sizes at the display size - exactly what
    /// the client allocates when no upscaler was ever enabled.
    /// </summary>
    public static bool TryPlanForFrame(
        int displayWidth, int displayHeight,
        out int renderWidth, out int renderHeight, out UpscalePlan plan)
    {
        renderWidth = displayWidth;
        renderHeight = displayHeight;
        plan = default;
        if (!Requested) return false;
        if (!TryPlan(displayWidth, displayHeight, OptimumConfig.UpscalerQuality, out plan)) return false;
        renderWidth = plan.RenderWidth;
        renderHeight = plan.RenderHeight;
        return true;
    }

    /// <summary>
    /// Publishes the plan the targets were really allocated for, so everything derived
    /// from the render ratio follows the passthrough frame exactly as it follows a DLSS
    /// frame: the jitter sequence length
    /// (<c>OptimumConfig.EffectiveTemporalRenderScale</c>) and the texture LOD bias.
    /// Republishing an unchanged plan is free; the config compares before it writes.
    /// </summary>
    public static void Publish(in UpscalePlan plan)
    {
        if (!plan.IsValid) return;
        OptimumConfig.SetUpscalerPlan(plan.RenderScale, plan.LodBias);
    }

    /// <summary>Round half away from zero, which is how the SDK's query rounds its sizes.</summary>
    private static int Round(double value) => (int)Math.Floor(value + 0.5);
}
