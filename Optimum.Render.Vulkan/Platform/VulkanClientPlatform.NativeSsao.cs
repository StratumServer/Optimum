using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Optimum.Render.Vulkan.AmbientOcclusion;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native render systems (docs/vulkan.md), Phase 3b stage 1c: the
// post chain's ambient-occlusion step drawn natively - the vanilla SSAO pass, its bilateral blur
// ping-pong and the AO composite that multiplies the visibility into the scene before the TAA
// resolve reads it.
//
// The step is the OpenGL body's (ClientPlatformWindows.OptimumPostAmbientOcclusion and the
// private ApplyOptimumSceneSsao it calls), value for value: the same guards, the same order, the
// same uniform expressions, the same textures. What changes is how each draw reaches the GPU -
// a pipeline built for stated fixed state (per-attachment blend, depth test/write/compare, cull,
// topology and the target's formats) instead of whatever the GL state tracker happens to hold,
// a pass that names its target, its written colour slots and the textures it samples instead of
// a draw-buffer mask, uniforms written by resolved placement instead of by name, and sampled
// textures resolved straight to bindless slots.
//
// Both AO modes run here. Vanilla SSAO renders into frameBuffers[13], is blurred through 15/14
// and composed from 14; Optimum's GTAO (GtaoRenderer, already native and untouched by this file)
// hands the composite its visibility texture instead and the composite takes the OPTIMUMAO branch
// with optimumAoMode = 1. Either way the step ends by recording that AO is in the scene, which is
// what keeps the final composition from applying it a second time.
public partial class VulkanClientPlatform
{
    private readonly NativeFullscreenPass nativeSsao = new("ssao",
        new[] { "screenSize", "projection", "samples", "temporalFrameIndex" },
        new[] { "gPosition", "gNormal", "texNoise", "revealage" });

    private readonly NativeFullscreenPass nativeBilateralBlur = new("bilateralblur",
        new[] { "frameSize", "isVertical" },
        new[] { "inputTexture", "depthTexture" });

    private readonly NativeFullscreenPass nativeSceneSsao = new("scene-ssao",
        new[] { "invRenderHeight", "optimumAoMode" },
        new[] { "ssaoScene", "gPositionScene", "revealageScene" });

    /// <summary>The frame buffer indices this step draws into and reads back, as the base indexes them.</summary>
    private const int NativeSsaoTargetIndex = 13;
    private const int NativeSsaoBlurVerticalIndex = 14;
    private const int NativeSsaoBlurHorizontalIndex = 15;

    /// <summary>
    /// The AO step, natively. Reproduces
    /// <see cref="ClientPlatformWindows.OptimumPostAmbientOcclusion" /> exactly: the platform's own
    /// AO first, vanilla SSAO and its blur when that stood down, then the composite - under the
    /// vanilla branch only while TAA is actually running, under the GTAO branch always.
    /// </summary>
    private void NativeAmbientOcclusion(float[] projectMatrix)
    {
        OptimumPostSsaoInScene = false;
        OptimumPostAmbientOcclusionTexture = 0;
        if (OptimumRenderSsao && projectMatrix != null)
        {
            OptimumPostAmbientOcclusionTexture = RenderOptimumAmbientOcclusion(projectMatrix);
        }

        if (OptimumPostAmbientOcclusionTexture == 0 && OptimumRenderSsao && projectMatrix != null)
        {
            Size2i client = OptimumWindowClientSize();
            float ssaa = OptimumPostSsaaLevel;

            // Outside every native pass, exactly where the OpenGL body puts them: this is the
            // GL-shaped state the steps after this one inherit.
            GlToggleBlend(on: false);
            NativeVanillaSsaoPass(projectMatrix, client, ssaa);
            NativeBilateralBlurPasses();
            // The body's tail: the blur's last target is what it leaves bound, with the viewport
            // back at full render resolution - the Luma step inherits that viewport.
            LoadFrameBuffer(EnumFrameBuffer.SSAOBlurVertical);
            GlToggleBlend(on: true);
            GlViewport(0, 0, (int)(ssaa * client.Width), (int)(ssaa * client.Height));
            if (OptimumTaaRequested && TaaTargetsReady)
            {
                NativeSceneSsaoPass();
            }
        }

        if (OptimumPostAmbientOcclusionTexture != 0)
        {
            NativeSceneSsaoPass();
        }
    }

    /// <summary>
    /// Whether the native route can run this step at all. The shader programs are the OpenGL
    /// body's own - it uses them unguarded - so a frame that has none of them falls back to the
    /// body rather than drawing nothing.
    /// </summary>
    private bool NativeAmbientOcclusionReady()
    {
        if (device == null) return false;
        List<FrameBufferRef> buffers = FrameBuffers;
        if (buffers == null || buffers.Count <= NativeSsaoBlurHorizontalIndex) return false;
        if (buffers[0] == null || buffers[1] == null) return false;
        if (!OptimumRenderSsao) return true;

        FrameBufferRef primary = buffers[0];
        if (primary.ColorTextureIds == null || primary.ColorTextureIds.Length < 4) return false;
        if (buffers[NativeSsaoTargetIndex] == null || buffers[NativeSsaoBlurVerticalIndex] == null ||
            buffers[NativeSsaoBlurHorizontalIndex] == null)
        {
            return false;
        }

        ShaderProgramSsao ssao = ShaderPrograms.Ssao;
        ShaderProgramBilateralblur blur = ShaderPrograms.Bilateralblur;
        if (ssao == null || ssao.LoadError || ssao.Disposed) return false;
        if (blur == null || blur.LoadError || blur.Disposed) return false;
        return true;
    }

    // ------------------------------------------------------------------ 1: vanilla SSAO

    /// <summary>
    /// The raw SSAO pass. One colour slot on frameBuffers[13], cleared white at pass entry the
    /// way the body's ClearSsaoTarget clears it, no blend, no depth, and the viewport the body's
    /// LoadFrameBuffer(SSAO) case sets - the target's own size. The four samplers and the four
    /// uniform values are the body's, including the half-resolution screenSize fudge and the
    /// temporal dither index, which is written under exactly the condition that compiles it in.
    /// </summary>
    private void NativeVanillaSsaoPass(float[] projectMatrix, Size2i client, float ssaa)
    {
        List<FrameBufferRef> buffers = FrameBuffers;
        FrameBufferRef primary = buffers[0];
        FrameBufferRef transparent = buffers[1];
        FrameBufferRef target = buffers[NativeSsaoTargetIndex];

        NativePipeline? pipeline = NativePostPipeline(nativeSsao, ShaderPrograms.Ssao, target.FboId, 1u,
            NativeOpaqueSlotZeroBlend(), depthTest: false, depthWrite: false, CompareOp.Less);
        if (pipeline == null) return;

        int gNormal = primary.ColorTextureIds[2];
        int gPosition = primary.ColorTextureIds[3];
        int noise = target.ColorTextureIds[1];
        int revealage = transparent.ColorTextureIds[1];

        if (BeginNativeAoPass("SSAO/" + NativeSsaoTargetIndex, target.FboId, target.Width, target.Height,
                new[] { gPosition, gNormal, noise, revealage }, clearWhite: true))
        {
            // screenSize: the body's num is 0.5 at SSAA 1 and 1 otherwise, so the value is the
            // SSAO target's resolution at SSAA 1 and the full render resolution above it.
            float half = ssaa == 1f ? 0.5f : 1f;
            device.WriteNative(pipeline, nativeSsao.Uniforms[0], ssaa * client.Width * half, ssaa * client.Height * half);
            WriteNativeFloats(pipeline, nativeSsao.Uniforms[1], projectMatrix);
            WriteNativeFloats(pipeline, nativeSsao.Uniforms[2], OptimumSsaoKernel);
            if (OptimumConfig.EffectiveTaa)
            {
                device.WriteNative(pipeline, nativeSsao.Uniforms[3],
                    (float)(OptimumTemporal.Frame.FrameIndex & 1023L));
            }
            device.DrawNativeFullscreen(pipeline, new[]
            {
                new NativeTexture(nativeSsao.Samplers[0], gPosition),
                new NativeTexture(nativeSsao.Samplers[1], gNormal),
                new NativeTexture(nativeSsao.Samplers[2], noise),
                new NativeTexture(nativeSsao.Samplers[3], revealage),
            });
        }
        device.EndNativePass();
    }

    // ------------------------------------------------------------------ 2: bilateral blur

    /// <summary>
    /// The bilateral blur ping-pong: one horizontal half-iteration into frameBuffers[15] and one
    /// vertical into frameBuffers[14], once at SSAO quality 1 and three times otherwise. Each
    /// half-iteration is its own pass, because each writes a target the next one samples.
    ///
    /// frameSize is the body's: frameBuffers[15]'s size, captured once and reused by every
    /// half-iteration including the vertical ones that write frameBuffers[14]. The two targets are
    /// built at the same size, so this is a value, not a bug to fix - and it is reproduced, not
    /// corrected. depthTexture is likewise bound on both halves: the body sets it on the
    /// horizontal half only and the vertical half inherits the same binding from the program.
    /// </summary>
    private void NativeBilateralBlurPasses()
    {
        List<FrameBufferRef> buffers = FrameBuffers;
        FrameBufferRef primary = buffers[0];
        FrameBufferRef horizontal = buffers[NativeSsaoBlurHorizontalIndex];
        FrameBufferRef vertical = buffers[NativeSsaoBlurVerticalIndex];

        NativePipeline? pipeline = NativePostPipeline(nativeBilateralBlur, ShaderPrograms.Bilateralblur,
            horizontal.FboId, 1u, NativeOpaqueSlotZeroBlend(), depthTest: false, depthWrite: false, CompareOp.Less);
        if (pipeline == null) return;

        int depth = primary.DepthTextureId;
        int iterations = ClientSettings.SSAOQuality == 1 ? 1 : 3;
        for (int i = 0; i < iterations; i++)
        {
            int source = buffers[i == 0 ? NativeSsaoTargetIndex : NativeSsaoBlurVerticalIndex].ColorTextureIds[0];
            NativeBilateralBlurHalf(pipeline, horizontal, source, depth, horizontal, isVertical: 0);
            NativeBilateralBlurHalf(pipeline, vertical, horizontal.ColorTextureIds[0], depth, horizontal, isVertical: 1);
        }
    }

    /// <summary>One half-iteration of the blur: one target, one input, the shared frameSize.</summary>
    private void NativeBilateralBlurHalf(NativePipeline pipeline, FrameBufferRef target, int source, int depth,
        FrameBufferRef frameSizeSource, int isVertical)
    {
        if (!BeginNativeAoPass("SSAOBlur/" + target.FboId, target.FboId, target.Width, target.Height,
                new[] { source, depth }, clearWhite: false))
        {
            device.EndNativePass();
            return;
        }

        device.WriteNative(pipeline, nativeBilateralBlur.Uniforms[0],
            (float)frameSizeSource.Width, (float)frameSizeSource.Height);
        device.WriteNative(pipeline, nativeBilateralBlur.Uniforms[1], isVertical);
        device.DrawNativeFullscreen(pipeline, new[]
        {
            new NativeTexture(nativeBilateralBlur.Samplers[0], source),
            new NativeTexture(nativeBilateralBlur.Samplers[1], depth),
        });
        device.EndNativePass();
    }

    // ------------------------------------------------------------------ 3: the AO composite

    /// <summary>
    /// The AO composite, natively: the visibility term multiplied into Primary colour 0 and
    /// nothing else, before the TAA resolve reads that colour.
    ///
    /// The Multiply blend is the pipeline's - dst * (1 - srcAlpha), which is what
    /// GlToggleBlend(true, EnumBlendMode.Multiply) sets - and the single written colour slot is
    /// the pass's, not a draw-buffer mask. That is also what lets the pass sample Primary's
    /// G-buffer position attachment: a slot the pass leaves out is not part of its scope, so it
    /// is read as a texture rather than being feedback.
    /// </summary>
    private void NativeSceneSsaoPass()
    {
        ShaderProgram composite = ShaderPrograms.SceneSsao;
        if (composite == null || composite.LoadError || composite.Disposed) return;

        List<FrameBufferRef> buffers = FrameBuffers;
        FrameBufferRef primary = buffers[0];
        FrameBufferRef transparent = buffers[1];

        int aoTexture = OptimumPostAmbientOcclusionTexture;
        if (aoTexture == 0)
        {
            FrameBufferRef blurred = buffers[NativeSsaoBlurVerticalIndex];
            if (blurred?.ColorTextureIds == null || blurred.ColorTextureIds.Length == 0) return;
            aoTexture = blurred.ColorTextureIds[0];
        }

        bool gtao = OptimumConfig.AmbientOcclusionShadersUseGtao;
        int gPosition = gtao ? primary.ColorTextureIds[3] : 0;
        int revealage = gtao ? transparent.ColorTextureIds[1] : 0;

        NativePipeline? pipeline = NativePostPipeline(nativeSceneSsao, composite, primary.FboId, 1u,
            NativeMultiplySlotZeroBlend(), depthTest: false, depthWrite: false, CompareOp.Less);
        if (pipeline == null) return;

        // The body binds Primary through LoadFrameBuffer here, which is also what puts the
        // viewport back at full render resolution for the steps that follow.
        LoadFrameBuffer(EnumFrameBuffer.Primary);

        var reads = new List<int> { aoTexture };
        if (gtao)
        {
            reads.Add(gPosition);
            reads.Add(revealage);
        }

        if (BeginNativeAoPass("SceneSsao/" + primary.FboId, primary.FboId, primary.Width, primary.Height,
                reads.ToArray(), clearWhite: false))
        {
            var textures = new List<NativeTexture> { new(nativeSceneSsao.Samplers[0], aoTexture) };
            if (gtao)
            {
                textures.Add(new NativeTexture(nativeSceneSsao.Samplers[1], gPosition));
                textures.Add(new NativeTexture(nativeSceneSsao.Samplers[2], revealage));
                device.WriteNative(pipeline, nativeSceneSsao.Uniforms[1],
                    OptimumPostAmbientOcclusionTexture != 0 ? 1 : 0);
            }
            device.WriteNative(pipeline, nativeSceneSsao.Uniforms[0], 1f / primary.Height);
            device.DrawNativeFullscreen(pipeline, textures.ToArray());
        }
        device.EndNativePass();

        // The net GL-shaped state the body's composite leaves: blending back on in the standard
        // mode and the depth test back on. The draw-buffer mask is not restored because it was
        // never narrowed - the written slot is the pass's, so the world mask never moved.
        GlToggleBlend(on: true);
        GlEnableDepthTest();
        OptimumPostSsaoInScene = true;
    }

    // ------------------------------------------------------------------ shared plumbing

    /// <summary>Colour slot 0 alone, opaque: no blend, every channel written, every other slot masked out.</summary>
    private static AttachmentBlend[] NativeOpaqueSlotZeroBlend() => new[] { AttachmentBlend.Default };

    /// <summary>
    /// Colour slot 0 alone with EnumBlendMode.Multiply: glBlendFuncSeparate(ZERO,
    /// ONE_MINUS_SRC_ALPHA, ONE, ONE_MINUS_SRC_ALPHA) over glBlendEquation(FUNC_ADD).
    /// </summary>
    private static AttachmentBlend[] NativeMultiplySlotZeroBlend()
    {
        AttachmentBlend blend = AttachmentBlend.Default;
        blend.Enabled = true;
        blend.SrcColor = BlendFactor.Zero;
        blend.DstColor = BlendFactor.OneMinusSrcAlpha;
        blend.ColorOp = BlendOp.Add;
        blend.SrcAlpha = BlendFactor.One;
        blend.DstAlpha = BlendFactor.OneMinusSrcAlpha;
        blend.AlphaOp = BlendOp.Add;
        return new[] { blend };
    }

    /// <summary>
    /// A pass of this step: one written colour slot, its own viewport (every target here is drawn
    /// at its own size, which is what the body's LoadFrameBuffer cases set), its reads, and the
    /// white clear the SSAO target starts from.
    /// </summary>
    private bool BeginNativeAoPass(string name, int framebufferId, int width, int height, int[] reads, bool clearWhite) =>
        device.BeginNativePass(new NativePassDescription
        {
            Name = name,
            FramebufferId = framebufferId,
            ColorSlots = 1u,
            Reads = reads,
            Flags = PassFlags.None,
            ClearSlots = clearWhite ? 1u : 0u,
            ClearValue = new[] { 1f, 1f, 1f, 1f },
            ViewportWidth = width,
            ViewportHeight = height,
        });

}

// Optimum AO (docs/vulkan.md#ambient-occlusion section C): the GTAO visibility-bitmask pass
// on the device. The base's RenderPostprocessingEffects asks for it where vanilla SSAO would run
// and composes the returned texture through ApplyOptimumSceneSsao before the TAA resolve.
public partial class VulkanClientPlatform
{
    private GtaoRenderer? ambientOcclusion;

    /// <summary>This frame's output, or 0 when GTAO did not run (the debug outputs and the compose pass's reads key off it).</summary>
    private int ambientOcclusionOutput;

    /// <summary>The output texture whose sampler state was last set up.</summary>
    private int ambientOcclusionSampledTexture;

    /// <summary>Set when the passes cannot run on this device; vanilla SSAO then runs for the session.</summary>
    private string? ambientOcclusionFailure;

    private bool ambientOcclusionToneRefusalLogged;

    private (string Preset, bool Temporal, GtaoSettings Settings)? ambientOcclusionSettingsCache;

    /// <summary>
    /// Runs GTAO when the live shaders were built for it (OPTIMUMAO, stamped from
    /// <see cref="OptimumConfig.EffectiveGtao" /> and the SSAO G-buffer's condition) and returns
    /// the denoised visibility; 0 hands the frame to vanilla SSAO.
    /// </summary>
    public override int RenderOptimumAmbientOcclusion(float[] projectMatrix)
    {
        ambientOcclusionOutput = 0;
        if (device == null || projectMatrix == null || ambientOcclusionFailure != null) return 0;
        if (!OptimumConfig.AmbientOcclusionShadersUseGtao) return 0;
        FrameBufferRef? primary = FrameBuffers is { Count: > 0 } buffers ? buffers[0] : null;
        if (primary?.ColorTextureIds == null || primary.ColorTextureIds.Length < 4 || primary.DepthTextureId == 0) return 0;

        // Avoid feeding high-variance rotating AO into the scene TAA. Keep
        // the AO sampling phase fixed and denoise before scene composition.
        bool temporal = OptimumConfig.EffectiveTaa && TaaTargetsReady;
        GtaoSettings settings = AmbientOcclusionSettings(temporal);
        if (!ambientOcclusionToneRefusalLogged && settings.EffectiveTone(0, out string? refusal) != settings.Tone)
        {
            ambientOcclusionToneRefusalLogged = true;
            Logger.Warning("[Optimum] AO: " + refusal);
        }
        const uint noiseIndex = 0u;

        ambientOcclusion ??= new GtaoRenderer(device);
        int output = ambientOcclusion.Render(primary.DepthTextureId, primary.ColorTextureIds[2], projectMatrix, settings, noiseIndex);
        if (output == 0)
        {
            if (ambientOcclusion.ProgramsFailed)
            {
                ambientOcclusionFailure = ambientOcclusion.LastError ?? "unknown";
                Logger.Error("[Optimum] AO: GTAO is unavailable on this device, vanilla SSAO runs instead: " + ambientOcclusionFailure);
            }
            return 0;
        }
        if (output != ambientOcclusionSampledTexture)
        {
            // Composed with texelFetch at the same resolution; nearest and clamp keep any sampling exact.
            SetupOptimumTextureSampler(output, 9728, 33071);
            ambientOcclusionSampledTexture = output;
        }
        ambientOcclusionOutput = output;
        return output;
    }

    /// <summary>The debug outputs of this frame in the base's index order (working term, edges, depth level 0, output).</summary>
    public override int OptimumAmbientOcclusionDebugTexture(int index)
    {
        if (ambientOcclusionOutput == 0 || ambientOcclusion == null) return 0;
        return index switch
        {
            0 => ambientOcclusion.WorkingTermTexture,
            1 => ambientOcclusion.EdgesTexture,
            2 => ambientOcclusion.WorkingDepthTexture,
            3 => ambientOcclusion.OutputTexture,
            _ => 0,
        };
    }

    /// <summary>The preset with the measurement overrides from the environment, rebuilt only when the preset or TAA changes.</summary>
    private GtaoSettings AmbientOcclusionSettings(bool temporal)
    {
        string preset = OptimumConfig.AmbientOcclusionPreset ?? "";
        if (ambientOcclusionSettingsCache is { } cached && cached.Preset == preset && cached.Temporal == temporal)
        {
            return cached.Settings;
        }
        GtaoPreset parsed = GtaoSettings.ParsePreset(preset);
        GtaoSettings settings = (temporal
                ? GtaoSettings.ForStableTemporal(parsed)
                : GtaoSettings.ForPreset(parsed, temporal: false))
            .WithEnvironment(Environment.GetEnvironmentVariable);
        ambientOcclusionSettingsCache = (preset, temporal, settings);
        return settings;
    }

    /// <summary>The size-dependent targets go with the framebuffers; the next frame recreates them.</summary>
    private void ReleaseAmbientOcclusionTargets()
    {
        ambientOcclusion?.ReleaseTargets();
        ambientOcclusionOutput = 0;
        ambientOcclusionSampledTexture = 0;
    }

    private void ReleaseAmbientOcclusion()
    {
        ambientOcclusion?.Dispose();
        ambientOcclusion = null;
        ambientOcclusionOutput = 0;
        ambientOcclusionSampledTexture = 0;
    }
}
