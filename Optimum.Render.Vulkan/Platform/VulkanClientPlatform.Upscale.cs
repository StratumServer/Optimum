using System;
using System.Collections.Generic;
using System.IO;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// DLSS plan, Phase 2 step 4: the upscaler's lifetime, tied to this platform's
/// graphics lifetime.
///
/// Bring-up is in two halves because NGX's device extensions have to be
/// requested <i>at device creation</i>: the session and its requirement
/// contributor are prepared before <c>device.Initialize</c> and reach the
/// context through <c>ConfigureContextOptions</c>, and NGX is initialised on the
/// device once it exists. Teardown is in the only order the driver survives -
/// the feature is retired, the frame timeline drained, NGX shut down, and only
/// then the device destroyed - and NGX is never brought up again in the process.
///
/// Every failure is the same failure: no upscaler, one line in the client log,
/// the setting stood down for the session, and a frame that runs exactly the
/// chain it ran before the upscaler was asked for.
/// </summary>
public partial class VulkanClientPlatform
{
    private DlssUpscaler? upscaler;

    /// <summary>The upscaler this platform brought up, or null when none is running.</summary>
    internal DlssUpscaler? Upscaler => upscaler;

    /// <summary>Whether an upscaler is up and can be planned and evaluated this frame.</summary>
    internal bool UpscalerActive => upscaler != null && upscaler.Active;

    /// <summary>
    /// The first half: prepared before the device is created, so NGX's instance
    /// and device extensions reach <c>VulkanContext</c> through the S1
    /// requirement seam. Does nothing at all when the setting is off.
    /// </summary>
    internal void PrepareUpscaler(VulkanDevice target)
    {
        if (upscaler != null) return;
        if (!DlssUpscaler.Requested) return;

        upscaler = DlssUpscaler.TryPrepare(UpscalerDataPath(), LogUpscaler);
        NgxDeviceRequirements? requirements = upscaler?.Requirements;
        if (requirements == null) return;

        // Chained rather than assigned: the platform is not the only thing that
        // may want to configure the context, and a test's own configuration has
        // to survive an upscaler being prepared.
        Action<VulkanContextOptions>? configured = target.ConfigureContextOptions;
        target.ConfigureContextOptions = options =>
        {
            configured?.Invoke(options);
            options.RequirementContributors.Add(requirements);
        };
    }

    /// <summary>
    /// The second half: NGX is initialised on the device that was just created.
    /// A refusal is not a failed install - the client keeps the Vulkan device and
    /// renders without an upscaler.
    /// </summary>
    internal void BringUpUpscaler(VulkanDevice target)
    {
        if (upscaler == null) return;

        target.UpscalerHandles(out IntPtr instance, out IntPtr physicalDevice, out IntPtr deviceHandle);
        if (!upscaler.BringUp(target, instance, physicalDevice, deviceHandle))
        {
            // TryPrepare's session is disposed with it; nothing was initialised,
            // so there is nothing to shut down.
            upscaler.Dispose();
            upscaler = null;
        }
    }

    /// <summary>
    /// Teardown, before the device goes: retire the feature, drain the frame
    /// timeline, shut NGX down. Releasing a feature after
    /// <c>NVSDK_NGX_VULKAN_Shutdown1</c>, or shutting NGX down after the device
    /// it was initialised on is gone, takes the process down inside the driver.
    /// </summary>
    internal void ShutDownUpscaler()
    {
        if (upscaler == null) return;
        try
        {
            upscaler.Shutdown();
        }
        catch (Exception error)
        {
            // A vendor runtime throwing on teardown must not stop the client exiting.
            LogUpscaler("[Optimum] DLSS teardown: " + error.Message);
        }
        upscaler = null;
        OptimumConfig.ClearUpscalerPlan();
    }

    // ------------------------------------------------------- the placement

    /// <summary>
    /// DLSS plan, Phase 3: whether the frame may be planned and evaluated through an
    /// upscaler. The host's own liveness plus the one thing the placement needs beyond
    /// it - the motion attachment, without which there is nothing to reproject with.
    /// </summary>
    public override bool OptimumUpscalerActive => UpscalerActive && MotionAttachmentIndex >= 0;

    /// <summary>
    /// DLSS plan, Phase 3: what to render at for this display size, from the vendor's
    /// own optimal-settings query. Called from <c>SetupDefaultFrameBuffers</c>, so it
    /// runs before there are any framebuffers and must not touch them.
    ///
    /// Nothing is remembered from it: the feature is created for one (render size,
    /// display size, preset) triple, and <see cref="RenderOptimumUpscale" /> reads that
    /// triple off the targets that were really allocated, so a plan cached here could
    /// only ever disagree with them.
    /// </summary>
    public override bool OptimumTryPlanUpscaleRenderSize(
        int displayWidth, int displayHeight, out int renderWidth, out int renderHeight)
    {
        renderWidth = displayWidth;
        renderHeight = displayHeight;
        if (upscaler == null || !upscaler.Active) return false;
        if (!upscaler.TryPlan(displayWidth, displayHeight, OptimumConfig.UpscalerQuality, out UpscalePlan plan))
        {
            return false;
        }
        renderWidth = plan.RenderWidth;
        renderHeight = plan.RenderHeight;
        LogUpscaler("[Optimum] DLSS plan: " + plan);
        return true;
    }

    /// <summary>Whether the one-time "no depth blit here" line has been logged.</summary>
    private bool upscaleDepthRefused;

    /// <summary>
    /// DLSS plan, Phase 3: the evaluate, where the TAA resolve would be - the jittered
    /// render-resolution colour, depth and motion in, the display-resolution scene colour
    /// out, followed by the one nearest-neighbour depth upscale the
    /// AfterFinalComposition overlays depth-test against.
    ///
    /// <para><b>Edge behaviour of that depth.</b> A nearest-neighbour upscale gives every
    /// display pixel the depth of the render pixel it lands in, so along a silhouette the
    /// overlay's depth test is quantised to the render grid: a depth-tested overlay can
    /// gain or lose up to one render pixel (about 1.5 display pixels at the Quality
    /// preset) of coverage against the edge it meets. That is deliberate. The
    /// alternative - moving the upscaler after the overlays - would feed DLSS a colour
    /// buffer with un-jittered, un-reprojectable 2D content baked into it, which the DLSS
    /// Programming Guide section 3.1 rules out.</para>
    ///
    /// <para>Any failure ends the same way every other upscaler failure does: false, one
    /// log line, the setting stood down, and a frame that runs the chain it ran before
    /// the upscaler was asked for.</para>
    /// </summary>
    public override bool RenderOptimumUpscale()
    {
        if (!OptimumUpscalerActive || upscaler == null) return false;

        List<FrameBufferRef> buffers = FrameBuffers;
        if (buffers == null || buffers.Count <= OptimumUpscaledSceneIndex) return false;
        FrameBufferRef primary = buffers[0];
        FrameBufferRef target = buffers[OptimumUpscaledSceneIndex];
        if (primary == null || target == null) return false;
        if (primary.ColorTextureIds == null || primary.ColorTextureIds.Length <= MotionAttachmentIndex) return false;

        // The triple the targets were really allocated for, which is the one the feature
        // must serve. Taken from the targets rather than from the remembered plan, so a
        // rebuild that produced different sizes cannot evaluate against a stale feature.
        UpscalePlan plan = new UpscalePlan(
            primary.Width, primary.Height, target.Width, target.Height,
            DlssUpscaler.QualityOf(OptimumConfig.UpscalerQuality));
        if (!upscaler.EnsureFeature(plan)) return false;

        IOptimumTemporalContext frame = OptimumTemporal.Context;
        NgxDlssEvaluation evaluation = new NgxDlssEvaluation
        {
            // Temporal contract 7.2: NGX wants the offset of a projection built by
            // adding the shear; ours subtracts it, so it gets -JitterPx, render pixels.
            JitterOffsetX = -frame.JitterPx.X,
            JitterOffsetY = -frame.JitterPx.Y,
            // 7.1 for raw NGX: our vectors are already render pixels.
            MotionVectorScaleX = 1f,
            MotionVectorScaleY = 1f,
            // Section 5: any reset reason at all throws the history away rather than
            // reprojecting it - a resize, a teleport, a rebase, a world load.
            Reset = frame.Reset,
        };

        NgxResult result = upscaler.Evaluate(
            primary.ColorTextureIds[0],
            primary.DepthTextureId,
            primary.ColorTextureIds[MotionAttachmentIndex],
            target.ColorTextureIds[0],
            evaluation);
        if (result != NgxResult.Success)
        {
            DisableOptimumUpscaler("NVSDK_NGX_VULKAN_EvaluateFeature: " + NgxInterop.Describe(result));
            upscaler.RetireFeature();
            return false;
        }

        // The overlays' depth, once per frame, from the depth the world was drawn with.
        if (target.DepthTextureId > 0 &&
            !device.UpscaleDepthNearest(primary.DepthTextureId, target.DepthTextureId) &&
            !upscaleDepthRefused)
        {
            upscaleDepthRefused = true;
            LogUpscaler("[Optimum] DLSS: this driver refuses a depth blit, so the late 3D overlays " +
                "(selection outline and similar) keep whatever depth the target holds.");
        }
        return true;
    }

    /// <summary>
    /// Where NGX keeps its own logs and caches. The client's data path, which is
    /// writable by definition, with a temp-directory fallback for a headless host
    /// that has none.
    /// </summary>
    private string UpscalerDataPath()
    {
        string? dataPath = CrashMarkerDataPath ?? GamePaths.DataPath;
        if (string.IsNullOrEmpty(dataPath)) dataPath = Path.GetTempPath();
        return Path.Combine(dataPath, "ModConfig", "optimum-ngx");
    }

    /// <summary>
    /// The upscaler's one line goes to the client log, like every other backend
    /// decision, so "why is DLSS not on" is answered by the log the user already
    /// sends.
    /// </summary>
    private void LogUpscaler(string line)
    {
        Logger?.Notification(line);
    }
}
