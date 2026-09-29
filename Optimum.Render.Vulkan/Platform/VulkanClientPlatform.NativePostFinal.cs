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

// Vulkan-native render systems (docs/vulkan.md), stage 1: the tail of the
// post chain - the bloom chain, god rays, the FXAA luma step and final composition - drawn
// through the native device API instead of the GL-shaped platform calls.
//
// Every pass here follows VulkanClientPlatform.NativeBlit.cs: a pipeline requested with its fixed
// state stated outright (per-attachment blend, depth test/write/compare, cull, topology, target
// formats), a pass declared with its explicit reads and colour slots, uniforms written by
// placement and sampled textures resolved straight to bindless slots. The values are the ones the
// OpenGL body computes, read from client state (decision 3): OptimumRenderBloom,
// OptimumRenderGodRays, OptimumRenderFxaa, OptimumSsaaLevel, OptimumRenderSsao,
// OptimumAmbientOcclusionTexture and OptimumSsaoInScene, plus ClientSettings and ShaderUniforms.
//
// The GL-shaped state calls the OpenGL body makes around these passes stay, outside every native
// pass: they are what the rest of the frame - the chain's epilogue, the GUI stage, a mod renderer
// - inherits, exactly as it does on the OpenGL body.
//
// Viewports: each of these targets is sized to the viewport its LoadFrameBuffer case sets (the
// FindBright and Luma cases set none and inherit the full render resolution, which is their own
// size), so every pass here draws into its whole target and the arithmetic cannot drift.
public partial class VulkanClientPlatform
{
    // ------------------------------------------------------------------ pass 6: bloom

    private readonly NativeFullscreenPass nativeFindBright =
        new("findbright", new[] { "ambientBloomLevel", "extraBloom" }, new[] { "colorTex", "glowTex" });

    // One instance per blur target: the program is the same, but a pipeline is per target
    // formats, and each instance keeps its own resolved placements.
    private readonly NativeFullscreenPass nativeBlurMedHorizontal =
        new("blur", new[] { "frameSize", "isVertical" }, new[] { "inputTexture" });

    private readonly NativeFullscreenPass nativeBlurMedVertical =
        new("blur", new[] { "frameSize", "isVertical" }, new[] { "inputTexture" });

    private readonly NativeFullscreenPass nativeBlurLowHorizontal =
        new("blur", new[] { "frameSize", "isVertical" }, new[] { "inputTexture" });

    private readonly NativeFullscreenPass nativeBlurLowVertical =
        new("blur", new[] { "frameSize", "isVertical" }, new[] { "inputTexture" });

    /// <summary>
    /// The bloom chain, drawn natively: find-bright into the full-resolution target, then the
    /// two blur ping-pongs at half and quarter resolution. The OpenGL body
    /// (ClientPlatformWindows.OptimumPostBloom) turns blending off for the whole block, sets the
    /// blur's <c>frameSize</c> exactly once - at the full resolution, before the half-resolution
    /// pair, and never again for the quarter-resolution pair - and puts the viewport and
    /// blending back at the end. All of that is reproduced here, the stale <c>frameSize</c>
    /// included: it is what the vanilla image is made of, not a bug to fix.
    /// </summary>
    private void NativeBloom(int scene, int glow)
    {
        if (!OptimumRenderBloom) return;

        List<FrameBufferRef> buffers = FrameBuffers;
        ShaderProgramFindbright findbright = ShaderPrograms.Findbright;
        ShaderProgramBlur blur = ShaderPrograms.Blur;
        FrameBufferRef? findBrightTarget = NativePostTarget(buffers, FindBrightIndex);
        FrameBufferRef? medHorizontal = NativePostTarget(buffers, BlurHorizontalMedResIndex);
        FrameBufferRef? medVertical = NativePostTarget(buffers, BlurVerticalMedResIndex);
        FrameBufferRef? lowHorizontal = NativePostTarget(buffers, BlurHorizontalLowResIndex);
        FrameBufferRef? lowVertical = NativePostTarget(buffers, BlurVerticalLowResIndex);

        if (!NativeProgramUsable(findbright) || !NativeProgramUsable(blur) ||
            findBrightTarget == null || medHorizontal == null || medVertical == null ||
            lowHorizontal == null || lowVertical == null)
        {
            LegacyBloom(scene, glow);
            return;
        }

        NativePipeline? bright = NativePipelineFor(nativeFindBright, findbright, findBrightTarget.FboId);
        NativePipeline? medH = NativePipelineFor(nativeBlurMedHorizontal, blur, medHorizontal.FboId);
        NativePipeline? medV = NativePipelineFor(nativeBlurMedVertical, blur, medVertical.FboId);
        NativePipeline? lowH = NativePipelineFor(nativeBlurLowHorizontal, blur, lowHorizontal.FboId);
        NativePipeline? lowV = NativePipelineFor(nativeBlurLowVertical, blur, lowVertical.FboId);
        if (bright == null || medH == null || medV == null || lowH == null || lowV == null)
        {
            LegacyBloom(scene, glow);
            return;
        }

        Size2i client = OptimumWindowClientSize();
        float ssaa = OptimumSsaaLevel;

        // The block's blend state, left where the OpenGL body leaves it, outside the passes.
        GlToggleBlend(on: false);

        if (BeginNativePostPass("Post/" + FindBrightIndex, findBrightTarget.FboId, new[] { scene, glow },
                transient: true))
        {
            device.WriteNative(bright, nativeFindBright.Uniforms[0], NativeAmbientBloomLevel());
            device.WriteNative(bright, nativeFindBright.Uniforms[1], ShaderUniforms.ExtraBloom);
            device.DrawNativeFullscreen(bright, new[]
            {
                new NativeTexture(nativeFindBright.Samplers[0], scene),
                new NativeTexture(nativeFindBright.Samplers[1], glow),
            });
        }
        device.EndNativePass();

        // frameSize is the full-resolution value the OpenGL body sets once here and reuses for
        // all four blur draws, including the two that write a quarter-resolution target.
        float blurWidth = client.Width * ssaa;
        float blurHeight = client.Height * ssaa;

        NativeBlurStep(medH, nativeBlurMedHorizontal, "Post/" + BlurHorizontalMedResIndex,
            medHorizontal.FboId, findBrightTarget.ColorTextureIds[0], vertical: 0, blurWidth, blurHeight);
        NativeBlurStep(medV, nativeBlurMedVertical, "Post/" + BlurVerticalMedResIndex,
            medVertical.FboId, medHorizontal.ColorTextureIds[0], vertical: 1, blurWidth, blurHeight);
        NativeBlurStep(lowH, nativeBlurLowHorizontal, "Post/" + BlurHorizontalLowResIndex,
            lowHorizontal.FboId, medVertical.ColorTextureIds[0], vertical: 0, blurWidth, blurHeight);
        NativeBlurStep(lowV, nativeBlurLowVertical, "Post/" + BlurVerticalLowResIndex,
            lowVertical.FboId, lowHorizontal.ColorTextureIds[0], vertical: 1, blurWidth, blurHeight);

        // What the rest of the frame inherits from this block on the OpenGL body.
        GlViewport(0, 0, (int)(ssaa * client.Width), (int)(ssaa * client.Height));
        GlToggleBlend(on: true);
    }

    private void NativeBlurStep(NativePipeline pipeline, NativeFullscreenPass pass, string name,
        int framebufferId, int input, int vertical, float frameWidth, float frameHeight)
    {
        if (BeginNativePostPass(name, framebufferId, new[] { input }, transient: true))
        {
            device.WriteNative(pipeline, pass.Uniforms[0], frameWidth, frameHeight);
            device.WriteNative(pipeline, pass.Uniforms[1], vertical);
            device.DrawNativeFullscreen(pipeline, new[] { new NativeTexture(pass.Samplers[0], input) });
        }
        device.EndNativePass();
    }

    // --------------------------------------------------------------- pass 7: god rays

    private readonly NativeFullscreenPass nativeGodRays = new("godrays",
        new[]
        {
            "invFrameSizeIn", "maxGodRaySamples", "sunPosScreenIn", "sunPos3dIn",
            "playerViewVector", "dusk", "iGlobalTimeIn",
        },
        new[] { "inputTexture", "glowParts" });

    /// <summary>
    /// God rays, drawn natively into the half-resolution target. The OpenGL body
    /// (ClientPlatformWindows.OptimumPostGodRays) toggles no blend of its own, so the draw runs
    /// with whatever the steps before it left - blending on in the source-alpha mode, which the
    /// shader's <c>outColor.a = 1</c> makes indistinguishable from blending off. The pipeline
    /// states that mode outright rather than inheriting a tracked one, and the viewport reset the
    /// body ends with stays, outside the pass.
    ///
    /// <c>sunPos3dIn</c> comes from <c>ShaderUniforms.LightPosition3D</c> here and from
    /// <c>SunPosition3D</c> in the final composition: two different fields behind one uniform
    /// name, as on the OpenGL body.
    /// </summary>
    private void NativeGodRays(int scene, int glow)
    {
        if (!OptimumRenderGodRays) return;

        List<FrameBufferRef> buffers = FrameBuffers;
        ShaderProgramGodrays godrays = ShaderPrograms.Godrays;
        FrameBufferRef? target = NativePostTarget(buffers, GodRaysIndex);
        if (!NativeProgramUsable(godrays) || target == null)
        {
            LegacyGodRays(scene, glow);
            return;
        }

        NativePipeline? pipeline = NativePostPipeline(nativeGodRays, godrays, target.FboId, 1u,
            NativeStandardBlend(), depthTest: false, depthWrite: false, CompareOp.Less);
        if (pipeline == null)
        {
            LegacyGodRays(scene, glow);
            return;
        }

        Size2i client = OptimumWindowClientSize();
        float ssaa = OptimumSsaaLevel;

        if (BeginNativePostPass("Post/" + GodRaysIndex, target.FboId, new[] { scene, glow }, transient: false))
        {
            // The input texel size is the full-resolution one, describing the texture the pass
            // samples and not the half-resolution target it writes.
            device.WriteNative(pipeline, nativeGodRays.Uniforms[0],
                1f / (client.Width * ssaa), 1f / (client.Height * ssaa));
            device.WriteNative(pipeline, nativeGodRays.Uniforms[1], OptimumConfig.GodRaysSampleLimit);
            WriteNativeVec3(pipeline, nativeGodRays.Uniforms[2], ShaderUniforms.SunPositionScreen);
            WriteNativeVec3(pipeline, nativeGodRays.Uniforms[3], ShaderUniforms.LightPosition3D);
            WriteNativeVec3(pipeline, nativeGodRays.Uniforms[4], ShaderUniforms.PlayerViewVector);
            device.WriteNative(pipeline, nativeGodRays.Uniforms[5], ShaderUniforms.Dusk);
            device.WriteNative(pipeline, nativeGodRays.Uniforms[6], (float)EllapsedMs / 1000f);
            device.DrawNativeFullscreen(pipeline, new[]
            {
                new NativeTexture(nativeGodRays.Samplers[0], scene),
                new NativeTexture(nativeGodRays.Samplers[1], glow),
            });
        }
        device.EndNativePass();

        GlViewport(0, 0, (int)(ssaa * client.Width), (int)(ssaa * client.Height));
    }

    // -------------------------------------------------------- pass 8: FXAA luma or blit

    private readonly NativeFullscreenPass nativeLuma =
        new("luma", Array.Empty<string>(), new[] { "scene" });

    private readonly NativeFullscreenPass nativeLumaBlit =
        new("blit", Array.Empty<string>(), new[] { "scene" });

    /// <summary>
    /// The Luma step, drawn natively. The OpenGL body (ClientPlatformWindows.OptimumPostLuma)
    /// picks the branch on <c>RenderFXAA &amp;&amp; !TaaResolvedThisFrame</c>: the FXAA luma
    /// prepass over the raw jittered Primary colour - bypassing the whole resolved chain - or a
    /// pass-through blit of the chain's scene. Both go into the Luma target, and the Luma case of
    /// <c>LoadFrameBuffer</c> turns blending off without putting it back; the chain's epilogue
    /// does that.
    /// </summary>
    private void NativePostLuma(int scene)
    {
        List<FrameBufferRef> buffers = FrameBuffers;
        FrameBufferRef? target = NativePostTarget(buffers, LumaIndex);
        FrameBufferRef? primary = NativePostTarget(buffers, PrimaryIndex);
        bool fxaa = OptimumRenderFxaa && !TaaResolvedThisFrame;
        if (fxaa && (primary == null || primary.ColorTextureIds == null || primary.ColorTextureIds.Length < 1))
        {
            LegacyPostLuma(scene);
            return;
        }

        ShaderProgramBase program = fxaa ? ShaderPrograms.Luma : ShaderPrograms.Blit;
        NativeFullscreenPass pass = fxaa ? nativeLuma : nativeLumaBlit;
        if (!NativeProgramUsable(program) || target == null)
        {
            LegacyPostLuma(scene);
            return;
        }

        NativePipeline? pipeline = NativePipelineFor(pass, program, target.FboId);
        if (pipeline == null)
        {
            LegacyPostLuma(scene);
            return;
        }

        int source = fxaa ? primary!.ColorTextureIds[0] : scene;

        // The Luma case of LoadFrameBuffer turns blending off and leaves it off.
        SetBlendEnabled(false);
        if (BeginNativePostPass("Post/" + LumaIndex, target.FboId, new[] { source }, transient: false))
        {
            device.DrawNativeFullscreen(pipeline, new[] { new NativeTexture(pass.Samplers[0], source) });
        }
        device.EndNativePass();
    }

    // ------------------------------------------------------- pass 9: final composition

    private readonly NativeFullscreenPass nativeFinal = new("final",
        new[]
        {
            "ambientBloomLevel", "optimumSsaoInScene", "optimumAoDebug", "invFrameSizeIn",
            "gammaLevel", "extraGamma", "contrastLevel", "brightnessLevel", "sepiaLevel",
            "windWaveCounter", "glitchEffectStrength", "sunPosScreenIn", "sunPos3dIn",
            "playerViewVector", "damageVignetting", "damageVignettingSide", "frostVignetting",
        },
        new[] { "primaryScene", "glowParts", "bloomParts", "godrayParts", "ssaoScene" });

    /// <summary>
    /// The final composition, drawn natively. This is the attachment-subset pass of the chain: it
    /// writes Primary colour 0 while sampling Primary colour 1 as the glow on a frame the TAA
    /// resolve did not run, so the pass declares every bound colour slot except 1 and slot 1
    /// leaves the scope for the pass - one barrier out to the shader-read layout and one back
    /// when the next pass attaches it, and no feedback copy.
    ///
    /// The values are the OpenGL body's (ClientPlatformWindows.RenderFinalComposition), including
    /// the two it writes unconditionally: <c>optimumSsaoInScene</c> is written on every frame -
    /// a declared uniform left unset reads back as whatever the uniform ring last held - and the
    /// bloom and god-ray inputs are sampled whether or not their passes ran this frame.
    /// </summary>
    private void NativeFinalComposition()
    {
        if (!offscreenBufferActive) return;

        List<FrameBufferRef> buffers = FrameBuffers;
        ShaderProgramFinal final = ShaderPrograms.Final;
        FrameBufferRef? primary = NativePostTarget(buffers, PrimaryIndex);
        FrameBufferRef? luma = NativePostTarget(buffers, LumaIndex);
        FrameBufferRef? bloom = NativePostTarget(buffers, BlurVerticalLowResIndex);
        FrameBufferRef? godRays = NativePostTarget(buffers, GodRaysIndex);
        if (!NativeProgramUsable(final) || primary == null || luma == null || bloom == null || godRays == null ||
            primary.ColorTextureIds == null || primary.ColorTextureIds.Length < 2)
        {
            LegacyFinalComposition();
            return;
        }

        bool renderSsao = OptimumRenderSsao;
        bool aoDebugView = OptimumConfig.AmbientOcclusionDebugView && renderSsao;
        int aoTexture = OptimumAmbientOcclusionTexture;
        FrameBufferRef? ssaoBlur = NativePostTarget(buffers, SsaoBlurVerticalIndex);
        int ssaoScene = 0;
        if (renderSsao)
        {
            ssaoScene = aoDebugView && aoTexture != 0
                ? aoTexture
                : ssaoBlur?.ColorTextureIds != null && ssaoBlur.ColorTextureIds.Length > 0
                    ? ssaoBlur.ColorTextureIds[0]
                    : 0;
        }

        // Primary colour 1 stays out of the pass so the glow can be sampled from it.
        const uint slots = ~(1u << 1);

        // The draw-buffer selection and the blend the OpenGL body sets around the pass: outside
        // it, so everything after the composition inherits what it always did.
        BeginFinalCompositionDrawBuffers();
        GlToggleBlend(on: true);

        NativePipeline? pipeline = NativePostPipeline(nativeFinal, final, primary.FboId, slots,
            NativeFinalBlend(slots), depthTest: false, depthWrite: false, CompareOp.Less);
        if (pipeline == null)
        {
            RestoreWorldDrawBuffers(renderSsao);
            LegacyFinalComposition();
            return;
        }

        int primaryScene = luma.ColorTextureIds[0];
        int glow = OptimumPostGlowTexture();
        int bloomParts = bloom.ColorTextureIds[0];
        int godrayParts = godRays.ColorTextureIds[0];

        var reads = new List<int> { primaryScene, glow, bloomParts, godrayParts };
        if (ssaoScene > 0 && !reads.Contains(ssaoScene)) reads.Add(ssaoScene);

        Size2i client = OptimumWindowClientSize();
        float ssaa = OptimumSsaaLevel;

        SetPassContext("FinalComposition", PassFlags.None);
        if (device.BeginNativePass(new NativePassDescription
        {
            Name = "FinalComposition/0",
            FramebufferId = primary.FboId,
            ColorSlots = slots,
            Reads = reads.ToArray(),
            Flags = PassFlags.None,
        }))
        {
            NativeUniform[] u = nativeFinal.Uniforms;
            device.WriteNative(pipeline, u[0], NativeAmbientBloomLevel());
            device.WriteNative(pipeline, u[1], (OptimumSsaoInScene || !renderSsao) ? 1 : 0);
            device.WriteNative(pipeline, u[2], aoDebugView ? 1 : 0);
            device.WriteNative(pipeline, u[3], 1f / (client.Width * ssaa), 1f / (client.Height * ssaa));
            device.WriteNative(pipeline, u[4], ClientSettings.GammaLevel);
            device.WriteNative(pipeline, u[5], ClientSettings.ExtraGammaLevel);
            device.WriteNative(pipeline, u[6], ShaderUniforms.ExtraContrastLevel);
            device.WriteNative(pipeline, u[7], ClientSettings.BrightnessLevel +
                Math.Max(0f, ShaderUniforms.DropShadowIntensity * 2f - 1.66f) / 3f);
            device.WriteNative(pipeline, u[8], ShaderUniforms.SepiaLevel + ShaderUniforms.ExtraSepia);
            device.WriteNative(pipeline, u[9], ShaderUniforms.WindWaveCounter);
            device.WriteNative(pipeline, u[10], ShaderUniforms.GlitchStrength);
            if (OptimumRenderGodRays)
            {
                WriteNativeVec3(pipeline, u[11], ShaderUniforms.SunPositionScreen);
                WriteNativeVec3(pipeline, u[12], ShaderUniforms.SunPosition3D);
                WriteNativeVec3(pipeline, u[13], ShaderUniforms.PlayerViewVector);
            }
            device.WriteNative(pipeline, u[14], ShaderUniforms.DamageVignetting);
            device.WriteNative(pipeline, u[15], ShaderUniforms.DamageVignettingSide);
            device.WriteNative(pipeline, u[16], ShaderUniforms.FrostVignetting);

            device.DrawNativeFullscreen(pipeline, new[]
            {
                new NativeTexture(nativeFinal.Samplers[0], primaryScene),
                new NativeTexture(nativeFinal.Samplers[1], glow),
                new NativeTexture(nativeFinal.Samplers[2], bloomParts),
                new NativeTexture(nativeFinal.Samplers[3], godrayParts),
                new NativeTexture(nativeFinal.Samplers[4], ssaoScene),
            });
        }
        device.EndNativePass();

        RestoreWorldDrawBuffers(renderSsao);
        SetPassContext("Frame", PassFlags.AllowSplit);
    }

    // ------------------------------------------------------------------ shared plumbing

    /// <summary>
    /// The bloom level the find-bright pass and the final composition both take, bit for bit the
    /// same expression on both (ClientPlatformWindows).
    /// </summary>
    private float NativeAmbientBloomLevel() =>
        ClientSettings.AmbientBloomLevel / 100f + ShaderUniforms.AmbientBloomLevelAdd[0] +
        ShaderUniforms.AmbientBloomLevelAdd[1] + ShaderUniforms.AmbientBloomLevelAdd[2] +
        ShaderUniforms.AmbientBloomLevelAdd[3];

    /// <summary>A vec3 at its placement.</summary>
    private void WriteNativeVec3(NativePipeline pipeline, NativeUniform uniform, Vec3f value) =>
        device.WriteNative(pipeline, uniform, value.X, value.Y, value.Z);

    /// <summary>A post target of the chain, or null when the frame buffers do not hold it.</summary>
    private static FrameBufferRef? NativePostTarget(List<FrameBufferRef> buffers, int index)
    {
        if (buffers == null || index < 0 || index >= buffers.Count) return null;
        FrameBufferRef target = buffers[index];
        if (target == null || target.Disposed || target.FboId == 0) return null;
        if (target.ColorTextureIds == null || target.ColorTextureIds.Length == 0) return null;
        return target;
    }

    private static bool NativeProgramUsable(ShaderProgramBase? program) =>
        program != null && !program.LoadError && !program.Disposed && program.ProgramId > 0;

    /// <summary>
    /// One colour-0 pass of the chain's tail, drawing into the whole target - which is the
    /// viewport its LoadFrameBuffer case sets on the OpenGL body.
    /// </summary>
    private bool BeginNativePostPass(string name, int framebufferId, int[] reads, bool transient) =>
        device.BeginNativePass(new NativePassDescription
        {
            Name = name,
            FramebufferId = framebufferId,
            ColorSlots = 1u,
            Reads = reads,
            TransientSlots = transient ? 1u : 0u,
            Flags = PassFlags.None,
        });

    /// <summary>Blending on in the source-alpha mode on colour 0, as GlToggleBlend(true) sets it.</summary>
    private static AttachmentBlend[] NativeStandardBlend()
    {
        AttachmentBlend blend = AttachmentBlend.Default;
        blend.Enabled = true;
        return new[] { blend };
    }

    /// <summary>
    /// The final composition's blend: the source-alpha mode on colour 0 and no write anywhere
    /// else, so the G-buffer and motion attachments the pass keeps in its scope come out of it
    /// exactly as they went in.
    /// </summary>
    private static AttachmentBlend[] NativeFinalBlend(uint slots)
    {
        var blend = new AttachmentBlend[NativeSlotCount(slots)];
        for (int i = 0; i < blend.Length; i++)
        {
            if (i == 0)
            {
                blend[0] = AttachmentBlend.Default;
                blend[0].Enabled = true;
                continue;
            }
            blend[i].WriteMask = 0;
        }
        return blend;
    }
}
