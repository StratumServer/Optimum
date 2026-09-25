using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native render systems (docs/vulkan.md), stage 1: the blit to
// the swapchain-equivalent Default target runs natively. The three branches are the GL body's
// (ClientPlatformWindows.BlitPrimaryToDefault): the TAA debug view, FSR (EASU into the FSR
// target, RCAS into Default) and the plain blit, with the same conditions and uniform values.
// EASU reads Primary colour 0 after late overlays (decision 7); the plain blit reads slot 21 when
// the post-composition TAA sharpen ran. Each written target
// is one declared pass, and no draw between BeginNativePass and EndNativePass touches the GL
// state tracker, a texture unit or a draw-buffer mask.
public partial class VulkanClientPlatform
{
    /// <summary>
    /// False runs the OpenGL body on the Vulkan device instead of the native chain: the old
    /// route the parity tests compare the native one against.
    /// </summary>
    internal bool NativeBlitEnabled { get; set; } = true;

    /// <summary>
    /// The base's OffscreenBuffer, which is private there. Tracked through the one virtual
    /// that changes it, and starting where the base's own field does.
    /// </summary>
    private bool offscreenBufferActive = true;

    public override void ToggleOffscreenBuffer(bool enable)
    {
        offscreenBufferActive = enable;
        base.ToggleOffscreenBuffer(enable);
    }

    /// <summary>A native fullscreen program: its pipeline for the current target, and the placements its draws write through.</summary>
    private sealed class NativeFullscreenPass
    {
        public NativeFullscreenPass(string passName, string[] uniforms, string[] samplers)
        {
            PassName = passName;
            UniformNames = uniforms;
            SamplerNames = samplers;
            Uniforms = new NativeUniform[uniforms.Length];
            Samplers = new NativeSamplerSlot[samplers.Length];
        }

        public string PassName { get; }
        public string[] UniformNames { get; }
        public string[] SamplerNames { get; }

        /// <summary>Resolved once per pipeline, never by name per draw.</summary>
        public NativeUniform[] Uniforms;
        public NativeSamplerSlot[] Samplers;

        public NativePipeline? Pipeline;
        public RenderTargetFormats? Formats;
        public bool Reported;

        public void Adopt(NativePipeline pipeline, RenderTargetFormats formats)
        {
            Pipeline = pipeline;
            Formats = formats;
            for (int i = 0; i < UniformNames.Length; i++) Uniforms[i] = pipeline.Uniform(UniformNames[i]);
            for (int i = 0; i < SamplerNames.Length; i++) Samplers[i] = pipeline.Sampler(SamplerNames[i]);
        }
    }

    private readonly NativeFullscreenPass nativeTaaDebug =
        new("taa-debug", new[] { "mode", "renderSize" }, new[] { "motionTex", "depthTex", "sceneTex" });

    private readonly NativeFullscreenPass nativeFsrEasu =
        new("fsr-easu", new[] { "inputTexelSize" }, new[] { "inputScene" });

    private readonly NativeFullscreenPass nativeFsrRcas =
        new("fsr-rcas", new[] { "inputTexelSize" }, new[] { "inputScene" });

    private readonly NativeFullscreenPass nativeBlit =
        new("blit", Array.Empty<string>(), new[] { "scene" });

    /// <summary>Opaque fullscreen state: no blend, no depth, no culling, every channel written.</summary>
    private static AttachmentBlend[] OpaqueColorZero() => new[] { AttachmentBlend.Default };

    /// <summary>
    /// The pipeline for one fullscreen program against one target, rebuilt only when the
    /// program was relinked or the target's formats changed. Opaque unless the pass states a
    /// blend; a pass object always states the same one, so the blend is not part of the check.
    /// </summary>
    private NativePipeline? NativePipelineFor(NativeFullscreenPass pass, ShaderProgramBase program, int framebufferId,
        AttachmentBlend[]? blend = null)
    {
        RenderTargetFormats? formats = device.NativeTargetFormats(framebufferId, 1u);
        if (formats == null) return null;

        if (pass.Pipeline != null && pass.Pipeline.ProgramId == program.ProgramId &&
            formats.Equals(pass.Formats) && device.IsNativePipelineLive(pass.Pipeline))
        {
            return pass.Pipeline;
        }

        NativePipeline? pipeline = device.RequestNativePipeline(new NativePipelineDescription
        {
            ProgramId = program.ProgramId,
            PassName = pass.PassName,
            Blend = blend ?? OpaqueColorZero(),
            DepthTest = false,
            DepthWrite = false,
            Cull = CullModeFlags.None,
            Topology = PrimitiveTopology.TriangleList,
            Targets = formats,
        }, out string error);

        if (pipeline == null)
        {
            if (!pass.Reported)
            {
                pass.Reported = true;
                Logger.Warning("Optimum: no native pipeline for '{0}': {1}", pass.PassName, error);
            }
            pass.Pipeline = null;
            return null;
        }

        pass.Reported = false;
        pass.Adopt(pipeline, formats);
        return pipeline;
    }

    /// <summary>The Default target, as a native pass names it.</summary>
    private const int NativeDefaultTarget = PassDeclaration.DefaultFramebuffer;

    private bool BeginNativeBlitPass(string name, int framebufferId, int width, int height, int[] reads) =>
        device.BeginNativePass(new NativePassDescription
        {
            Name = name,
            FramebufferId = framebufferId,
            ColorSlots = 1u,
            Reads = reads,
            Flags = PassFlags.None,
            ViewportWidth = width,
            ViewportHeight = height,
        });

    /// <summary>
    /// Leaves the stated state where the OpenGL body leaves it, so everything the
    /// client draws after the blit (the ortho GUI pass) sees what it always saw: the Default
    /// target bound, the viewport on the window, and blending back on where the body turned
    /// it off. Outside every native pass.
    /// </summary>
    private void FinishNativeBlit(bool restoreBlend)
    {
        passContext = "Frame";
        passContextFlags = PassFlags.AllowSplit;
        LoadFrameBuffer(EnumFrameBuffer.Default);
        if (restoreBlend) GlToggleBlend(true);
    }

    /// <summary>The native blit: the OpenGL body's three branches, drawn through the native device API.</summary>
    private void RenderNativeBlit()
    {
        if (!offscreenBufferActive) return;

        List<FrameBufferRef> buffers = FrameBuffers;
        FrameBufferRef primary = buffers[0];
        int scene2D = primary.ColorTextureIds[0];
        Size2i client = OptimumWindowClientSize();

        // TAA debug views (P1): bypasses FSR and the blit entirely, exactly as the GL body does.
        if (OptimumConfig.TaaDebugView != 0 && MotionAttachmentIndex >= 0)
        {
            ShaderProgram taaDebug = ShaderPrograms.TaaDebug;
            if (taaDebug != null && !taaDebug.LoadError)
            {
                int motion = primary.ColorTextureIds[MotionAttachmentIndex];
                int depth = primary.DepthTextureId;
                NativePipeline? pipeline = NativePipelineFor(nativeTaaDebug, taaDebug, NativeDefaultTarget);
                if (pipeline != null &&
                    BeginNativeBlitPass("Blit/Default", NativeDefaultTarget, client.Width, client.Height,
                        new[] { motion, depth, scene2D }))
                {
                    device.WriteNative(pipeline, nativeTaaDebug.Uniforms[0], OptimumConfig.TaaDebugView);
                    device.WriteNative(pipeline, nativeTaaDebug.Uniforms[1], primary.Width, primary.Height);
                    device.DrawNativeFullscreen(pipeline, new[]
                    {
                        new NativeTexture(nativeTaaDebug.Samplers[0], motion),
                        new NativeTexture(nativeTaaDebug.Samplers[1], depth),
                        new NativeTexture(nativeTaaDebug.Samplers[2], scene2D),
                    });
                }
                device.EndNativePass();
                NotePostStep(NativePostStep.Blit);
                FinishNativeBlit(restoreBlend: false);
                return;
            }
        }

        // AfterFinalComposition renderers draw onto Primary between FinalComposition and this
        // method. Sharpen only now, after those overlays are complete; FSR owns RCAS instead.
        bool useFsr = OptimumFsrBlitActive();
        if (!useFsr)
        {
            scene2D = RenderOptimumTaaSharpen(scene2D);
        }
        NotePostStep(NativePostStep.Blit);
        if (useFsr)
        {
            FrameBufferRef fsrTarget = buffers[OptimumFsrFramebufferIndex];
            try
            {
                ShaderProgram fsrEasu = ShaderPrograms.FsrEasu;
                ShaderProgram fsrRcas = ShaderPrograms.FsrRcas;
                int fsrColor = fsrTarget.ColorTextureIds[0];

                // EASU upsamples Primary colour 0 into the FSR target (decision 7).
                NativePipeline? easu = NativePipelineFor(nativeFsrEasu, fsrEasu, fsrTarget.FboId);
                if (easu != null &&
                    BeginNativeBlitPass("Blit/" + OptimumFsrFramebufferIndex, fsrTarget.FboId,
                        fsrTarget.Width, fsrTarget.Height, new[] { scene2D }))
                {
                    device.WriteNative(easu, nativeFsrEasu.Uniforms[0], 1f / primary.Width, 1f / primary.Height);
                    device.DrawNativeFullscreen(easu, new[]
                    {
                        new NativeTexture(nativeFsrEasu.Samplers[0], scene2D),
                    });
                }
                device.EndNativePass();

                // RCAS sharpens the upsampled image into Default.
                NativePipeline? rcas = NativePipelineFor(nativeFsrRcas, fsrRcas, NativeDefaultTarget);
                if (rcas != null &&
                    BeginNativeBlitPass("Blit/Default", NativeDefaultTarget, client.Width, client.Height, new[] { fsrColor }))
                {
                    device.WriteNative(rcas, nativeFsrRcas.Uniforms[0], 1f / fsrTarget.Width, 1f / fsrTarget.Height);
                    device.DrawNativeFullscreen(rcas, new[]
                    {
                        new NativeTexture(nativeFsrRcas.Samplers[0], fsrColor),
                    });
                }
                device.EndNativePass();
                FinishNativeBlit(restoreBlend: true);
                return;
            }
            catch (Exception error)
            {
                device.EndNativePass();
                DisableOptimumFsr(error);
                FinishNativeBlit(restoreBlend: true);
            }
        }

        // The TAA sharpen is deliberately after final composition and its late overlays. A plain
        // blit therefore reads its dedicated target, while FSR above keeps Primary as input and
        // supplies the one RCAS pass at native resolution.
        int finalScene = NativeFinalBlitSceneTexture(scene2D, useFsr);
        ShaderProgramBlit blit = ShaderPrograms.Blit;
        NativePipeline? plain = NativePipelineFor(nativeBlit, blit, NativeDefaultTarget);
        if (plain != null &&
            BeginNativeBlitPass("Blit/Default", NativeDefaultTarget, client.Width, client.Height, new[] { finalScene }))
        {
            device.DrawNativeFullscreen(plain, new[]
            {
                new NativeTexture(nativeBlit.Samplers[0], finalScene),
            });
        }
        device.EndNativePass();
        FinishNativeBlit(restoreBlend: false);
    }

    /// <summary>
    /// Returns the post-composition TAA sharpen target for the plain blit, when the same guards
    /// that ran <see cref="RenderOptimumTaaSharpen" /> prove that this frame produced it. FSR is
    /// checked first: its EASU/RCAS branch owns the final sharpening and must consume Primary's
    /// unsharpened composition.
    /// </summary>
    private int NativeFinalBlitSceneTexture(int fallback, bool fsrActive)
    {
        ShaderProgram sharpen = ShaderPrograms.TaaSharpen;
        List<FrameBufferRef> buffers = FrameBuffers;
        FrameBufferRef target = buffers != null && buffers.Count > OptimumTaaSharpenIndex
            ? buffers[OptimumTaaSharpenIndex]
            : null!;
        bool targetAvailable = sharpen != null && !sharpen.LoadError && target != null &&
            !target.Disposed && target.ColorTextureIds != null && target.ColorTextureIds.Length > 0;
        int sharpened = targetAvailable ? target.ColorTextureIds[0] : fallback;
        return OptimumApiBridge.SelectTaaPresentationTexture(
            fallback, sharpened, fsrActive, TaaResolvedThisFrame, OptimumConfig.TaaSharpness,
            targetAvailable);
    }
}
