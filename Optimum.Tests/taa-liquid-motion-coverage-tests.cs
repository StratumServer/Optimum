using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Source coverage for the TAA P4 liquid velocity pass (TAA-PLAN.md accuracy
/// rule 7): the chunkliquidmotion program, the motion-only draw-buffer window,
/// the ChunkRenderer pass that drives it, its placement in the frame, and the
/// plumbing that has to ship it (patcher entries, shader registration, the
/// compatibility scanner).
///
/// Text assertions only prove the wiring exists - the GPU test
/// (Optimum.Render.Vulkan.Tests/TaaLiquidMotionTests) proves the numbers and
/// that colour attachment 0 is untouched. What these catch is the failure this
/// project keeps hitting: a change that works in the build tree and never
/// reaches the installed runtime because a patcher entry or a registration was
/// missed.
/// </summary>
public class TaaLiquidMotionCoverageTests
{
    // ------------------------------------------------------------- the shader

    [Fact]
    public void TheLiquidWriterEmitsTheMotionContractAndNothingElse()
    {
        string vertex = Read("sources/shaders/chunkliquidmotion.vsh");
        string fragment = Read("sources/shaders/chunkliquidmotion.fsh");

        // Compiled in only while TAA is on, exactly as the P3 writers are.
        Assert.Contains("#if TAAMOTION > 0", vertex);
        Assert.Contains("#if TAAMOTION > 0", fragment);

        // Previous transforms and the camera's own movement.
        Assert.Contains("uniform mat4 prevProjectionMatrix;", vertex);
        Assert.Contains("uniform mat4 prevModelViewMatrix;", vertex);
        Assert.Contains("uniform vec3 cameraPosDelta;", vertex);
        Assert.Contains("out vec4 taaPrevClip;", vertex);

        // prevRel = truePos + cameraPosDelta, warped with the previous state,
        // through the previous unjittered projection and view (accuracy rule 4).
        Assert.Contains("WarpState taaPrev = previousWarpState();", vertex);
        Assert.Contains("vec4 taaPrevPos = vec4(truePos.xyz + cameraPosDelta, 1.0);", vertex);
        Assert.Contains("taaPrevPos = taaLiquidWorldPos(taaPrev, taaPrevPos);", vertex);
        Assert.Contains("taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos);", vertex);

        // The fragment contract taa-resolve.fsh consumes.
        Assert.Contains("layout(location = TAAMOTIONLOCATION) out vec4 outMotion;", fragment);
        Assert.Contains("uniform vec2 taaRenderSize;", fragment);
        Assert.Contains("uniform vec2 taaJitterPx;", fragment);
        Assert.Contains("vec2 prevPixel = (taaPrevClip.xy / taaPrevClip.w * 0.5 + 0.5) * taaRenderSize;", fragment);
        Assert.Contains("vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;", fragment);
        Assert.Contains(
            "outMotion = vec4(prevPixel - currentPixel, clamp(taaLiquidReactive, 0.0, 1.0), gl_FragCoord.z);",
            fragment);
        Assert.Contains("if (taaPrevClip.w <= 1e-6) {", fragment);
        // ... and the reactive value crosses that branch. taa-resolve.fsh reads
        // motion.b whether or not the writer-depth test accepted the pixel
        // (TAA-PLAN.md finding (h)), and the foam and flow-UV animation 0.3
        // stands for is happening on this fragment either way. P4 review fix -
        // the GPU proof is TaaLiquidMotionTests
        // .APreviousPositionBehindThePreviousCameraStillCarriesTheReactiveValue.
        string behindCamera = Between(fragment, "if (taaPrevClip.w <= 1e-6) {", "}", 0);
        Assert.Contains("clamp(taaLiquidReactive, 0.0, 1.0)", behindCamera);
        Assert.DoesNotContain("outMotion = vec4(0.0);", behindCamera);

        // Foam and the flow-UV scroll animate in place, so the surface is
        // reactive even where it reprojects perfectly.
        Assert.Contains("uniform float taaLiquidReactive = 0.3;", fragment);

        // And NOTHING else is written. The pass re-draws geometry that has
        // already been shaded into Primary through the OIT merge; a second
        // colour output would overwrite that image.
        foreach (string forbidden in new[] { "outColor", "outGlow", "outGNormal", "outGPosition", "OIT(" })
        {
            Assert.False(fragment.Contains(forbidden, StringComparison.Ordinal),
                "the liquid velocity pass must write only the motion attachment, found: " + forbidden);
        }
        // Exactly two output declarations: the real one and the TAA-off dummy
        // that keeps the program compilable when the writer is preprocessed out.
        Assert.Equal(2, Count(fragment, "layout(location"));
        Assert.Contains("#else", fragment);
        Assert.Contains("layout(location = 0) out vec4 outMotion;", fragment);
    }

    /// <summary>
    /// The velocity pass has to land on exactly the surface chunkliquid.vsh
    /// shaded, or it depth-tests against a fragment a fraction of a pixel away
    /// and reports another surface's motion. That means the same liquid wave
    /// warp, the same divisor from the same water flags, and the same
    /// "pretend the surface is closer" w-offset.
    /// </summary>
    [Fact]
    public void TheVertexPathIsChunkliquidsPositionPathVerbatim()
    {
        string ours = Read("sources/shaders/chunkliquidmotion.vsh");

        // The offset is applied to BOTH clip positions: it moves where the
        // fragment lands, so leaving it off the previous one reports it as motion.
        Assert.Contains("gl_Position.w += 0.0008 / max(0.1, gl_Position.z);", ours);
        Assert.Contains("taaPrevClip.w += 0.0008 / max(0.1, taaPrevClip.z);", ours);

        // Both evaluations go through one function, so the current and previous
        // positions cannot drift apart.
        Assert.Contains("vec4 taaLiquidWorldPos(WarpState st, vec4 worldPos)", ours);
        Assert.Contains("vec4 worldPos = taaLiquidWorldPos(currentWarpState(), truePos);", ours);
        Assert.Contains("#include vertexwarp.vsh", ours);

        string? vanillaPath = TryFind(".vanilla/win-x64/vintagestory/assets/game/shaders/chunkliquid.vsh");
        // The vanilla shaders are proprietary and never committed; a checkout
        // that has not bootstrapped has nothing to compare against.
        if (vanillaPath == null) return;

        string vanilla = File.ReadAllText(vanillaPath);

        // The warp branch, from vanilla's main() and from our function, with the
        // only permitted difference undone: the warp reaches the state-taking
        // overload instead of the currentWarpState() wrapper.
        string vanillaBranch = Between(vanilla, "if ((waterFlagsIn & 1) == 1) {", "vec4 cameraPos", 0);
        int ourFunction = ours.IndexOf("vec4 taaLiquidWorldPos(WarpState st, vec4 worldPos)", StringComparison.Ordinal);
        Assert.True(ourFunction > 0);
        string ourBranch = Between(ours, "if ((waterFlagsIn & 1) == 1) {", "return worldPos;", ourFunction)
            .Replace("applyLiquidWarpingState(st, ", "applyLiquidWarping(");

        Assert.Equal(Squash(vanillaBranch), Squash(ourBranch));
    }

    // ------------------------------------------------- the draw-buffer window

    /// <summary>
    /// The window this pass opens is narrower than the P3 one: the motion
    /// attachment is not ADDED to the set a shading pass writes, it REPLACES it,
    /// so a second draw of already-shaded geometry cannot touch the colour, glow
    /// or G-buffer attachments.
    /// </summary>
    [Fact]
    public void ThePlatformOpensAMotionOnlyWindowOnBothBackends()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        Assert.Contains("public override bool BeginMotionOnlyWrite()", platform);
        Assert.Contains("public override void EndMotionOnlyWrite()", platform);

        // Device path: the mask is the single motion bit, not the prefix mask.
        Assert.Contains("optimumDevice.SetDrawBuffers(frameBuffers[0].FboId, 1 << MotionAttachmentIndex);", platform);

        // GL path: GL_NONE in every slot below the motion attachment, and a
        // cached array - the pass runs once a frame, but the P3 window's rule
        // that the draw-buffer sets are built once and kept applies here too.
        Assert.Contains("private DrawBuffersEnum[] optimumMotionOnlyDrawBuffers;", platform);
        Assert.Contains("optimumMotionOnlyDrawBuffers[optimumDb] = (DrawBuffersEnum)0;", platform);
        Assert.Contains(
            "optimumMotionOnlyDrawBuffers[MotionAttachmentIndex] = (DrawBuffersEnum)(36064 + MotionAttachmentIndex);",
            platform);
        Assert.Contains("GL.DrawBuffers(optimumMotionOnlyDrawBuffers.Length, optimumMotionOnlyDrawBuffers);", platform);

        // The same guards as the P3 window, including the one that keeps the two
        // backends from disagreeing about which framebuffer the mask belongs to.
        int begin = platform.IndexOf("public override bool BeginMotionOnlyWrite()", StringComparison.Ordinal);
        int drawBuffers = platform.IndexOf(
            "optimumDevice.SetDrawBuffers(frameBuffers[0].FboId, 1 << MotionAttachmentIndex);",
            begin, StringComparison.Ordinal);
        Assert.True(drawBuffers > begin);
        string guards = platform.Substring(begin, drawBuffers - begin);
        Assert.Contains("if (OptimumMotionWriteActive) return false;", guards);
        Assert.Contains("if (MotionAttachmentIndex < 0 || !TaaTargetsReady) return false;", guards);
        Assert.Contains("if (!Vintagestory.API.Config.OptimumConfig.EffectiveTaa) return false;", guards);
        Assert.Contains("if (!ReferenceEquals(CurrentFrameBuffer, frameBuffers[0])) return false;", guards);

        // Replace blending on the motion attachment, same as the P3 window.
        int end = platform.IndexOf("public override void EndMotionOnlyWrite()", begin, StringComparison.Ordinal);
        Assert.True(end > begin);
        Assert.Contains("ApplyOptimumMotionBlendState();", platform.Substring(begin, end - begin));

        // Closing restores Primary's default set - the same restore the P3
        // window does, reached through it rather than duplicated.
        Assert.Contains("EndMotionWrite();", platform.Substring(end));
    }

    // --------------------------------------------------------------- the pass

    [Fact]
    public void ChunkRendererDrawsTheLiquidPoolsIntoTheMotionAttachmentOnly()
    {
        string chunk = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");

        string pass = MethodBodyAfter(chunk, "internal void RenderLiquidMotion(float deltaTime)");

        // Off entirely with TAA off, and never without the window.
        Assert.Contains("if (!Vintagestory.API.Config.OptimumConfig.EffectiveTaa)", pass);
        Assert.Contains("if (!optimumPlatform.BeginMotionOnlyWrite())", pass);
        Assert.Contains("optimumPlatform.EndMotionOnlyWrite();", pass);
        // Closed on every path, including a throwing draw.
        Assert.Contains("finally", pass);

        // Depth test on against Primary's depth, and the depth WRITE on: the OIT
        // liquid draw writes no depth (LoadFrameBuffer(Transparent) drops the
        // mask), so without this the resolve's writer-depth test rejects every
        // liquid pixel and the pass buys nothing.
        Assert.Contains("platform.GlEnableDepthTest();", pass);
        Assert.Contains("platform.GlDepthMask(flag: true);", pass);
        // No blending: the motion attachment is a vector, not a colour.
        Assert.Contains("platform.GlToggleBlend(on: false);", pass);
        // Culling off, as the OIT liquid draw runs.
        Assert.Contains("platform.GlDisableCullFace();", pass);
        // And the state the AfterOIT stage left is handed back, because this
        // pass runs after that stage and the post chain starts from it.
        Assert.Contains("platform.GlToggleBlend(on: true);", pass);

        // The same jittered projection and terrain view the OIT liquid draw
        // used, the previous-frame transforms from the shared contract helper,
        // and the reactive value.
        Assert.Contains("liquidMotion.UniformMatrix(\"projectionMatrix\", game.CurrentProjectionMatrix);", pass);
        Assert.Contains("liquidMotion.UniformMatrix(\"modelViewMatrix\", game.CurrentModelViewMatrix);", pass);
        Assert.Contains("game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);", pass);
        Assert.Contains("SetOptimumMotionUniforms(liquidMotion);", pass);
        Assert.Contains("liquidMotion.Uniform(\"taaLiquidReactive\", OptimumLiquidReactive);", pass);

        // Pool 4 is the liquid pool - the same one RenderOIT and the LiquidDepth
        // prepass draw - and the SSBO path is off for it there too.
        Assert.Contains("poolsByRenderPass[4][i].Render(cameraPos, \"origin\");", pass);
        Assert.Contains("game.api.renderapi.useSSBOs = false;", pass);

        // The reactive value the plan starts from.
        Assert.Contains("internal const float OptimumLiquidReactive = 0.3f;", chunk);
    }

    /// <summary>
    /// Review finding: the pass used to restore useSSBOs, pop the matrix and
    /// turn blending back on INSIDE the try, after the pool draws. A throwing
    /// draw then left the renderer with SSBOs off, an unbalanced matrix stack
    /// and blending off for the rest of the frame. Every restore now sits in
    /// the finally, and the useSSBOs snapshot is taken before the try so the
    /// finally always has something to hand back.
    /// </summary>
    [Fact]
    public void EveryLiquidPassRestoreRunsInTheFinallyBlock()
    {
        string chunk = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");

        string pass = MethodBodyAfter(chunk, "internal void RenderLiquidMotion(float deltaTime)");

        var tryMatch = System.Text.RegularExpressions.Regex.Match(pass, @"try\s*\{");
        Assert.True(tryMatch.Success, "the pass no longer has a try block");
        int tryStart = tryMatch.Index;
        int capture = pass.IndexOf("bool useSSBOs = game.api.renderapi.useSSBOs;", StringComparison.Ordinal);
        Assert.True(capture >= 0 && capture < tryStart,
            "the useSSBOs snapshot must be taken before the try, or the finally cannot restore it");

        string restores = FinallyBlock(pass);
        Assert.Contains("game.api.renderapi.useSSBOs = useSSBOs;", restores);
        Assert.Contains("game.GlPopMatrix();", restores);
        Assert.Contains("platform.GlToggleBlend(on: true);", restores);
        Assert.Contains("optimumPlatform.EndMotionOnlyWrite();", restores);

        // ...and nowhere else: a restore left in the try is a restore a throwing
        // pool draw skips.
        string guarded = pass.Substring(tryStart, pass.IndexOf(restores, StringComparison.Ordinal) - tryStart);
        Assert.DoesNotContain("game.api.renderapi.useSSBOs = useSSBOs;", guarded);
        Assert.DoesNotContain("GlPopMatrix", guarded);
        Assert.DoesNotContain("GlToggleBlend(on: true)", guarded);

        // The pop is balanced against the push that actually happened.
        Assert.Contains("pushedMatrix = true;", pass);
        Assert.Contains("if (pushedMatrix)", restores);
    }

    /// <summary>
    /// Placement is the whole reason this is a separate pass. It has to run
    /// after the OIT merge (the liquid it re-draws was shaded into the
    /// Transparent target), after every AfterOIT renderer (it writes depth for
    /// the water surface, which would otherwise occlude geometry drawn later
    /// that is legitimately visible through water), and before the resolve,
    /// which only reads the motion attachment in RenderPostprocessingEffects.
    /// </summary>
    [Fact]
    public void TheVelocityPassRunsAfterTheAfterOitStageAndInsideTheTemporalWindow()
    {
        // Read from the source of truth, not the patch: ordering is a property of
        // the whole method, and a patch only carries its hunks plus three lines
        // of context. That the method ships at all is asserted separately, by
        // the patcher-target check below.
        string main = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

        int merge = main.IndexOf("Platform.MergeTransparentRenderPass();", StringComparison.Ordinal);
        int afterOit = main.IndexOf("TriggerRenderStage(EnumRenderStage.AfterOIT, dt);", StringComparison.Ordinal);
        int liquidMotion = main.IndexOf("chunkRenderer.RenderLiquidMotion(dt);", StringComparison.Ordinal);
        int closeJitter = main.IndexOf("OptimumTemporal.Frame.JitterActive = false;", StringComparison.Ordinal);

        Assert.True(merge >= 0, "the OIT merge is not in the patched body");
        Assert.True(afterOit >= 0, "the AfterOIT stage is not in the patched body");
        Assert.True(liquidMotion >= 0, "the liquid velocity pass is never called");
        Assert.True(closeJitter >= 0, "the jitter window is never closed");

        Assert.True(merge < liquidMotion, "the velocity pass must run after the OIT merge");
        Assert.True(afterOit < liquidMotion, "the velocity pass must run after every AfterOIT renderer");
        Assert.True(liquidMotion < closeJitter, "the velocity pass must run inside the temporal window");

        // Skipped when the transparent pass that shaded the liquid did not run.
        Assert.Contains("if (doTransparentRenderPass && chunkRenderer != null)", main);
    }

    // -------------------------------------------------------- the ship

    [Fact]
    public void TheProgramIsRegisteredAndOptional()
    {
        string registry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");

        Assert.Contains(
            "RegisterOptimumShaderProgram(\"chunkliquidmotion\", ShaderPrograms.ChunkLiquidMotion = new ShaderProgram());",
            registry);

        // Optimum-only programs mark LoadError on a failed compile instead of
        // failing the whole shader load, the way the FSR and TAA programs do.
        Assert.Contains("shaderProgram == ShaderPrograms.ChunkLiquidMotion", registry);
    }

    [Fact]
    public void CecilPatcherShipsEveryLiquidMotionMethodAndMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"BeginMotionOnlyWrite\"", patcher);
        Assert.Contains("\"EndMotionOnlyWrite\"", patcher);
        Assert.Contains("\"optimumMotionOnlyDrawBuffers\"", patcher);
        Assert.Contains("\"ChunkLiquidMotion\"", patcher);
        Assert.Contains("\"RenderLiquidMotion\"", patcher);
        Assert.Contains("\"OptimumLiquidReactive\"", patcher);

        // The two bodies that call into all of it.
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"MainRenderLoop\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"registerDefaultShaderProgramsPre\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"loadRegisteredShaderPrograms\", 0", patcher);
    }

    /// <summary>
    /// A mod that ships its own chunkliquid.vsh moves the water surface the
    /// velocity pass is aiming at; one that ships chunkliquidmotion replaces the
    /// writer outright. Either way the vectors stop describing the surface that
    /// was shaded, so TAA is disabled rather than fed wrong data.
    /// </summary>
    [Fact]
    public void TheCompatibilityScannerDisablesTaaForAnExternalLiquidShader()
    {
        string scanner = Read("Optimum.Launcher/ShaderCompatibilityScanner.cs");

        foreach (string shader in new[]
        {
            "chunkliquid.vsh", "chunkliquidmotion.vsh", "chunkliquidmotion.fsh",
        })
        {
            Assert.Contains("HasExternalShader(report, \"" + shader + "\")", scanner);
        }
        Assert.Contains("AddFeatureDecision(report, \"Taa\", externalMotionShader,", scanner);
    }

    // ----------------------------------------------------------------- helpers

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
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "no such method: " + signature);
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

    /// <summary>The text between two markers, searching from <paramref name="from" />.</summary>
    private static string Between(string source, string start, string end, int from)
    {
        int begin = source.IndexOf(start, from, StringComparison.Ordinal);
        Assert.True(begin >= 0, "marker not found: " + start);
        int stop = source.IndexOf(end, begin, StringComparison.Ordinal);
        Assert.True(stop > begin, "marker not found: " + end);
        return source.Substring(begin, stop - begin);
    }

    /// <summary>Every whitespace run collapsed to one space, so indentation and
    /// vanilla's trailing whitespace cannot fail the comparison.</summary>
    private static string Squash(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
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

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
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
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
