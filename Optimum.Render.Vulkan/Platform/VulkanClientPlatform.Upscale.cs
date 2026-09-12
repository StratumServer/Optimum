using System;
using System.Collections.Generic;
using System.IO;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

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
        // No upscaler owns the resolve any more, so the bias goes back to whatever
        // the render scale and TAA ask for - 0, and the parameter off the atlases
        // and samplers entirely, in the common case.
        ShaderRegistry.ApplyOptimumLodBias();
    }

    // ------------------------------------------------------- the placement

    /// <summary>
    /// DLSS plan, Phase 3: whether the frame may be planned and evaluated through an
    /// upscaler. The host's own liveness plus the one thing the placement needs beyond
    /// it - the motion attachment, without which there is nothing to reproject with.
    ///
    /// <para>The setting is part of the condition, not just the host's liveness: the tab
    /// can turn the upscaler off while the host is still up and holding a feature, and on
    /// that frame the answer has to be "no" immediately - the in-house TAA resolve takes
    /// the frame back on the same rebuild, and two resolves over one history would blend
    /// the same frame twice.</para>
    /// </summary>
    public override bool OptimumUpscalerActive =>
        UpscalerActive && OptimumConfig.UpscalerReplacesTaa && MotionAttachmentIndex >= 0;

    /// <summary>
    /// DLSS plan, Phase 6: why the settings tab may not offer an upscaler here, or null
    /// while it may. Everything the host already knows, in the words it already produced -
    /// "the NVIDIA driver library ... is not installed", "the NGX shim is not loadable",
    /// "no NGX feature libraries were found", or the sentence a runtime stand-down logged.
    ///
    /// The one case the host cannot describe is the one where there is no host: the
    /// session started with the setting off, so NGX's device extensions were never
    /// requested at device creation and no amount of setting-flipping can bring DLSS up
    /// before the client restarts. That is said plainly rather than silently offering a
    /// choice that would stand itself down one rebuild later.
    /// </summary>
    public override string OptimumUpscalerUnavailable()
    {
        if (OptimumConfig.UpscalerRuntimeDisabled && upscaler == null)
        {
            return "the renderer stood the upscaler down for this session";
        }
        if (upscaler == null)
        {
            return "this session started without an upscaler, and NGX's device extensions " +
                "are requested when the Vulkan device is created; restart the game with the " +
                "upscaler on to use DLSS";
        }
        return upscaler.Active ? null : upscaler.Unavailable;
    }

    /// <summary>
    /// DLSS plan, Phase 6: the upscaler slot or its preset changed while the client runs.
    ///
    /// The live feature goes back on the frame timeline first, because it was created for
    /// a plan that is no longer the one the frame will ask for - a different preset, or
    /// none at all - and NGX sizes its internal buffers at creation, so a preset change is
    /// a new feature, never a reconfigured one. The host creates the replacement lazily on
    /// the next frame that evaluates, from the sizes the rebuilt targets were really
    /// allocated at; with the slot off it creates none, and nothing is left holding vendor
    /// memory for a feature the frame will never evaluate again.
    ///
    /// Then the base does what the OpenGL path does on its own: rebuild every framebuffer,
    /// which re-plans the render size through <see cref="OptimumTryPlanUpscaleRenderSize" />
    /// and resets the temporal history.
    ///
    /// <para>Order matters: the feature is retired <i>before</i> the rebuild, and the
    /// rebuild's sizing question is answered from the setting the caller has already
    /// written, never from this host still being alive (<c>DlssUpscaler.TryPlanForFrame</c>).
    /// </para>
    /// </summary>
    public override void ApplyOptimumUpscalerSettings()
    {
        if (upscaler != null) upscaler.RetireFeature();
        base.ApplyOptimumUpscalerSettings();
        // Retiring the feature cleared the published plan, so the bias the samplers
        // carry is now the one the old preset asked for. A preset change does not
        // reload shaders by design, which is exactly why it has to be written here;
        // the next frame's feature publishes the new plan and writes it again.
        ShaderRegistry.ApplyOptimumLodBias();
    }

    /// <summary>
    /// DLSS plan, Phase 6: the latency mode changed. One call into the live backend,
    /// which is the same call device bring-up makes; no target changes size and nothing
    /// temporal is invalidated, so there is no rebuild here.
    /// </summary>
    public override void ApplyOptimumLatencySettings()
    {
        device?.ReapplyLatencySettings();
    }

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
        // The whole rule lives in DlssUpscaler.TryPlanForFrame: the setting in force
        // for the frame being built decides, and only then is the host asked. The
        // host outlives a settings change, so its liveness is not the question.
        if (!DlssUpscaler.TryPlanForFrame(
            upscaler, displayWidth, displayHeight, out renderWidth, out renderHeight, out UpscalePlan plan))
        {
            return false;
        }
        LogUpscaler("[Optimum] DLSS plan: " + plan);
        return true;
    }

    /// <summary>
    /// DLSS plan, Phase 6 (PR #3 follow-up): the live plan, for the upscaling settings
    /// tab's readout - "1707x993 -> 2560x1490 MaxQuality (scale 0.667, lod bias -1.59,
    /// 18 jitter phases)". Null whenever no feature is serving a plan, which is every
    /// frame the upscaler is off, standing down, or has not created its feature yet;
    /// the tab then says so rather than showing numbers the frame is not using.
    /// </summary>
    public override string OptimumUpscalerPlan()
    {
        if (upscaler == null) return null;
        UpscalePlan plan = upscaler.Plan;
        return plan.IsValid ? plan.ToString() : null;
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
        // The feature that was just created published its own ratio, so the texture
        // LOD bias moved with it (OptimumConfig.EffectiveTerrainLodBias). Applied
        // here rather than left to the chunk renderer's per-frame poll: the poll is
        // a backstop, and the two places the bias reaches the GPU - the atlas
        // textures and the terrain sampler objects - have to follow the plan
        // whether or not a chunk pass ran. Nothing is rebuilt and no shader is
        // reloaded; a bias that did not move makes no call at all.
        ShaderRegistry.ApplyOptimumLodBias();

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
            !device.UpscaleDepthNearest(primary.DepthTextureId, target.DepthTextureId))
        {
            // A refused blit must not leave the display-resolution depth as it was: on
            // the first frame it is undefined, on every frame after that it is the
            // previous frame's silhouettes, and the late 3D overlays test against it
            // either way. Far plane, every affected frame, so the overlays lose their
            // depth occlusion instead of testing against depth that belongs to no
            // geometry in this frame.
            device.ClearDepthImageToFar(target.DepthTextureId);
            if (!upscaleDepthRefused)
            {
                upscaleDepthRefused = true;
                LogUpscaler("[Optimum] DLSS: this driver refuses a depth blit, so the late 3D overlays " +
                    "(selection outline and similar) are drawn without depth occlusion: their " +
                    "display-resolution depth is cleared to the far plane instead of upscaled.");
            }
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
