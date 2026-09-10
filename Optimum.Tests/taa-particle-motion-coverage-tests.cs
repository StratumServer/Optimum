using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Source coverage for the TAA P4 particle policies: the cube-particle motion
/// writer (drawn on Primary, so it writes the attachment itself), the reactive
/// value the OIT merge contributes for everything drawn into the Transparent
/// target, and the plumbing that has to ship both (patcher entries, the
/// compatibility scanner).
///
/// Text assertions only prove the wiring exists - the GPU test
/// (Optimum.Render.Vulkan.Tests/TaaParticleMotionTests) proves the numbers.
/// What these catch is the failure this project keeps hitting: a change that
/// works in the build tree and never reaches the installed runtime because a
/// patcher entry or a registration was missed - and the second one, that an
/// override drifted away from the vanilla shader it is supposed to be identical
/// to with TAA off.
/// </summary>
public class TaaParticleMotionCoverageTests
{
    // ------------------------------------------------- (a) the cube writer

    [Fact]
    public void TheCubeParticleWriterEmitsTheMotionContractBesideItsVanillaOutputs()
    {
        string vertex = Read("sources/shaders/particlescube.vsh");
        string fragment = Read("sources/shaders/particlescube.fsh");

        // Compiled in only while TAA is on, exactly as the P3 writers are.
        Assert.Contains("#if TAAMOTION > 0", vertex);
        Assert.Contains("#if TAAMOTION > 0", fragment);

        // Previous transforms and the camera's own movement.
        Assert.Contains("uniform mat4 prevProjectionMatrix;", vertex);
        Assert.Contains("uniform mat4 prevModelViewMatrix;", vertex);
        Assert.Contains("uniform vec3 cameraPosDelta;", vertex);
        Assert.Contains("out vec4 taaPrevClip;", vertex);

        // The camera-only previous position: the instance positions are
        // camera-relative, so prevPos = pos + cameraPosDelta is the same rule
        // accuracy rule 4 gives a chunk vertex, and the warp is replayed with
        // the previous frame's state through the very same function.
        Assert.Contains("WarpState taaPrev = previousWarpState();", vertex);
        Assert.Contains(
            "vec4 taaPrevPos = taaParticleWorldPos(taaPrev, particlePosition + cameraPosDelta);",
            vertex);
        Assert.Contains("taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos);", vertex);

        // The fragment contract taa-resolve.fsh consumes.
        Assert.Contains("layout(location = TAAMOTIONLOCATION) out vec4 outMotion;", fragment);
        Assert.Contains("uniform vec2 taaRenderSize;", fragment);
        Assert.Contains("uniform vec2 taaJitterPx;", fragment);
        Assert.Contains("vec2 prevPixel = (taaPrevClip.xy / taaPrevClip.w * 0.5 + 0.5) * taaRenderSize;", fragment);
        Assert.Contains("vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;", fragment);
        // reactive 1: a cube particle is alpha-blended, appears and disappears
        // between frames, and has no per-particle history - the resolve must
        // take this frame's pixel.
        Assert.Contains(
            "outMotion = vec4(prevPixel - currentPixel, 1.0, gl_FragCoord.z);",
            fragment);
        Assert.Contains("if (taaPrevClip.w <= 1e-6) {", fragment);

        // Unlike the liquid velocity pass, this writer is an ADDITION to a
        // shading pass: the vanilla outputs have to still be there, or the
        // particles would stop being drawn.
        Assert.Contains("layout(location = 0) out vec4 outColor;", fragment);
        Assert.Contains("layout(location = 1) out vec4 outGlow;", fragment);
        Assert.Contains("layout(location = 2) out vec4 outGNormal;", fragment);
        Assert.Contains("layout(location = 3) out vec4 outGPosition;", fragment);
    }

    /// <summary>
    /// The previous position has to come out of the same expression the current
    /// one does, or the two disagree by whatever drifted between the copies and
    /// that difference is reported as motion. Vanilla's own lines are left
    /// untouched in the override (the additive rule), so the twin beside them is
    /// compared against them here.
    /// </summary>
    [Fact]
    public void TheCubeParticlePositionPathIsVanillasVerbatim()
    {
        string ours = Read("sources/shaders/particlescube.vsh");

        Assert.Contains("vec4 taaParticleWorldPos(WarpState st, vec3 taaParticlePosition)", ours);
        Assert.Contains("#include vertexwarp.vsh", ours);

        string? vanilla = VanillaShaderArchive.TryRead("shaders/particlescube.vsh");
        // The vanilla shaders are proprietary and never committed; a checkout
        // that has not bootstrapped has nothing to compare against.
        if (vanilla == null) return;

        // From vanilla's main(): the first "#if defined(VEC3SCALE)" in the file
        // is the attribute declaration, not the position branch.
        int vanillaMain = vanilla.IndexOf("void main()", StringComparison.Ordinal);
        Assert.True(vanillaMain > 0);
        string vanillaBranch = Between(vanilla, "#if defined(VEC3SCALE)", "vec4 cameraPos", vanillaMain);

        int ourFunction = ours.IndexOf(
            "vec4 taaParticleWorldPos(WarpState st, vec3 taaParticlePosition)", StringComparison.Ordinal);
        Assert.True(ourFunction > 0);
        string ourBranch = Between(ours, "#if defined(VEC3SCALE)", "return taaWorldPos;", ourFunction)
            // The only permitted differences: the local name, the argument name,
            // and the warp reaching the state-taking overloads instead of the
            // currentWarpState() wrappers.
            .Replace("taaWorldPos", "worldPos")
            .Replace("taaParticlePosition", "particlePosition")
            .Replace("applyVertexWarpingState(st, ", "applyVertexWarping(")
            .Replace("applyGlobalWarpingState(st, ", "applyGlobalWarping(");

        Assert.Equal(Squash(vanillaBranch), Squash(ourBranch));
    }

    // ------------------------------------------- (b) the OIT merge reactive

    [Fact]
    public void TheMergeWritesOneMinusRevealageIntoTheReactiveChannelOnly()
    {
        string compose = Read("sources/shaders/transparentcompose.fsh");

        Assert.Contains("#if TAAMOTION > 0", compose);
        Assert.Contains("layout(location = TAAMOTIONLOCATION) out vec4 outMotion;", compose);

        // Zero into r, g and a: with the motion attachment on FUNC_ADD (ONE,
        // ONE) those channels add zero, so the vector and the writer depth of
        // the opaque surface behind the transparency survive and only the
        // reactive channel accumulates.
        Assert.Contains("outMotion = vec4(0.0, 0.0, clamp(anet, 0.0, 1.0), 0.0);", compose);

        // anet is vanilla's own coverage term - the alpha this pass is
        // composited with - not a second, parallel computation.
        Assert.Contains("float anet = 1.0 - texelFetch(revealage, ivec2(gl_FragCoord), 0).r;", compose);
        Assert.Contains("outColor = vec4(unproject(k), anet);", compose);
    }

    /// <summary>
    /// TAA off must be byte-identical to today's chain. For a whole-file shader
    /// override that means: delete every <c>#if TAAMOTION &gt; 0</c> region and
    /// the comments, and what is left has to be the vanilla file.
    ///
    /// The vanilla text is read from the release archive, never from
    /// <c>.vanilla/win-x64/...</c>: <c>make deploy</c> copies Optimum's
    /// overrides straight into that tree, so a test that compared against it
    /// would be comparing a file with itself from the first deploy onwards.
    /// </summary>
    [Theory]
    [InlineData("particlescube.vsh")]
    [InlineData("particlescube.fsh")]
    [InlineData("transparentcompose.fsh")]
    public void WithTaaOffTheOverridesAreTheVanillaShaders(string shader)
    {
        string ours = Read("sources/shaders/" + shader);
        string? vanilla = VanillaShaderArchive.TryRead("shaders/" + shader);
        if (vanilla == null) return;

        Assert.Equal(Squash(StripComments(vanilla)), Squash(StripComments(StripTaaRegions(ours))));
    }

    // --------------------------------------------------------- the passes

    [Fact]
    public void SystemRenderParticlesOpensTheMotionWindowAroundTheCubeDraw()
    {
        // Read from the source of truth, not the patch: a patch carries its
        // hunks plus three lines of context, which is not a parsable method
        // body. That the change ships at all is asserted separately, from the
        // patch and the patcher's target list, below.
        string particles = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderParticles.cs");

        string pass = MethodBodyAfter(particles, "public void OnRenderFrame3D(float deltaTime)");

        // The P3 window (the motion attachment ADDED to Primary's set), not the
        // motion-only one: this pass shades and writes motion in one draw.
        Assert.Contains("optimumPlatform.BeginMotionWrite()", pass);
        Assert.Contains("optimumPlatform.EndMotionWrite();", pass);
        // Closed on every path, including a throwing draw.
        Assert.Contains("finally", pass);

        // The window has to be open before GlToggleBlend, because that call is
        // what forces replace blending onto the motion attachment.
        int begin = pass.IndexOf("BeginMotionWrite()", StringComparison.Ordinal);
        int blend = pass.IndexOf("GlToggleBlend(on: true)", StringComparison.Ordinal);
        Assert.True(begin >= 0 && blend > begin,
            "the motion window must be opened before blending is turned on");

        Assert.Contains("SetOptimumMotionUniforms(particlescube);", pass);

        // The previous-frame uniforms, from the shared frame contract.
        string uniforms = MethodBodyAfter(particles, "private void SetOptimumMotionUniforms(IShaderProgram program)");
        Assert.Contains("frame.GetPrevProjection(EnumTemporalView.World)", uniforms);
        Assert.Contains("frame.PrevCameraMatrixOrigin", uniforms);
        Assert.Contains("frame.ApplyMotionUniforms(program);", uniforms);

        // Quad particles stay out of it: they render into the Transparent
        // target, where the motion attachment is not even present.
        string oit = MethodBodyAfter(particles, "public void OnRenderFrame3DOIT(float deltaTime)");
        Assert.DoesNotContain("MotionWrite", oit);

        // And the change reaches the installed runtime: the patch exists and
        // carries the window.
        string? patch = TryFind("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderParticles.cs.patch");
        Assert.True(patch != null, "SystemRenderParticles has no patch, so the change never ships");
        Assert.Contains("BeginMotionWrite()", PatchReader.ReadPatchedContent(patch!));
    }

    [Fact]
    public void TheMergeOpensTheWindowAndPutsTheMotionAttachmentOnAdditiveBlending()
    {
        // Source of truth, for the same reason as above; the patch is checked
        // for the two new symbols at the end.
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        string merge = MethodBodyAfter(platform, "public override void MergeTransparentRenderPass()");

        Assert.Contains("bool optimumMotionWrite = BeginMotionWrite();", merge);
        Assert.Contains("ApplyOptimumMotionAccumulateBlendState();", merge);
        Assert.Contains("EndMotionWrite();", merge);

        // The window is opened after the global blend mode is set, or the mode
        // would overwrite the per-attachment factors; and the attachment is put
        // back on replace before the window closes, so no later pass inherits
        // the accumulating factors.
        int mode = merge.IndexOf("SetBlend(true, EnumBlendMode.Standard);", StringComparison.Ordinal);
        int begin = merge.IndexOf("BeginMotionWrite();", StringComparison.Ordinal);
        int accumulate = merge.IndexOf("ApplyOptimumMotionAccumulateBlendState();", StringComparison.Ordinal);
        int draw = merge.IndexOf("RenderFullscreenTriangle(screenQuad);", StringComparison.Ordinal);
        int restore = merge.IndexOf("ApplyOptimumMotionBlendState();", StringComparison.Ordinal);
        int end = merge.IndexOf("EndMotionWrite();", StringComparison.Ordinal);
        Assert.True(mode >= 0 && begin > mode, "the window must be opened after the global blend mode is set");
        Assert.True(accumulate > begin && draw > accumulate, "additive blending must be set before the draw");
        Assert.True(restore > draw && end > restore, "replace blending must be restored before the window closes");

        // The blend state itself, on both backends: FUNC_ADD with (ONE, ONE).
        string state = MethodBodyAfter(platform, "private void ApplyOptimumMotionAccumulateBlendState()");
        Assert.Contains("if (!OptimumMotionWriteActive || MotionAttachmentIndex < 0) return;", state);
        Assert.Contains("optimumDevice.SetBlendEquation(MotionAttachmentIndex, 32774);", state);
        Assert.Contains("optimumDevice.SetBlendFuncSeparate(MotionAttachmentIndex, 1, 1, 1, 1);", state);
        Assert.Contains("GL.BlendEquation(MotionAttachmentIndex, (BlendEquationMode)32774);", state);
        Assert.Contains("GL.BlendFunc(MotionAttachmentIndex, (BlendingFactorSrc)1, (BlendingFactorDest)1);", state);

        string? patch = TryFind("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch");
        Assert.True(patch != null, "ClientPlatformWindows has no patch, so the change never ships");
        string patched = PatchReader.ReadPatchedContent(patch!);
        Assert.Contains("ApplyOptimumMotionAccumulateBlendState", patched);
        Assert.Contains("bool optimumMotionWrite = BeginMotionWrite();", patched);
    }

    // -------------------------------------------------------------- the ship

    [Fact]
    public void CecilPatcherShipsEveryParticleMotionMethodAndMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"Vintagestory.Client.NoObf.SystemRenderParticles\", \"OnRenderFrame3D\", 1", patcher);
        Assert.Contains("\"SetOptimumMotionUniforms\"", patcher);
        Assert.Contains("\"ApplyOptimumMotionAccumulateBlendState\"", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"MergeTransparentRenderPass\", 0", patcher);
    }

    /// <summary>
    /// A mod that ships its own particlescube replaces the writer outright; one
    /// that ships transparentcompose removes the only reactive value every OIT
    /// transparent has. Either way that content falls back to camera
    /// reprojection with no reactive flag at all, which ghosts exactly the
    /// fast-moving alpha-blended pixels TAA is worst at, so TAA is disabled
    /// rather than fed wrong data.
    /// </summary>
    [Fact]
    public void TheCompatibilityScannerDisablesTaaForAnExternalParticleOrMergeShader()
    {
        string scanner = Read("Optimum.Launcher/ShaderCompatibilityScanner.cs");

        foreach (string shader in new[]
        {
            "particlescube.vsh", "particlescube.fsh", "transparentcompose.fsh",
        })
        {
            Assert.Contains("HasExternalShader(report, \"" + shader + "\")", scanner);
        }
        Assert.Contains("AddFeatureDecision(report, \"Taa\", externalMotionShader,", scanner);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Every <c>#if TAAMOTION &gt; 0</c> region removed, including its
    /// <c>#else</c> branch if it has one, with nested conditionals tracked so a
    /// region that contains one is not cut short.
    /// </summary>
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

    /// <summary>Whole-line // comments dropped from both sides, so the
    /// override's explanations do not have to exist in vanilla.</summary>
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
