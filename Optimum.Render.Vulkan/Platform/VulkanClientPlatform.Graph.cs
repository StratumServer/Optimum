using System.Collections.Generic;
using System.Globalization;
using Optimum.Render.Vulkan.Graph;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 2 step 2, as it stands after the emulation layer went: every pass is
// declared by the native route that records it (BeginNativePass), in frame order. The pass
// context below is only the naming prefix and flag set of the stage or post method running -
// the render stage (from the C3 bracket), or the OIT merge, TAA resolve and sharpen,
// post-processing, final composition, blit, sky motion and liquid motion - which the entity
// route names its coalesced pass after. Binds declare nothing.
public partial class VulkanClientPlatform
{
    // ClientPlatformWindows' EnumFrameBuffer slots the post chain indexes.
    private const int PrimaryIndex = 0;
    private const int TransparentIndex = 1;
    private const int BlurHorizontalMedResIndex = 2;
    private const int BlurVerticalMedResIndex = 3;
    private const int FindBrightIndex = 4;
    private const int LiquidDepthIndex = 5;
    private const int GodRaysIndex = 7;
    private const int BlurVerticalLowResIndex = 8;
    private const int BlurHorizontalLowResIndex = 9;
    private const int LumaIndex = 10;
    private const int ShadowFarIndex = 11;
    private const int ShadowNearIndex = 12;
    private const int SsaoIndex = 13;
    private const int SsaoBlurVerticalIndex = 14;
    private const int SsaoBlurHorizontalIndex = 15;

    private string passContext = "Frame";
    private PassFlags passContextFlags = PassFlags.AllowSplit;

    /// <summary>Forwards the render-stage bracket to the pass declarations.</summary>
    private sealed class FrameGraphStageListener : IRenderStageListener
    {
        private readonly VulkanClientPlatform platform;

        public FrameGraphStageListener(VulkanClientPlatform platform) => this.platform = platform;

        public void OnBeginRenderStage(EnumRenderStage stage)
        {
            platform.SetPassContext(stage.ToString(), StageFlags(stage));
        }

        public void OnEndRenderStage(EnumRenderStage stage)
        {
            platform.GraphDevice?.EndStagePass();
            // The liquid motion pass runs right after the AfterOIT renderers (ClientMain.MainRenderLoop).
            platform.SetPassContext(stage == EnumRenderStage.AfterOIT ? "LiquidMotion" : "Frame", PassFlags.AllowSplit);
        }
    }

    private VulkanDevice? GraphDevice => device;

    /// <summary>World stages draw declared targets; everything a mod hosts may sample anything.</summary>
    internal static PassFlags StageFlags(EnumRenderStage stage) => stage switch
    {
        EnumRenderStage.Before or EnumRenderStage.ShadowFar or EnumRenderStage.ShadowFarDone or
            EnumRenderStage.ShadowNear or EnumRenderStage.ShadowNearDone or EnumRenderStage.Opaque or
            EnumRenderStage.OIT => PassFlags.AllowSplit,
        _ => PassFlags.OpenSampling | PassFlags.AllowSplit,
    };

    /// <summary>Starts a context: the prefix and flags of the passes recorded under it.</summary>
    private void SetPassContext(string context, PassFlags flags)
    {
        passContext = context;
        passContextFlags = flags;
    }

    /// <summary>
    /// The (context, current target) pass name. The entity route records every entity of a stage
    /// under it with the scope kept open, so RenderTargetManager.DeclarePass coalesces instead of
    /// ending the rendering scope and starting another per entity.
    /// </summary>
    private string BoundPassName()
    {
        int id = CurrentTargetId;
        int index = FrameBufferIndexOf(id);
        string target = index >= 0
            ? index.ToString(CultureInfo.InvariantCulture)
            : id == PassDeclaration.DefaultFramebuffer
                ? "Default"
                : "fbo" + id.ToString(CultureInfo.InvariantCulture);
        return passContext + "/" + target;
    }

    private int FrameBufferIndexOf(int framebufferId)
    {
        List<FrameBufferRef> buffers = FrameBuffers;
        if (buffers == null || framebufferId <= 0) return -1;
        for (int i = 0; i < buffers.Count; i++)
        {
            if (buffers[i] != null && buffers[i].FboId == framebufferId) return i;
        }
        return -1;
    }

    /// <summary>
    /// The textures the base's pass body samples for (context, target). Where the base picks
    /// one of several (the resolved or sharpened scene, either history parity) all candidates
    /// are listed: the set stays the same from frame to frame, which the plan needs.
    /// </summary>
    internal int[] PassReads(string context, int target)
    {
        var reads = new List<int>();
        switch (context)
        {
        case "MergeTransparent":
            AddColour(reads, TransparentIndex, 0);
            AddColour(reads, TransparentIndex, 1);
            AddColour(reads, TransparentIndex, 2);
            break;
        case "SkyMotion":
            AddColour(reads, TransparentIndex, 1);
            break;
        case "TaaResolve":
            AddColour(reads, PrimaryIndex, 0);
            AddColour(reads, PrimaryIndex, 1);
            // Absent motion attachment: the index stays -1.
            if (MotionAttachmentIndex > -1) AddColour(reads, PrimaryIndex, MotionAttachmentIndex);
            AddDepth(reads, PrimaryIndex);
            for (int slot = 0; slot < 3; slot++)
            {
                AddColour(reads, OptimumTaaHistoryIndexA, slot);
                AddColour(reads, OptimumTaaHistoryIndexB, slot);
            }
            break;
        case "TaaSharpen":
            AddColour(reads, OptimumTaaHistoryIndexA, 0);
            AddColour(reads, OptimumTaaHistoryIndexB, 0);
            break;
        case "Post":
            switch (target)
            {
            case FindBrightIndex:
            case GodRaysIndex:
            case LumaIndex:
                AddPostScene(reads);
                break;
            case BlurHorizontalMedResIndex:
                AddColour(reads, FindBrightIndex, 0);
                break;
            case BlurVerticalMedResIndex:
                AddColour(reads, BlurHorizontalMedResIndex, 0);
                break;
            case BlurHorizontalLowResIndex:
                AddColour(reads, BlurVerticalMedResIndex, 0);
                break;
            case BlurVerticalLowResIndex:
                AddColour(reads, BlurHorizontalLowResIndex, 0);
                break;
            case SsaoIndex:
                AddColour(reads, PrimaryIndex, 2);
                AddColour(reads, PrimaryIndex, 3);
                AddColour(reads, SsaoIndex, 1);
                AddColour(reads, TransparentIndex, 1);
                break;
            case SsaoBlurHorizontalIndex:
                AddColour(reads, SsaoIndex, 0);
                AddColour(reads, SsaoBlurVerticalIndex, 0);
                AddDepth(reads, PrimaryIndex);
                break;
            case SsaoBlurVerticalIndex:
                AddColour(reads, SsaoBlurHorizontalIndex, 0);
                break;
            }
            break;
        case "FinalComposition":
            AddColour(reads, LumaIndex, 0);
            AddColour(reads, BlurVerticalLowResIndex, 0);
            AddColour(reads, GodRaysIndex, 0);
            AddColour(reads, PrimaryIndex, 1);
            AddColour(reads, OptimumTaaHistoryIndexA, 1);
            AddColour(reads, OptimumTaaHistoryIndexB, 1);
            AddColour(reads, SsaoBlurVerticalIndex, 0);
            break;
        case "Blit":
            AddColour(reads, PrimaryIndex, 0);
            AddColour(reads, OptimumFsrFramebufferIndex, 0);
            break;
        case "Before":
        case "ShadowFar":
        case "ShadowNear":
        case "Opaque":
        case "OIT":
            AddDepth(reads, ShadowFarIndex);
            AddDepth(reads, ShadowNearIndex);
            AddDepth(reads, LiquidDepthIndex);
            break;
        }
        return reads.ToArray();
    }

    /// <summary>
    /// Slots a pass overwrites completely with blending off and never reads from an earlier
    /// frame: the bloom blur chain and the SSAO blur targets. The plan may load them DONT_CARE.
    /// </summary>
    internal static uint PassTransientSlots(string context, int target)
    {
        if (context != "Post") return 0;
        return target switch
        {
            FindBrightIndex or BlurHorizontalMedResIndex or BlurVerticalMedResIndex or BlurHorizontalLowResIndex or
                BlurVerticalLowResIndex or SsaoBlurHorizontalIndex or SsaoBlurVerticalIndex => 1u,
            _ => 0u,
        };
    }

    /// <summary>Every texture the post chain may read as the scene: Primary, the TAA histories, the sharpen output.</summary>
    private void AddPostScene(List<int> reads)
    {
        AddColour(reads, PrimaryIndex, 0);
        AddColour(reads, PrimaryIndex, 1);
        AddColour(reads, OptimumTaaHistoryIndexA, 0);
        AddColour(reads, OptimumTaaHistoryIndexA, 1);
        AddColour(reads, OptimumTaaHistoryIndexB, 0);
        AddColour(reads, OptimumTaaHistoryIndexB, 1);
        AddColour(reads, OptimumTaaSharpenIndex, 0);
    }

    private void AddColour(List<int> reads, int index, int slot)
    {
        List<FrameBufferRef> buffers = FrameBuffers;
        if (buffers == null || index < 0 || index >= buffers.Count) return;
        FrameBufferRef buffer = buffers[index];
        if (buffer?.ColorTextureIds == null || slot < 0 || slot >= buffer.ColorTextureIds.Length) return;
        int id = buffer.ColorTextureIds[slot];
        if (id > 0 && !reads.Contains(id)) reads.Add(id);
    }

    private void AddDepth(List<int> reads, int index)
    {
        List<FrameBufferRef> buffers = FrameBuffers;
        if (buffers == null || index < 0 || index >= buffers.Count || buffers[index] == null) return;
        int id = buffers[index].DepthTextureId;
        if (id > 0 && !reads.Contains(id)) reads.Add(id);
    }

    // ------------------------------------------------------------ post methods

    /// <summary>
    /// Phase 3b stage 1: the chain's first pass, drawn natively
    /// (VulkanClientPlatform.NativePostChain.cs). <see cref="NativePostChainEnabled" /> puts the
    /// whole chain back on the OpenGL body for the differential tests.
    /// </summary>
    public override void MergeTransparentRenderPass()
    {
        NotePostStep(NativePostStep.OitMerge);
        if (UseNativePostChain)
        {
            NativeOitMerge();
            return;
        }
        LegacyOitMerge();
    }

    /// <summary>Phase 3b stage 1: the chain's second pass, drawn natively.</summary>
    public override bool RenderOptimumSkyMotion()
    {
        NotePostStep(NativePostStep.SkyMotion);
        return UseNativePostChain ? NativeSkyMotion() : LegacySkyMotion();
    }

    /// <summary>
    /// Phase 3b stage 1: Optimum owns the post chain's order. The native route runs the steps
    /// this method holds - AO, TAA resolve and sharpen, bloom, god rays, the Luma step and the
    /// epilogue - and never calls base.
    /// </summary>
    public override void RenderPostprocessingEffects(float[] projectMatrix)
    {
        if (UseNativePostChain)
        {
            RunNativePostChain(projectMatrix);
            return;
        }
        SetPassContext("Post", PassFlags.None);
        base.RenderPostprocessingEffects(projectMatrix);
        SetPassContext("Frame", PassFlags.AllowSplit);
    }

    public override bool RenderOptimumTaaResolve()
    {
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        SetPassContext("TaaResolve", PassFlags.None);
        bool resolved = base.RenderOptimumTaaResolve();
        SetPassContext(outer, outerFlags);
        return resolved;
    }

    public override int RenderOptimumTaaSharpen(int resolvedScene)
    {
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        SetPassContext("TaaSharpen", PassFlags.None);
        int sharpened = base.RenderOptimumTaaSharpen(resolvedScene);
        SetPassContext(outer, outerFlags);
        return sharpened;
    }

    /// <summary>
    /// Phase 3b stage 1: the resolve's draw, natively. The lib body above keeps every temporal
    /// decision and every field it writes afterwards, so only the draw changes route.
    /// </summary>
    public override void OptimumTaaResolveDraw(FrameBufferRef write, FrameBufferRef read,
        float[] invViewProjJittered, float[] prevViewProj, bool reset)
    {
        if (UseNativePostChain)
        {
            NativeTaaResolve(write, read, invViewProjJittered, prevViewProj, reset);
            return;
        }
        base.OptimumTaaResolveDraw(write, read, invViewProjJittered, prevViewProj, reset);
    }

    /// <summary>Phase 3b stage 1: the sharpen's draw, natively.</summary>
    public override void OptimumTaaSharpenDraw(FrameBufferRef target, int resolvedScene)
    {
        if (UseNativePostChain)
        {
            NativeTaaSharpen(target, resolvedScene);
            return;
        }
        base.OptimumTaaSharpenDraw(target, resolvedScene);
    }

    /// <summary>
    /// Phase 3b stage 1: the chain's ninth pass, drawn natively - the attachment-subset pass that
    /// writes Primary colour 0 while sampling Primary colour 1 (VulkanClientPlatform.NativePostFinal.cs).
    /// </summary>
    public override void RenderFinalComposition()
    {
        NotePostStep(NativePostStep.FinalComposition);
        if (UseNativePostChain)
        {
            NativeFinalComposition();
        }
        else
        {
            LegacyFinalComposition();
        }
        // World/UI separation: the composited image holds the scene alone for exactly this long -
        // RenderAfterFinalComposition draws the world-space overlays onto it next.
        CaptureSceneNoHud();
    }

    /// <summary>
    /// Phase 3b: the blit runs natively (VulkanClientPlatform.NativeBlit.cs) - its own
    /// pipelines, one declared pass per written target, no GL-shaped call in between. The
    /// GL body stays reachable through <see cref="NativeBlitEnabled" /> for the old-route
    /// side of the parity tests.
    /// </summary>
    public override void BlitPrimaryToDefault()
    {
        NotePostStep(NativePostStep.Blit);
        if (NativeBlitEnabled && UseNativePostChain)
        {
            RenderNativeBlit();
        }
        else
        {
            SetPassContext("Blit", PassFlags.None);
            base.BlitPrimaryToDefault();
            SetPassContext("Frame", PassFlags.AllowSplit);
        }
        // World/UI separation: the boundary. Everything ScreenManager draws after this call - the
        // AfterBlit stage, the menu background, the Ortho stage - goes into the UI image, on every
        // route out of the blit (debug view, FSR, plain, no offscreen buffer).
        OpenUiScope();
    }
}
