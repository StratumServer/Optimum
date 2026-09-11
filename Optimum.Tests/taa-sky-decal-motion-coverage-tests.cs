using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Source coverage for the TAA P4 sky / volumetrics / decal / late-overlay
/// policies:
///
///  (a) the sky and cloud policy - which of the celestial layers need a writer
///      at all, and the one pass that was added for the two that do;
///  (b) decals, which write the surface's motion themselves because they move
///      the depth buffer out from under the block's writer depth;
///  (c) AfterFinalComposition and AfterBlit, which run after the resolve, draw
///      with the unjittered projection, and are refused the motion window.
///
/// Text assertions only prove the wiring exists - the GPU test
/// (Optimum.Render.Vulkan.Tests/TaaSkyMotionTests) proves the numbers.
/// </summary>
public class TaaSkyDecalMotionCoverageTests
{
    // ------------------------------------------------- (a) sky and clouds

    /// <summary>
    /// The sky direction is far point MINUS near point. CameraMatrixOrigin is a
    /// look-at with the eye at LocalEyePos, ~1.7 blocks above the origin the
    /// terrain is drawn relative to, so a reconstructed far point's position
    /// vector is not the view direction: it carries the eye offset, which
    /// projected into a fixed ~0.6 px vertical error on every sky vector on
    /// both backends (measured 2026-09-11, eye / far * rows / 2 / tan(fov / 2)).
    /// Both consumers - the sky pass and the resolve's own sky branch - take the
    /// homogeneous difference of the two reconstructed points.
    /// </summary>
    [Fact]
    public void TheSkyDirectionIsFarMinusNearInBothConsumers()
    {
        string sky = Read("sources/shaders/taa-skymotion.fsh");
        string resolve = Read("sources/shaders/taa-resolve.fsh");

        Assert.Contains("vec4 nearH = taaInvViewProjJittered * vec4(ndc, -1.0, 1.0);", sky);
        Assert.Contains("vec3 direction = farH.xyz * nearH.w - nearH.xyz * farH.w;", sky);
        Assert.Contains("if ((farH.w < 0.0) != (nearH.w < 0.0)) direction = -direction;", sky);
        Assert.DoesNotContain("farH.w < 0.0 ? -farH.xyz : farH.xyz", sky);

        // The resolve reprojects the nearest-depth tap of its 3x3 (2026-09-11), so
        // its far and near points are that tap's; the direction is still far minus
        // near with the same sign rule.
        Assert.Contains("vec4 nearH = invViewProjJittered * vec4(closestNdc, -1.0, 1.0);", resolve);
        Assert.Contains("vec3 skyDirection = closestH.xyz * nearH.w - nearH.xyz * closestH.w;", resolve);
        Assert.Contains("if ((closestH.w < 0.0) != (nearH.w < 0.0)) skyDirection = -skyDirection;", resolve);
        Assert.Contains("prevViewProj * vec4(skyDirection, 0.0)", resolve);
        Assert.DoesNotContain("prevViewProj * vec4(world, 0.0)", resolve);
        Assert.DoesNotContain("prevViewProj * vec4(closestWorld, 0.0)", resolve);
    }

    /// <summary>
    /// Sky colour, the night sky, the sun and the moon draw on Primary with the
    /// depth test disabled or the depth mask off, so they leave Primary's depth
    /// at the far plane and never claim a motion pixel. taa-resolve.fsh's camera
    /// fallback then unprojects a depth of 1 - a point at infinity - and
    /// reprojects it, which IS the infinite-direction reprojection those layers
    /// need. None of them is given a writer, and this pins down the depth state
    /// that makes that correct.
    /// </summary>
    [Fact]
    public void TheCelestialLayersWriteNoDepthAndThereforeNeedNoWriter()
    {
        string nightSky = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderNightSky.cs");
        string skyColor = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSkyColor.cs");
        string sunMoon = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSunMoon.cs");

        // Depth test off for the whole pass: nothing is written to depth at all.
        Assert.Contains("GlDisableDepthTest();", nightSky);
        Assert.Contains("GlDisableDepthTest();", skyColor);
        // The sun and moon DO test depth (terrain occludes them) but never write
        // it, so the sky depth of 1 survives underneath them.
        Assert.Contains("GlDepthMask(flag: false);", sunMoon);

        // And none of the three is instrumented - the whole point of the row.
        foreach (string source in new[] { nightSky, skyColor, sunMoon })
        {
            Assert.DoesNotContain("MotionWrite", source);
            Assert.DoesNotContain("SetOptimumMotionUniforms", source);
        }
    }

    /// <summary>
    /// The volumetric clouds and the aurora are the two celestial layers that
    /// move independently of the camera, and both are drawn into the Transparent
    /// target, where Primary's motion attachment does not exist. They get a
    /// reactive value from the sky pass instead, gated on the coverage the
    /// Transparent target's revealage attachment records.
    /// </summary>
    [Fact]
    public void TheSkyPassWritesTheRotationVectorAndACoverageGatedReactiveValue()
    {
        string vertex = Read("sources/shaders/taa-skymotion.vsh");
        string fragment = Read("sources/shaders/taa-skymotion.fsh");

        // Depth 1: the triangle survives only where nothing wrote depth.
        Assert.Contains("gl_Position = vec4(x, y, 1.0, 1.0);", vertex);

        Assert.Contains("#if TAAMOTION > 0", fragment);
        Assert.Contains("layout(location = TAAMOTIONLOCATION) out vec4 outMotion;", fragment);
        Assert.Contains("uniform sampler2D transparentRevealTex;", fragment);
        Assert.Contains("uniform float taaCloudReactive", fragment);

        // Coverage = 1 - revealage, the same term transparentcompose.fsh
        // composites with.
        Assert.Contains(
            "float coverage = clamp(1.0 - texelFetch(transparentRevealTex, ivec2(gl_FragCoord.xy), 0).r, 0.0, 1.0);",
            fragment);
        // Interpolated, not stepped: a clear pixel keeps reactive 0 and its full
        // history weight, a fully covered one gets taaCloudReactive.
        Assert.Contains(
            "float reactive = mix(coverage, clamp(taaCloudReactive, 0.0, 1.0), coverage);",
            fragment);

        // The rotation-only reprojection: a direction (w = 0) through the
        // previous view-projection drops its translation column, which is what
        // "a point on the celestial sphere does not parallax" means in maths.
        Assert.Contains("vec4 prevClip = taaPrevViewProj * vec4(direction, 0.0);", fragment);
        Assert.Contains("vec4 farH = taaInvViewProjJittered * vec4(ndc, 1.0, 1.0);", fragment);

        // The motion contract taa-resolve.fsh consumes.
        Assert.Contains("vec2 prevPixel = (prevClip.xy / prevClip.w * 0.5 + 0.5) * taaRenderSize;", fragment);
        Assert.Contains("vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;", fragment);
        Assert.Contains("outMotion = vec4(prevPixel - currentPixel, reactive, gl_FragCoord.z);", fragment);

        // A fragment stage with no output at all is not worth handing to two
        // translators; the TAA-off build keeps one dummy attachment.
        Assert.Contains("#else", fragment);
        Assert.Contains("layout(location = 0) out vec4 outMotion;", fragment);
    }

    [Fact]
    public void TheSkyPassRunsLastInTheSceneStillInsideTheTemporalWindow()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        string clientMain = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

        string pass = MethodBodyAfter(platform, "public override bool RenderOptimumSkyMotion()");

        // The motion-only window, like the liquid velocity pass: this one
        // re-records motion for pixels the frame has already shaded.
        Assert.Contains("BeginMotionOnlyWrite()", pass);
        Assert.Contains("EndMotionOnlyWrite();", pass);
        Assert.Contains("finally", pass);

        // Depth test on, depth writes OFF, and GL_LEQUAL - without the depth
        // func change a triangle at the far plane draws nothing at all under the
        // default GL_LESS.
        Assert.Contains("GlEnableDepthTest();", pass);
        Assert.Contains("GlDepthMask(flag: false);", pass);
        Assert.Contains("GlDepthFunc(EnumDepthFunction.Lequal);", pass);
        // ...and handed back afterwards.
        Assert.Contains("GlDepthFunc(EnumDepthFunction.Less);", pass);
        Assert.Contains("GlDepthMask(flag: true);", pass);

        // The revealage it reads is the very texture the merge composited with.
        Assert.Contains("transparent.ColorTextureIds[1]", pass);
        string merge = MethodBodyAfter(platform, "public override void MergeTransparentRenderPass()");
        Assert.Contains("transparentcompose.Revealage2D = frameBuffers[1].ColorTextureIds[1];", merge);

        // Ordering inside the scene phase: after the liquid velocity pass, which
        // is after the OIT merge and after every AfterOIT renderer.
        string loop = MethodBodyAfter(clientMain, "public void MainRenderLoop(float dt)");
        int afterOit = loop.IndexOf("TriggerRenderStage(EnumRenderStage.AfterOIT, dt);", StringComparison.Ordinal);
        int liquid = loop.IndexOf("chunkRenderer.RenderLiquidMotion(dt);", StringComparison.Ordinal);
        int sky = loop.IndexOf("RenderOptimumSkyMotion();", StringComparison.Ordinal);
        Assert.True(afterOit >= 0 && liquid > afterOit, "the liquid velocity pass must follow the AfterOIT stage");
        Assert.True(sky > liquid, "the sky motion pass must be the last writer of the scene phase");

        // And it is still inside the temporal window: the jitter is not closed
        // until RenderAfterPostProcessing, which is a different method.
        Assert.DoesNotContain("JitterActive = false", loop);

        // Registered as an Optimum-only program, so a failed compile marks
        // LoadError instead of failing the whole shader load.
        string registry = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        Assert.Contains(
            "RegisterOptimumShaderProgram(\"taa-skymotion\", ShaderPrograms.TaaSkyMotion = new ShaderProgram());",
            registry);
        Assert.Contains("shaderProgram == ShaderPrograms.TaaSkyMotion", registry);
    }

    /// <summary>
    /// Review finding: GlDisableCullFace() runs BEFORE BeginMotionOnlyWrite(),
    /// so it is not covered by the try at all. Both the early return (the window
    /// refused to open) and the finally restored depth and blend but left
    /// culling disabled for everything that followed in the frame. Both paths
    /// now re-enable it.
    /// </summary>
    [Fact]
    public void TheSkyPassRestoresCullingOnBothPaths()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        string pass = MethodBodyAfter(platform, "public override bool RenderOptimumSkyMotion()");

        // The disable that has to be undone, and it is outside the try.
        int disable = pass.IndexOf("GlDisableCullFace();", StringComparison.Ordinal);
        int begin = pass.IndexOf("if (!BeginMotionOnlyWrite())", StringComparison.Ordinal);
        Assert.True(disable >= 0 && begin > disable,
            "culling is no longer disabled before the window opens; revisit the restores");

        // Path 1: the window refused to open.
        string earlyReturn = pass.Substring(begin, pass.IndexOf("return false;", begin, StringComparison.Ordinal) - begin);
        Assert.Contains("GlDepthFunc(EnumDepthFunction.Less);", earlyReturn);
        Assert.Contains("GlDepthMask(flag: true);", earlyReturn);
        Assert.Contains("GlToggleBlend(on: true);", earlyReturn);
        Assert.Contains("GlEnableCullFace();", earlyReturn);

        // Path 2: the pass ran, threw or not.
        string restores = FinallyBlock(pass);
        Assert.Contains("EndMotionOnlyWrite();", restores);
        Assert.Contains("GlDepthFunc(EnumDepthFunction.Less);", restores);
        Assert.Contains("GlDepthMask(flag: true);", restores);
        Assert.Contains("GlToggleBlend(on: true);", restores);
        Assert.Contains("GlEnableCullFace();", restores);
    }

    // ---------------------------------------------------------- (b) decals

    /// <summary>
    /// The justification the plan asks for, pinned to the code: the decal pass
    /// runs with the depth mask on (inherited from the AfterOIT setup in
    /// ClientMain) and decals.vsh pushes the fragment nearer than the block. So
    /// the depth buffer at a decal pixel is NOT the depth the terrain writer
    /// recorded, and leaving the attachment untouched would demote the pixel to
    /// the camera fallback wherever that offset exceeds the resolve's tolerance.
    /// The decal writes the surface's motion with its own depth instead.
    /// </summary>
    [Fact]
    public void DecalsMoveTheDepthBufferAndThereforeWriteTheMotionThemselves()
    {
        string clientMain = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");
        string vertex = Read("sources/shaders/decals.vsh");
        string fragment = Read("sources/shaders/decals.fsh");

        // The depth state the AfterOIT stage runs under: mask on, test on.
        string loop = MethodBodyAfter(clientMain, "public void MainRenderLoop(float dt)");
        int mask = loop.IndexOf("Platform.GlDepthMask(flag: true);", StringComparison.Ordinal);
        int afterOit = loop.IndexOf("TriggerRenderStage(EnumRenderStage.AfterOIT, dt);", StringComparison.Ordinal);
        Assert.True(mask >= 0 && afterOit > mask,
            "the AfterOIT stage no longer runs with depth writes on; the decal policy has to be revisited");

        // Vanilla's z-offset, which is what moves the depth buffer.
        Assert.Contains("gl_Position.w += zOffset * 0.00025 / max(0.1, gl_Position.z * 0.05);", vertex);
        // ...applied to the previous clip position too, or the shift itself
        // would be reported as motion.
        Assert.Contains(
            "taaPrevClip.w += taaPrevZOffset * 0.00025 / max(0.1, taaPrevClip.z * 0.05);",
            vertex);

        // The terrain previous path of accuracy rule 4, through the same warp
        // functions chunkopaque.vsh uses.
        Assert.Contains("#if TAAMOTION > 0", vertex);
        Assert.Contains("uniform mat4 prevProjectionMatrix;", vertex);
        Assert.Contains("uniform mat4 prevModelViewMatrix;", vertex);
        Assert.Contains("uniform vec3 cameraPosDelta;", vertex);
        Assert.Contains("WarpState taaPrev = previousWarpState();", vertex);
        Assert.Contains("vec4 taaPrevPos = vec4(vertexPos + origin + cameraPosDelta, 1.0);", vertex);
        Assert.Contains("taaPrevPos = applyVertexWarpingState(taaPrev, renderFlagsIn, taaPrevPos);", vertex);
        Assert.Contains("taaPrevPos = applyGlobalWarpingState(taaPrev, taaPrevPos);", vertex);
        Assert.Contains("taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos);", vertex);

        // reactive 0: crack progress is left to the resolve's colour clipping,
        // which is the inventory row's own wording.
        Assert.Contains("layout(location = TAAMOTIONLOCATION) out vec4 outMotion;", fragment);
        Assert.Contains("outMotion = vec4(prevPixel - currentPixel, 0.0, gl_FragCoord.z);", fragment);
        // Vanilla's own outputs still there - this is an addition to a shading
        // pass, not a replacement of it.
        Assert.Contains("layout(location = 0) out vec4 outColor;", fragment);
        Assert.Contains("layout(location = 1) out vec4 outGlow;", fragment);
    }

    [Fact]
    public void SystemRenderDecalsOpensTheMotionWindowAroundItsDraw()
    {
        string decals = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderDecals.cs");

        string pass = MethodBodyAfter(decals, "public void OnRenderFrame3D(float deltaTime)");

        // The P3 window (motion ADDED to Primary's set), not the motion-only
        // one: this pass shades and writes motion in the same draw.
        Assert.Contains("optimumPlatform.BeginMotionWrite()", pass);
        Assert.Contains("optimumPlatform.EndMotionWrite();", pass);
        Assert.Contains("finally", pass);
        Assert.Contains("SetOptimumMotionUniforms(shaderProgramDecals);", pass);

        // The window has to be open before GlToggleBlend, because that call is
        // what forces replace blending onto the motion attachment - and the
        // decal pass blends.
        int begin = pass.IndexOf("BeginMotionWrite()", StringComparison.Ordinal);
        int blend = pass.IndexOf("GlToggleBlend(on: true)", StringComparison.Ordinal);
        Assert.True(begin >= 0 && blend > begin,
            "the motion window must be opened before blending is turned on");

        string uniforms = MethodBodyAfter(decals, "private void SetOptimumMotionUniforms(IShaderProgram program)");
        Assert.Contains("frame.GetPrevProjection(EnumTemporalView.World)", uniforms);
        Assert.Contains("frame.PrevCameraMatrixOrigin", uniforms);
        Assert.Contains("frame.ApplyMotionUniforms(program);", uniforms);

        string? patch = TryFind("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderDecals.cs.patch");
        Assert.True(patch != null, "SystemRenderDecals has no patch, so the change never ships");
        Assert.Contains("BeginMotionWrite()", PatchReader.ReadPatchedContent(patch!));
    }

    /// <summary>
    /// Review finding: only decalPool.Draw sat inside the try. The GL setup, the
    /// shader activation and the uniform setup ran between BeginMotionWrite()
    /// and the try, so a throw in any of them left the motion attachment in
    /// Primary's draw-buffer mask - and replace blending on it - for the rest of
    /// the frame. Everything the open window covers is inside the try now.
    /// </summary>
    [Fact]
    public void TheDecalMotionWindowCoversEverythingItOpened()
    {
        string decals = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderDecals.cs");
        string pass = MethodBodyAfter(decals, "public void OnRenderFrame3D(float deltaTime)");

        int begin = pass.IndexOf("optimumPlatform.BeginMotionWrite();", StringComparison.Ordinal);
        Assert.True(begin >= 0);
        var tryMatch = System.Text.RegularExpressions.Regex.Match(pass.Substring(begin), @"try\s*\{");
        Assert.True(tryMatch.Success, "the window opens outside any try");
        int tryStart = begin + tryMatch.Index;

        string restores = FinallyBlock(pass);
        Assert.Contains("optimumPlatform.EndMotionWrite();", restores);

        // Nothing but the window's own bookkeeping happens between the open and
        // the try: every statement below runs guarded.
        string guarded = pass.Substring(tryStart, pass.IndexOf(restores, StringComparison.Ordinal) - tryStart);
        foreach (string statement in new[]
        {
            "game.Platform.GlToggleBlend(on: true);",
            "game.Platform.GlDisableCullFace();",
            "shaderProgramDecals.Use();",
            "shaderProgramDecals.ProjectionMatrix = game.CurrentProjectionMatrix;",
            "SetOptimumMotionUniforms(shaderProgramDecals);",
            "decalPool.Draw(game.api, game.frustumCuller, EnumFrustumCullMode.CullInstant);",
        })
        {
            Assert.Contains(statement, guarded);
        }

        string unguarded = pass.Substring(begin, tryStart - begin);
        Assert.DoesNotContain("GlToggleBlend", unguarded);
        Assert.DoesNotContain("shaderProgramDecals", unguarded);
    }

    // ------------------------------- (c) AfterFinalComposition / AfterBlit

    /// <summary>
    /// The late overlays - selection boxes, work-item guides, the knapping,
    /// clay-form and anvil surfaces, and the AfterBlit rifts - all run after the
    /// resolve has consumed the motion attachment and after the jitter window is
    /// closed, so they draw with the unjittered projection and must not be able
    /// to open a motion window.
    /// </summary>
    [Fact]
    public void TheLateStagesRunAfterTheResolveWithTheUnjitteredProjection()
    {
        string screenManager = Read("build/VintagestoryLib/Vintagestory.Client/ScreenManager.cs");
        string clientMain = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // The frame's order, from the one place that sequences it.
        string render = MethodBodyAfter(screenManager, "internal void Render(float dt)");
        int post = render.IndexOf("Platform.RenderPostprocessingEffects(projectMatrix);", StringComparison.Ordinal);
        int afterPost = render.IndexOf("CurrentScreen.RenderAfterPostProcessing(dt);", StringComparison.Ordinal);
        int afterFinal = render.IndexOf("CurrentScreen.RenderAfterFinalComposition(dt);", StringComparison.Ordinal);
        int blit = render.IndexOf("Platform.BlitPrimaryToDefault();", StringComparison.Ordinal);
        int afterBlit = render.IndexOf("CurrentScreen.RenderAfterBlit(dt);", StringComparison.Ordinal);
        Assert.True(post >= 0 && afterPost > post,
            "AfterPostProcessing must follow the post chain that contains the resolve");
        Assert.True(afterFinal > afterPost && blit > afterFinal && afterBlit > blit,
            "AfterFinalComposition and AfterBlit must follow it too");

        // The resolve is the first thing in that post chain.
        string postChain = MethodBodyAfter(platform, "public override void RenderPostprocessingEffects(float[] projectMatrix)");
        Assert.Contains("RenderOptimumTaaResolve();", postChain);

        // The jitter window closes before any of them.
        string afterPostProcessing = MethodBodyAfter(clientMain, "public void RenderAfterPostProcessing(float dt)");
        int close = afterPostProcessing.IndexOf("OptimumTemporal.Frame.JitterActive = false;", StringComparison.Ordinal);
        int trigger = afterPostProcessing.IndexOf("TriggerRenderStage(EnumRenderStage.AfterPostProcessing, dt);", StringComparison.Ordinal);
        Assert.True(close >= 0 && trigger > close,
            "the jitter window must be closed before the first late stage runs");

        // ...and with it closed, CurrentProjectionMatrix returns the unjittered
        // matrix unconditionally, whatever a late renderer asks for.
        string projection = MethodBodyAfter(clientMain, "public float[] CurrentProjectionMatrix");
        Assert.Contains("if (OptimumTemporal.Frame.JitterActive", projection);

        // The late renderers really do use that getter.
        Assert.Contains("prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;",
            Read("VSSurvivalMod/BlockEntityRenderer/KnappingRenderer.cs"));
    }

    /// <summary>
    /// And the motion window is refused there, on the same flag. Without this
    /// guard the check would rest on "Primary happens not to be bound", which is
    /// false: RenderFinalComposition leaves Primary bound, so an
    /// AfterFinalComposition renderer that called BeginMotionWrite would get it.
    /// </summary>
    [Fact]
    public void TheMotionWindowIsRefusedOnceTheTemporalWindowIsClosed()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        foreach (string signature in new[]
        {
            "public override bool BeginMotionWrite()",
            "public override bool BeginMotionOnlyWrite()",
        })
        {
            string body = MethodBodyAfter(platform, signature);
            Assert.Contains("if (!OptimumTemporal.Frame.JitterActive) return false;", body);
            // The other two guards that were already there, kept together with
            // it so a refactor cannot drop one silently.
            Assert.Contains("if (!Vintagestory.API.Config.OptimumConfig.EffectiveTaa) return false;", body);
            Assert.Contains("if (!ReferenceEquals(CurrentFrameBuffer, frameBuffers[0])) return false;", body);
        }

        // The AfterBlit content is further out still - it draws into the default
        // framebuffer - which the Primary-is-bound guard catches on its own.
        Assert.Contains("capi.Event.RegisterRenderer(this, EnumRenderStage.AfterBlit, \"riftrenderer\");",
            Read("VSSurvivalMod/Systems/Rifts/RiftRenderer.cs"));
    }

    // -------------------------------------------------------------- the ship

    [Fact]
    public void CecilPatcherShipsEverySkyAndDecalMotionMethodAndMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"Vintagestory.Client.NoObf.SystemRenderDecals\", \"OnRenderFrame3D\", 1", patcher);
        Assert.Contains("\"RenderOptimumSkyMotion\"", patcher);
        Assert.Contains("\"OptimumCloudReactive\"", patcher);
        Assert.Contains("\"TaaSkyMotion\"", patcher);

        // The patch has to be owned, or extract-patches writes a file nothing
        // ships.
        Assert.Contains(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderDecals.cs.patch",
            Read("patches/cecil-owned.list"));
    }

    [Fact]
    public void TheCompatibilityScannerDisablesTaaForAnExternalDecalOrSkyShader()
    {
        string scanner = Read("Optimum.Launcher/ShaderCompatibilityScanner.cs");

        foreach (string shader in new[]
        {
            "decals.vsh", "decals.fsh", "taa-skymotion.vsh", "taa-skymotion.fsh",
        })
        {
            Assert.Contains("HasExternalShader(report, \"" + shader + "\")", scanner);
        }
    }

    /// <summary>
    /// TAA off must be byte-identical to today's chain. Delete every
    /// <c>#if TAAMOTION &gt; 0</c> region and the comments, and what is left has
    /// to be the vanilla file - read from the release archive, never from
    /// <c>.vanilla/win-x64/...</c>, which <c>make deploy</c> overwrites with
    /// these very overrides.
    /// </summary>
    [Theory]
    [InlineData("decals.vsh")]
    [InlineData("decals.fsh")]
    public void WithTaaOffTheOverridesAreTheVanillaShaders(string shader)
    {
        string ours = Read("sources/shaders/" + shader);
        string? vanilla = VanillaShaderArchive.TryRead("shaders/" + shader);
        if (vanilla == null) return;

        Assert.Equal(Squash(StripComments(vanilla)), Squash(StripComments(StripTaaRegions(ours))));
    }

    /// <summary>
    /// The decal writer has two shapes, because decals.vsh has two: with
    /// USESSBO the vertex position and the render flags are locals unpacked from
    /// the face buffer, without it they are vertex attributes. USESSBO tracks
    /// ScreenManager.Platform.UseSSBOs and is on by default, so the SSBO shape is
    /// what the game really runs - and the ShaderCorpus rows that carry USESSBO 1
    /// all carry TAAMOTION 0, which left the combination outside the translation
    /// gate entirely. Explicit cases cover it; this test keeps them there.
    /// </summary>
    [Fact]
    public void BothDecalVertexShapesAreInTheTranslationGate()
    {
        string vertex = Read("sources/shaders/decals.vsh");
        // The TAA block reads symbols the SSBO branch declares as locals.
        Assert.Contains("#if USESSBO > 0", vertex);
        Assert.Contains("int renderFlagsIn = vdata.flags[vIndex];", vertex);
        Assert.Contains("vec4 taaPrevPos = vec4(vertexPos + origin + cameraPosDelta, 1.0);", vertex);

        string gate = Read("Optimum.Render.Vulkan.Tests/ShaderTranslationTests.cs");
        Assert.Contains("decals-ssbo-ssao", gate);
        Assert.Contains("decals-nossbo-ssao", gate);
    }

    // ---------------------------------------------------------------- helpers

    private static string StripTaaRegions(string source)
    {
        var kept = new List<string>();
        int depth = 0;
        bool inTaa = false;

        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.Trim();

            if (!inTaa && trimmed.StartsWith("#if TAAMOTION", StringComparison.Ordinal))
            {
                inTaa = true;
                depth = 1;
                continue;
            }

            if (inTaa)
            {
                if (trimmed.StartsWith("#if", StringComparison.Ordinal)) depth++;
                else if (trimmed.StartsWith("#endif", StringComparison.Ordinal))
                {
                    depth--;
                    if (depth == 0) inTaa = false;
                }
                continue;
            }

            kept.Add(line);
        }

        Assert.False(inTaa, "unterminated #if TAAMOTION region");
        return string.Join("\n", kept);
    }

    private static string StripComments(string source)
    {
        var kept = new List<string>();
        foreach (string line in source.Split('\n'))
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    /// <summary>The braced block of the method's finally clause.</summary>
    private static string FinallyBlock(string body)
    {
        var match = System.Text.RegularExpressions.Regex.Match(body, @"finally\s*\{");
        Assert.True(match.Success, "no finally block");
        int open = body.IndexOf('{', match.Index);
        int depth = 0;
        for (int i = open; i < body.Length; i++)
        {
            if (body[i] == '{') depth++;
            else if (body[i] == '}')
            {
                depth--;
                if (depth == 0) return body.Substring(open, i - open + 1);
            }
        }
        throw new InvalidOperationException("unterminated finally block");
    }

    private static string MethodBodyAfter(string source, string signature)
    {
        return signature + BodyOf(source, signature);
    }

    private static string BodyOf(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "no such function: " + signature);
        int open = source.IndexOf('{', start);
        Assert.True(open > start);

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(open, i - open + 1);
            }
        }
        throw new InvalidOperationException("unterminated function body: " + signature);
    }

    private static string Squash(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && builder.Length > 0) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
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
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
