using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The post and TAA chain as a chain (docs/vulkan-native-render-systems.md, stage 1): the OpenGL
/// body is one virtual per pass, Optimum's platform owns the order and never calls base for the
/// steps the chain holds, the first two passes draw natively, and every remaining step is a
/// legacy helper that names the stage which will replace it.
/// </summary>
public class NativePostChainCoverageTests
{
    private const string ChainFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativePostChain.cs";
    private const string TailFile = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.NativePostFinal.cs";

    /// <summary>
    /// The lib body is split into one virtual per pass, and RenderPostprocessingEffects is only
    /// their order. The split is a lift, not a rewrite: each step keeps the body it had.
    /// </summary>
    [Fact]
    public void TheOpenGlPostBodyIsOneVirtualPerPass()
    {
        string platform = Platform();

        foreach (string member in new[]
                 {
                     "public virtual void OptimumPostAmbientOcclusion(float[] projectMatrix)",
                     "public virtual int OptimumPostSceneTexture()",
                     "public virtual int OptimumPostGlowTexture()",
                     "public virtual void OptimumPostBloom(int postSceneTexture, int postGlowTexture)",
                     "public virtual void OptimumPostGodRays(int postSceneTexture, int postGlowTexture)",
                     "public virtual void OptimumPostLuma(int postSceneTexture)",
                     "public virtual void OptimumPostFinish()",
                     "public virtual void OptimumBindKeepViewport(FrameBufferRef value)",
                 })
        {
            Assert.Contains(member, platform);
        }

        // The steps' bodies, still the OpenGL body's own code.
        Assert.Contains("ShaderProgramSsao ssao = ShaderPrograms.Ssao;", platform);
        Assert.Contains("ShaderProgramFindbright findbright = ShaderPrograms.Findbright;", platform);
        Assert.Contains("godrays.SunPos3dIn = ShaderUniforms.LightPosition3D;", platform);
        Assert.Contains("return TaaResolvedThisFrame ? taaResolvedColorTexture : frameBuffers[0].ColorTextureIds[0];", platform);
        Assert.Contains("return TaaResolvedThisFrame ? taaResolvedGlowTexture : frameBuffers[0].ColorTextureIds[1];", platform);

        // Every new lib member is a patcher target and an owned region.
        string patcher = Read("Optimum.Patcher/Program.cs");
        string regions = Read("Optimum.Tests/client-platform-windows-vanilla-regions-tests.cs");
        foreach (string member in new[]
                 {
                     "OptimumPostAmbientOcclusion", "OptimumPostSceneTexture", "OptimumPostGlowTexture",
                     "OptimumPostBloom", "OptimumPostGodRays", "OptimumPostLuma", "OptimumPostFinish",
                     "OptimumBindKeepViewport",
                 })
        {
            Assert.Contains("\"" + member + "\"", patcher);
            Assert.Contains("\"" + member + "\"", regions);
        }
        Assert.Contains("\"OptimumOitRevealTexture\"", patcher);
        Assert.Contains("\"OptimumOitAccumTexture\"", patcher);
    }

    /// <summary>The OpenGL body runs its steps in the order it always ran them.</summary>
    [Fact]
    public void TheOpenGlPostBodyKeepsItsOrder()
    {
        string platform = Platform();
        int start = platform.IndexOf("public override void RenderPostprocessingEffects(float[] projectMatrix)",
            StringComparison.Ordinal);
        Assert.True(start >= 0);

        int previous = start;
        foreach (string step in new[]
                 {
                     "OptimumPostAmbientOcclusion(projectMatrix);",
                     "RenderOptimumTaaResolve();",
                     "int postSceneTexture = OptimumPostSceneTexture();",
                     "postSceneTexture = RenderOptimumTaaSharpen(postSceneTexture);",
                     "OptimumPostBloom(postSceneTexture, postGlowTexture);",
                     "OptimumPostGodRays(postSceneTexture, postGlowTexture);",
                     "OptimumPostLuma(postSceneTexture);",
                     "OptimumPostFinish();",
                 })
        {
            int at = platform.IndexOf(step, previous, StringComparison.Ordinal);
            Assert.True(at > previous, step + " is missing or out of order in RenderPostprocessingEffects");
            previous = at;
        }
    }

    /// <summary>
    /// The Vulkan chain owns the order, in the doc's section 3 sequence, and its
    /// RenderPostprocessingEffects override never calls base on the native route.
    /// </summary>
    [Fact]
    public void TheVulkanChainOwnsTheOrderAndNeverCallsBase()
    {
        string chain = Read(ChainFile);

        int previous = chain.IndexOf("internal static readonly NativePostStep[] NativePostChainOrder", StringComparison.Ordinal);
        Assert.True(previous >= 0);
        foreach (string step in new[]
                 {
                     "NativePostStep.OitMerge,", "NativePostStep.SkyMotion,",
                     "NativePostStep.SsaoAndAmbientOcclusion,", "NativePostStep.TaaResolve,",
                     "NativePostStep.TaaSharpen,", "NativePostStep.Bloom,", "NativePostStep.GodRays,",
                     "NativePostStep.FxaaOrBlit,", "NativePostStep.FinalComposition,", "NativePostStep.Blit,",
                 })
        {
            int at = chain.IndexOf(step, previous, StringComparison.Ordinal);
            Assert.True(at > previous, step + " is missing or out of order in NativePostChainOrder");
            previous = at;
        }

        // The chain's own steps, in the same order, inside the override's body.
        previous = chain.IndexOf("private void RunNativePostChain(float[] projectMatrix)", StringComparison.Ordinal);
        Assert.True(previous >= 0);
        foreach (string step in new[]
                 {
                     "PostStepAmbientOcclusion(projectMatrix);", "PostStepTaaResolve();",
                     "scene = PostStepTaaSharpen(scene);", "PostStepBloom(scene, glow);",
                     "PostStepGodRays(scene, glow);", "PostStepFxaaOrBlit(scene);", "PostStepFinish();",
                 })
        {
            int at = chain.IndexOf(step, previous, StringComparison.Ordinal);
            Assert.True(at > previous, step + " is missing or out of order in the native chain");
            previous = at;
        }

        string graph = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Graph.cs");
        Assert.Contains("if (UseNativePostChain)\n        {\n            RunNativePostChain(projectMatrix);\n            return;\n        }", graph.Replace("\r\n", "\n"));
        Assert.Contains("NativeOitMerge();", graph);
        Assert.Contains("return UseNativePostChain ? NativeSkyMotion() : LegacySkyMotion();", graph);
        Assert.Contains("LegacyFinalComposition();", graph);

        // The test switch that puts the whole chain back on the OpenGL body.
        Assert.Contains("internal bool NativePostChainEnabled { get; set; } = true;", chain);
        Assert.Contains("private bool UseNativePostChain => NativePostChainEnabled && device != null;", chain);
        Assert.Contains("if (NativeBlitEnabled && UseNativePostChain)", graph);
    }

    /// <summary>
    /// The two native passes draw through the device API - a pipeline with fixed state, a
    /// declared pass with explicit reads and colour slots, and a native fullscreen draw - and
    /// keep the OpenGL body's conditions, blend and uniform values.
    /// </summary>
    [Fact]
    public void TheFirstTwoPassesDrawNatively()
    {
        string chain = Read(ChainFile);

        Assert.Contains("device.RequestNativePipeline(new NativePipelineDescription", chain);
        Assert.Contains("device.BeginNativePass(new NativePassDescription", chain);
        Assert.Contains("device.DrawNativeFullscreen(pipeline, new[]", chain);
        Assert.Contains("device.EndNativePass();", chain);

        // The merge: the Transparent target's three attachments plus the OIT pair, the world
        // colour set with the motion attachment added while the window is open, and the
        // additive (ONE, ONE) blend on that attachment alone.
        Assert.Contains("nativeOitMerge = new(\"transparentcompose\"", chain);
        Assert.Contains("\"accumulation\", \"revealage\", \"inGlow\", \"OITreveal\", \"OITaccumulation\"", chain);
        Assert.Contains("SystemRenderOITLayers.OptimumOitRevealTexture", chain);
        Assert.Contains("SystemRenderOITLayers.OptimumOitAccumTexture", chain);
        Assert.Contains("if (motion) slots |= 1u << MotionAttachmentIndex;", chain);
        Assert.Contains("attachment.SrcColor = BlendFactor.One;", chain);
        Assert.Contains("private uint NativeWorldColorSlots() => OptimumRenderSsao ? 0b1111u : 0b11u;", chain);
        Assert.Contains("OptimumBindKeepViewport(primary);", chain);
        Assert.Contains("ApplyTransparentMergeBlendState();", chain);

        // Sky motion: the same guards, the same two matrices, LEQUAL with depth writes off, and
        // the motion attachment as the pass's only colour slot.
        Assert.Contains("nativeSkyMotion = new(\"taa-skymotion\"", chain);
        Assert.Contains("if (!frame.WasViewCaptured(EnumTemporalView.World)) return false;", chain);
        Assert.Contains("OptimumTemporalMath.ApplyProjectionJitter(jittered, frame.JitterPx.X, frame.JitterPx.Y,", chain);
        Assert.Contains("float[] invViewProj = Mat4f.Invert(new float[16], viewProj);", chain);
        Assert.Contains("if (invViewProj == null) return false;", chain);
        Assert.Contains("uint slots = 1u << MotionAttachmentIndex;", chain);
        Assert.Contains("depthTest: true, depthWrite: false, CompareOp.LessOrEqual", chain);
        Assert.Contains("device.WriteNative(pipeline, nativeSkyMotion.Uniforms[4], OptimumCloudReactive);", chain);
        Assert.Contains("GlDepthFunc(EnumDepthFunction.Less);", chain);
        Assert.Contains("GlEnableCullFace();", chain);

        // The motion window's guards, minus the draw-buffer mask a native pass does not use.
        Assert.Contains("private bool NativeMotionAttachmentWritable(FrameBufferRef primary)", chain);
        Assert.Contains("if (!OptimumTemporal.Frame.JitterActive) return false;", chain);
        Assert.Contains("return ReferenceEquals(CurrentFrameBuffer, primary);", chain);
    }

    /// <summary>
    /// Every pass of the chain draws natively, and each pass whose OpenGL body is a legacy helper
    /// keeps that helper as the old route the differential tests compare against.
    /// </summary>
    [Fact]
    public void EveryChainStepIsNativeAndKeepsItsOldRouteReachable()
    {
        string chain = Read(ChainFile);

        foreach (string helper in new[]
                 {
                     "private bool PostStepTaaResolve() => RenderOptimumTaaResolve();",
                     "private int PostStepTaaSharpen(int resolvedScene) => RenderOptimumTaaSharpen(resolvedScene);",
                     "private void PostStepBloom(int scene, int glow) => NativeBloom(scene, glow);",
                     "private void PostStepGodRays(int scene, int glow) => NativeGodRays(scene, glow);",
                     "private void PostStepFxaaOrBlit(int scene) => NativePostLuma(scene);",
                     "private void PostStepFinish() => OptimumPostFinish();",
                     "private void LegacyBloom(int scene, int glow) => OptimumPostBloom(scene, glow);",
                     "private void LegacyGodRays(int scene, int glow) => OptimumPostGodRays(scene, glow);",
                     "private void LegacyPostLuma(int scene) => OptimumPostLuma(scene);",
                     "private void LegacyFinalComposition()",
                     "private void LegacyOitMerge()",
                     "private bool LegacySkyMotion()",
                 })
        {
            Assert.Contains(helper, chain);
        }

        // Every pass of the chain is native now: no step carries a "Stage 1x makes it native"
        // marker any more.
        Assert.DoesNotContain("makes it native", chain);
        // One LEGACY helper per native pass whose OpenGL body stays reachable for the
        // differential tests: the merge, sky motion, bloom, god rays, the Luma step and the
        // final composition. The AO step and the two TAA passes keep their old route in the lib
        // virtual itself, not in a legacy helper.
        Assert.Equal(6, Count(chain, "LEGACY -"));
    }

    /// <summary>
    /// Stage 1d: the TAA resolve and the TAA sharpen draw natively, through a draw seam that
    /// leaves every temporal decision in the lib body. The contract is what this pins - the
    /// reset test, the resolved textures, the history validity and the parity flip have to stay
    /// where both backends run the same code, or the two routes can drift a frame apart.
    /// </summary>
    [Fact]
    public void TheTwoTaaPassesDrawNativelyAndKeepTheirContractInTheLibBody()
    {
        string platform = Platform();
        string abstractPlatform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        // The seams exist on the abstract platform, so a platform can override them, and the
        // Windows body is the OpenGL draw.
        Assert.Contains("public virtual void OptimumTaaResolveDraw(FrameBufferRef write, FrameBufferRef read, float[] invViewProjJittered, float[] prevViewProj, bool reset)", abstractPlatform);
        Assert.Contains("public virtual void OptimumTaaSharpenDraw(FrameBufferRef target, int resolvedScene)", abstractPlatform);
        Assert.Contains("public override void OptimumTaaResolveDraw(FrameBufferRef write, FrameBufferRef read, float[] invViewProjJittered, float[] prevViewProj, bool reset)", platform);
        Assert.Contains("public override void OptimumTaaSharpenDraw(FrameBufferRef target, int resolvedScene)", platform);

        // The temporal contract stays in the pass, on the far side of the seam.
        string resolve = MethodBody(platform, "public override bool RenderOptimumTaaResolve()");
        Assert.Contains("bool reset = frame.Reset || !_taaHistoryValid || !frame.WasViewCaptured(EnumTemporalView.World) || invViewProj == null;", resolve);
        Assert.Contains("OptimumTaaResolveDraw(write, read, invViewProj, prevViewProj, reset);", resolve);
        foreach (string state in new[]
                 {
                     "taaResolvedColorTexture = write.ColorTextureIds[0];",
                     "taaResolvedGlowTexture = write.ColorTextureIds[1];",
                     "_taaHistoryValid = true;",
                     "_taaFrameParity ^= 1;",
                     "optimumTaaResolvedThisFrame = true;",
                 })
        {
            Assert.Contains(state, resolve);
        }
        // And nothing of it leaked into the draw.
        string draw = MethodBody(platform,
            "public override void OptimumTaaResolveDraw(FrameBufferRef write, FrameBufferRef read, float[] invViewProjJittered, float[] prevViewProj, bool reset)");
        Assert.DoesNotContain("_taaFrameParity", draw);
        Assert.DoesNotContain("_taaHistoryValid", draw);
        Assert.DoesNotContain("optimumTaaResolvedThisFrame", draw);

        // The native route: a pipeline with stated fixed state, a pass with stated reads and
        // colour slots, uniforms by placement, textures straight to bindless slots.
        string chain = Read(ChainFile);
        Assert.Contains("private void NativeTaaResolve(FrameBufferRef write, FrameBufferRef read,", chain);
        Assert.Contains("private void NativeTaaSharpen(FrameBufferRef target, int resolvedScene)", chain);
        Assert.Contains("nativeTaaResolve = new(\"taa-resolve\"", chain);
        Assert.Contains("nativeTaaSharpen = new(\"taa-sharpen\"", chain);
        // All three history attachments are the pass's colour slots.
        Assert.Contains("const uint slots = 0b111u;", chain);
        // The nine resolve uniforms and the seven textures the OpenGL body writes and binds.
        foreach (string uniform in new[]
                 {
                     "renderSize", "jitterPx", "invViewProjJittered", "prevViewProj", "viewMatrix",
                     "cameraDelta", "resetHistory", "blendAlpha", "varianceGamma",
                 })
        {
            Assert.Contains("\"" + uniform + "\"", chain);
        }
        foreach (string sampler in new[]
                 {
                     "sceneTex", "glowTex", "motionTex", "depthTex",
                     "historyColor", "historyGlow", "historyDepth", "inputScene",
                 })
        {
            Assert.Contains("\"" + sampler + "\"", chain);
        }
        // The two literals the OpenGL body passes, unchanged.
        Assert.Contains("nativeTaaResolve.Uniforms[7], 0.1f", chain);
        Assert.Contains("nativeTaaResolve.Uniforms[8], 1.25f", chain);
        // And the strength, clamped exactly as the OpenGL body clamps it.
        Assert.Contains("GameMath.Clamp(OptimumConfig.TaaSharpness, 0f, 1f)", chain);

        // Registered everywhere a new lib member has to be.
        string patcher = Read("Optimum.Patcher/Program.cs");
        string regions = Read("Optimum.Tests/client-platform-windows-vanilla-regions-tests.cs");
        string selfCheck = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs");
        foreach (string member in new[] { "OptimumTaaResolveDraw", "OptimumTaaSharpenDraw" })
        {
            Assert.Contains("\"" + member + "\"", patcher);
            Assert.Contains("\"" + member + "\"", regions);
            Assert.Contains("new(true, \"" + member + "\"", selfCheck);
        }
    }

    /// <summary>
    /// The chain's tail - bloom, god rays, the Luma step and the final composition - draws through
    /// the device API, with the OpenGL body's conditions, inputs and uniform values, and the final
    /// composition declares the attachment subset that lets it sample Primary colour 1 while it
    /// writes Primary colour 0.
    /// </summary>
    [Fact]
    public void TheChainTailDrawsNatively()
    {
        string tail = Read(TailFile);

        Assert.Contains("device.BeginNativePass(new NativePassDescription", tail);
        Assert.Contains("device.DrawNativeFullscreen(pipeline, new[]", tail);
        Assert.Contains("device.EndNativePass();", tail);

        // Bloom: find-bright then the two blur ping-pongs, the stale full-resolution frameSize
        // the OpenGL body reuses for all four blur draws, and the block's blend and viewport
        // handoff outside the passes.
        Assert.Contains("private void NativeBloom(int scene, int glow)", tail);
        Assert.Contains("if (!OptimumRenderBloom) return;", tail);
        Assert.Contains("float blurWidth = client.Width * ssaa;", tail);
        Assert.Contains("NativeBlurStep(lowV, nativeBlurLowVertical", tail);
        Assert.Contains("GlToggleBlend(on: false);", tail);
        Assert.Contains("GlToggleBlend(on: true);", tail);

        // God rays: the half-resolution target, the full-resolution input texel size, and
        // LightPosition3D behind sunPos3dIn.
        Assert.Contains("private void NativeGodRays(int scene, int glow)", tail);
        Assert.Contains("if (!OptimumRenderGodRays) return;", tail);
        Assert.Contains("ShaderUniforms.LightPosition3D", tail);
        Assert.Contains("device.WriteNative(pipeline, nativeGodRays.Uniforms[1], OptimumConfig.GodRaysSampleLimit);", tail);

        // The Luma branch: the raw jittered Primary colour for the FXAA prepass, the chain's
        // scene for the blit, and blending left off.
        Assert.Contains("bool fxaa = OptimumRenderFxaa && !TaaResolvedThisFrame;", tail);
        Assert.Contains("int source = fxaa ? primary!.ColorTextureIds[0] : scene;", tail);
        Assert.Contains("SetBlendEnabled(false);", tail);

        // The final composition: the attachment subset, the unconditional uniform writes and the
        // draw-buffer handoff around the pass.
        Assert.Contains("private void NativeFinalComposition()", tail);
        Assert.Contains("const uint slots = ~(1u << 1);", tail);
        Assert.Contains("BeginFinalCompositionDrawBuffers();", tail);
        Assert.Contains("RestoreWorldDrawBuffers(renderSsao);", tail);
        Assert.Contains("device.WriteNative(pipeline, u[1], (OptimumSsaoInScene || !renderSsao) ? 1 : 0);", tail);
        Assert.Contains("device.WriteNative(pipeline, u[2], aoDebugView ? 1 : 0);", tail);
        Assert.Contains("ShaderUniforms.SunPosition3D", tail);
        Assert.Contains("aoDebugView && aoTexture != 0", tail);
        Assert.Contains("int glow = OptimumPostGlowTexture();", tail);

        string graph = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Graph.cs");
        Assert.Contains("NativeFinalComposition();", graph);
    }

    /// <summary>
    /// The client state the native tail reads instead of GL state (decision 3) is a lib accessor,
    /// a patcher target and an owned region, and the two post steps that sized themselves from
    /// NativeWindow.ClientSize now read the same window-size seam the blit does.
    /// </summary>
    [Fact]
    public void TheNativeTailReadsClientStateThroughListedLibSeams()
    {
        string platform = Platform().Replace("\r\n", "\n");
        string patcher = Read("Optimum.Patcher/Program.cs");
        string regions = Read("Optimum.Tests/client-platform-windows-vanilla-regions-tests.cs");

        foreach (string member in new[]
                 {
                     "public bool OptimumRenderBloom => RenderBloom;",
                     "public bool OptimumRenderGodRays => RenderGodRays;",
                     "public bool OptimumRenderFxaa => RenderFXAA;",
                     "public float OptimumSsaaLevel => ssaaLevel;",
                     "public int OptimumAmbientOcclusionTexture => optimumAmbientOcclusionTexture;",
                     "public bool OptimumSsaoInScene => optimumSsaoInScene;",
                 })
        {
            Assert.Contains(member, platform);
        }
        foreach (string member in new[]
                 {
                     "OptimumRenderBloom", "OptimumRenderGodRays", "OptimumRenderFxaa", "OptimumSsaaLevel",
                     "OptimumAmbientOcclusionTexture", "OptimumSsaoInScene",
                 })
        {
            Assert.Contains("\"" + member + "\"", patcher);
            Assert.Contains("\"" + member + "\"", regions);
        }

        // The bloom, god-rays and final-composition bodies take the window size from the seam, so
        // both routes compute the same value; nothing in them reads NativeWindow.ClientSize.
        foreach (string body in new[]
                 {
                     "public virtual void OptimumPostBloom(int postSceneTexture, int postGlowTexture)",
                     "public virtual void OptimumPostGodRays(int postSceneTexture, int postGlowTexture)",
                     "public override void RenderFinalComposition()",
                 })
        {
            int start = platform.IndexOf(body, StringComparison.Ordinal);
            Assert.True(start >= 0, body + " is missing");
            int end = platform.IndexOf("\n\t}\n", start, StringComparison.Ordinal);
            Assert.True(end > start);
            string source = platform.Substring(start, end - start);
            Assert.Contains("Size2i optimumClientSize = OptimumWindowClientSize();", source);
            Assert.DoesNotContain("((NativeWindow)window).ClientSize", source);
        }
    }

    private static string Platform() => ReadPatchedOrSource(
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
        "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

    private static int Count(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    /// <summary>
    /// The text from a method's signature to the start of the next member declaration at the
    /// same indentation ("\n\t}" followed by a newline).
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "method not found: " + signature);
        int end = source.IndexOf("\n\t}\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "method end not found: " + signature);
        return source.Substring(start, end - start);
    }

    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string? resolvedPatch = TryFind(patchPath);
        return resolvedPatch != null ? PatchReader.ReadPatchedContent(resolvedPatch) : Read(sourcePath);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }

    private static string? TryFind(string relativePath)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relativePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
