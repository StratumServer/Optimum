using System.Collections.Generic;
using System.Globalization;
using Optimum.Render.Vulkan.Graph;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 2 step 2: the platform declares the frame's passes in frame order.
// A pass is (context, bound target): the context is the render stage (from the C3 bracket) or
// the post method running (OIT merge, TAA resolve and sharpen, post-processing, final
// composition, blit, sky motion, liquid motion); every bind through the CurrentFrameBuffer
// setters declares the pass for the new target. Reads are the textures the base's pass body
// binds, so the pass opens with them already shader-readable. Mod-hosted stages sample
// anything and use OpenSampling and AllowSplit. With OPTIMUM_VULKAN_FRAMEGRAPH=0 the device
// ignores declarations and the old scope inference runs.
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
            platform.GraphDevice?.EndPass();
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

    /// <summary>Starts a context and declares its pass on the bound target.</summary>
    private void SetPassContext(string context, PassFlags flags)
    {
        passContext = context;
        passContextFlags = flags;
        DeclareBoundPass();
    }

    /// <summary>Declares the (context, bound target) pass; a repeat of the current one changes nothing.</summary>
    private void DeclareBoundPass()
    {
        if (device == null || !device.FrameGraphEnabled) return;
        int index = FrameBufferIndexOf(device.BoundFramebufferId);
        string target = index >= 0
            ? index.ToString(CultureInfo.InvariantCulture)
            : device.BoundFramebufferId == device.DefaultFramebufferId
                ? "Default"
                : "fbo" + device.BoundFramebufferId.ToString(CultureInfo.InvariantCulture);
        device.DeclarePass(new PassDeclaration
        {
            Name = passContext + "/" + target,
            FramebufferId = PassDeclaration.BoundFramebuffer,
            Reads = PassReads(passContext, index),
            TransientSlots = PassTransientSlots(passContext, index),
            Flags = passContextFlags,
        });
    }

    /// <summary>
    /// The final composition writes Primary 0 while sampling Primary 1: an attachment-subset
    /// pass, Primary 1 out of the scope for the whole pass (one barrier each way per frame).
    /// </summary>
    private void DeclareFinalCompositionPass()
    {
        if (device == null || !device.FrameGraphEnabled) return;
        if (passContext == "Post")
        {
            // The pre-upscale AO multiply shares the colour-0 mask, but samples
            // only AO and preserves every other Primary attachment.
            var reads = new List<int>();
            AddColour(reads, SsaoBlurVerticalIndex, 0);
            device.DeclarePass(new PassDeclaration
            {
                Name = "UpscaleSsao/0",
                FramebufferId = PassDeclaration.BoundFramebuffer,
                ColorSlots = 1u,
                Reads = reads.ToArray(),
                Flags = PassFlags.None,
            });
            return;
        }
        List<FrameBufferRef> buffers = FrameBuffers;
        if (CurrentFrameBuffer != null && buffers != null && buffers.Count > PrimaryIndex &&
            !ReferenceEquals(CurrentFrameBuffer, buffers[PrimaryIndex]))
        {
            // DLSS plan, Phase 3: with an upscaler the composition writes the
            // display-resolution target instead. Nothing it samples lives in that
            // target, so the attachment-subset trick has no work to do and the pass is
            // an ordinary one writing its single attachment.
            device.DeclarePass(new PassDeclaration
            {
                Name = "FinalComposition/" + OptimumUpscaledSceneIndex.ToString(CultureInfo.InvariantCulture),
                FramebufferId = PassDeclaration.BoundFramebuffer,
                Reads = PassReads("FinalComposition", PrimaryIndex),
                Flags = PassFlags.None,
            });
            return;
        }
        device.DeclarePass(new PassDeclaration
        {
            Name = "FinalComposition/0",
            FramebufferId = PassDeclaration.BoundFramebuffer,
            ColorSlots = ~(1u << 1),
            Reads = PassReads("FinalComposition", PrimaryIndex),
            Flags = PassFlags.None,
        });
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
            AddColour(reads, OptimumUpscaledSceneIndex, 0);
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
        // DLSS plan, Phase 3: and the upscaled scene colour, which is what the post
        // chain reads on a frame an upscaler produced.
        AddColour(reads, OptimumUpscaledSceneIndex, 0);
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

    public override void MergeTransparentRenderPass()
    {
        SetPassContext("MergeTransparent", PassFlags.None);
        base.MergeTransparentRenderPass();
        SetPassContext("Frame", PassFlags.AllowSplit);
    }

    public override bool RenderOptimumSkyMotion()
    {
        SetPassContext("SkyMotion", PassFlags.None);
        bool drawn = base.RenderOptimumSkyMotion();
        SetPassContext("Frame", PassFlags.AllowSplit);
        return drawn;
    }

    public override void RenderPostprocessingEffects(float[] projectMatrix)
    {
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

    public override void RenderFinalComposition()
    {
        SetPassContext("FinalComposition", PassFlags.None);
        base.RenderFinalComposition();
        SetPassContext("Frame", PassFlags.AllowSplit);
    }

    public override void BlitPrimaryToDefault()
    {
        SetPassContext("Blit", PassFlags.None);
        base.BlitPrimaryToDefault();
        SetPassContext("Frame", PassFlags.AllowSplit);
    }
}
