using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Source coverage for the TAA P3 standard-shader motion-vector writer: the
/// standard shader pair, the per-object previous-transform store that feeds it,
/// the renderers that name themselves to it, and the plumbing that has to ship
/// all of it (patcher entries, mod-patcher manifests, packaging).
///
/// Text assertions only prove the wiring exists - the GPU test
/// (Optimum.Render.Vulkan.Tests/TaaStandardMotionWriterTests) proves the numbers.
/// What they catch is the failure this project keeps hitting: a change that works
/// in the build tree and never reaches the installed runtime because a patcher
/// entry was missed.
/// </summary>
public class TaaStandardMotionCoverageTests
{
    // ------------------------------------------------------------- the shaders

    [Fact]
    public void TheStandardVertexShaderReplaysTheCallersOwnWarpBranch()
    {
        string vertex = Read("sources/shaders/standard.vsh");

        Assert.Contains("#if TAAMOTION > 0", vertex);
        Assert.Contains("out vec4 taaPrevClip;", vertex);

        // The previous half of every input the current position uses.
        foreach (string uniform in new[]
        {
            "prevProjectionMatrix", "prevViewMatrix", "prevModelMatrix",
            "cameraPosDelta", "taaHistoryValid",
        })
        {
            Assert.True(DeclaresUniform(vertex, uniform),
                uniform + " is not declared by standard.vsh");
        }

        Assert.Contains("taaPrevWorld = prevModelMatrix * vec4(vertexPositionIn, 1.0);", vertex);
        Assert.Contains("WarpState taaPrev = previousWarpState();", vertex);

        // Both warp branches, replayed exactly as the current position takes them:
        // a dropped item passes dontWarpVertices 0, a held item 2 (quarter warp),
        // and a block-entity model can pass neither.
        Assert.Contains("if (dontWarpVertices == 0) {", vertex);
        Assert.Contains("if (dontWarpVertices == 2) {", vertex);
        Assert.Contains("applyVertexWarpingState(taaPrev, flags | addRenderFlags, taaPrevWorld)", vertex);
        Assert.Contains("applyGlobalWarpingState(taaPrev, taaPrevWorld)", vertex);
        Assert.Contains("taaPrevWorld = mix(taaPrevWorld, taaNewPos, 0.25);", vertex);

        // No usable history: camera-only motion, the same rule the terrain and
        // entity writers and the resolve's fallback use.
        Assert.Contains("taaPrevWorld = vec4(worldPos.xyz + cameraPosDelta, 1.0);", vertex);
        Assert.Contains("taaPrevClip = prevProjectionMatrix * (prevViewMatrix * taaPrevWorld);", vertex);

        // Accuracy rule 1: the z-fighting w-offset applies to both clip positions
        // or the pair disagrees by it.
        Assert.Contains("gl_Position.w += extraZOffset;", vertex);
        Assert.Contains("taaPrevClip.w += extraZOffset;", vertex);
    }

    [Fact]
    public void TheStandardFragmentShaderWritesTheMotionAttachmentWithItsOwnDepth()
    {
        string fragment = Read("sources/shaders/standard.fsh");

        Assert.Contains("#if TAAMOTION > 0", fragment);
        Assert.Contains("in vec4 taaPrevClip;", fragment);
        Assert.Contains("layout(location = TAAMOTIONLOCATION) out vec4 outMotion;", fragment);

        // The contract taa-resolve.fsh consumes.
        Assert.Contains("vec2 prevPixel = (taaPrevClip.xy / taaPrevClip.w * 0.5 + 0.5) * taaRenderSize;", fragment);
        Assert.Contains("vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;", fragment);
        Assert.Contains("return vec4(prevPixel - currentPixel, reactive, writerDepth);", fragment);
        Assert.Contains("if (taaPrevClip.w <= 1e-6) return vec4(0.0);", fragment);

        // The first-person item program writes gl_FragDepth, so the writer depth
        // has to carry the same offset or the resolve's depth-match test rejects
        // every first-person item pixel.
        Assert.Contains("gl_FragDepth = gl_FragCoord.z + depthOffset;", fragment);
        Assert.Contains("outMotion = taaMotionVector(taaReactive, clamp(gl_FragCoord.z + depthOffset, 0.0, 1.0));", fragment);
        Assert.Contains("outMotion = taaMotionVector(taaReactive, gl_FragCoord.z);", fragment);

        Assert.True(DeclaresUniform(fragment, "taaReactive"));
        Assert.True(DeclaresUniform(fragment, "taaRenderSize"));
        Assert.True(DeclaresUniform(fragment, "taaJitterPx"));
    }

    // ---------------------------------------------------- the per-draw history

    [Fact]
    public void TheFrameContractKeepsPerObjectHistoryForStandardShaderDraws()
    {
        string frame = Read("VintagestoryApi/Client/Render/OptimumTemporalFrame.cs");
        string vertex = Read("sources/shaders/standard.vsh");
        string fragment = Read("sources/shaders/standard.fsh");

        Assert.Contains("public static class OptimumStandardMotion", frame);
        Assert.Contains("ConditionalWeakTable<object, History>", frame);

        string apply = BodyOf(frame, "public static bool Apply(IShaderProgram program, object identity, object shape, float[] modelMatrix)");

        // Every uniform the hook sets must be declared by the shader that reads
        // it, or the HasUniform guard silently drops it.
        foreach (string uniform in new[] { "prevProjectionMatrix", "prevViewMatrix", "prevModelMatrix" })
        {
            Assert.Contains("program.UniformMatrix(\"" + uniform + "\"", apply);
            Assert.True(DeclaresUniform(vertex, uniform), uniform + " is set but declared by no shader");
        }
        Assert.Contains("program.Uniform(\"taaHistoryValid\", valid ? 1 : 0);", apply);
        Assert.True(DeclaresUniform(vertex, "taaHistoryValid"));
        Assert.Contains("program.Uniform(\"taaReactive\", valid ? 0f : 1f);", apply);
        Assert.True(DeclaresUniform(fragment, "taaReactive"));

        // The shared warp/jitter/render-size block comes from the one helper the
        // terrain and entity writers use, not from a second copy of it.
        Assert.Contains("frame.ApplyMotionUniforms(program);", apply);

        // The two warp uniforms a standard-shader draw overrides for itself: a
        // swimming dropped item sets waterWaveCounter. Read through the recorder
        // the entity writer already installs rather than a second hook.
        Assert.Contains("OptimumEntityMotion.ScratchWindWaveIntensity", apply);
        Assert.Contains("OptimumEntityMotion.ScratchWaterWaveCounter", apply);
        Assert.Contains("program.Uniform(\"prevWindWaveIntensity\"", apply);
        Assert.Contains("program.Uniform(\"prevWaterWaveCounter\"", apply);

        // History is only usable when the same object was drawn last frame, with
        // the same mesh, under the same view, in a frame that did not reset.
        Assert.Contains("!frame.Reset &&", apply);
        Assert.Contains("history.PreviousFrame == frame.FrameIndex - 1 &&", apply);
        Assert.Contains("ReferenceEquals(history.PrevShape, shape) &&", apply);
        Assert.Contains("history.PrevView == view &&", apply);
        Assert.Contains("frame.WasViewCaptured(view)", apply);

        // The roll happens once per frame, so something drawn twice in a frame
        // still compares against the frame before rather than its own first draw.
        Assert.Contains("if (history.CapturedFrame != frame.FrameIndex)", apply);

        // The hand FOV is a different view with a different previous projection.
        Assert.Contains("EnumTemporalView view = frame.ActiveView;", apply);
        Assert.Contains("frame.GetPrevProjection(view)", apply);
    }

    // ------------------------------------------------ the instrumented callers

    /// <summary>
    /// Each standard-shader user that draws in the Opaque stage has to name itself
    /// to the store and open the draw-buffer window around its own draw. A narrow
    /// window, because a standard-shader draw that is not instrumented must stay
    /// outside it - inside it the attachment would keep another surface's vector.
    /// </summary>
    [Theory]
    [InlineData("patches/VSEssentials/EntityRenderer/EntityShapeRenderer.cs.patch",
        "VSEssentials/EntityRenderer/EntityShapeRenderer.cs",
        "OptimumStandardMotion.Apply(prog, apap, renderInfo.ModelRef, ItemModelMat.Values);")]
    [InlineData("patches/VSEssentials/EntityRenderer/EntityItemRenderer.cs.patch",
        "VSEssentials/EntityRenderer/EntityItemRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, renderInfo.ModelRef, ModelMat);")]
    [InlineData("patches/VSSurvivalMod/BlockEntityRenderer/QuernTopRenderer.cs.patch",
        "VSSurvivalMod/BlockEntityRenderer/QuernTopRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, meshref, ModelMat.Values);")]
    public void EveryInstrumentedStandardShaderUserStoresItsPreviousTransformAndOpensTheWindow(
        string patch, string source, string apply)
    {
        string renderer = ReadPatchedOrSource(patch, source);

        Assert.Contains(apply, renderer);
        Assert.Contains("OptimumMotionWrite.Begin();", renderer);
        Assert.Contains("if (optimumMotionWrite) OptimumMotionWrite.End();", renderer);
    }

    /// <summary>
    /// The shadow pass runs the same code with a different program that has no
    /// motion output at all, so it must never open the window.
    /// </summary>
    [Fact]
    public void TheShadowPassNeverOpensTheMotionWindow()
    {
        foreach ((string patch, string source) in new[]
        {
            ("patches/VSEssentials/EntityRenderer/EntityShapeRenderer.cs.patch",
                "VSEssentials/EntityRenderer/EntityShapeRenderer.cs"),
            ("patches/VSEssentials/EntityRenderer/EntityItemRenderer.cs.patch",
                "VSEssentials/EntityRenderer/EntityItemRenderer.cs"),
        })
        {
            string renderer = ReadPatchedOrSource(patch, source);
            Assert.Contains("bool optimumMotionWrite = !isShadowPass && OptimumMotionWrite.Begin();", renderer);
        }
    }

    // --------------------------------------------------------------- the ship

    [Fact]
    public void ModPatcherManifestsCarryTheChangedStandardShaderRenderers()
    {
        string manifest = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("new(\"Vintagestory.GameContent.EntityShapeRenderer\", \"RenderItem\", 5)", manifest);
        Assert.Contains("new(\"Vintagestory.GameContent.EntityItemRenderer\", \"DoRender3DOpaque\", 2)", manifest);
        Assert.Contains("new(\"Vintagestory.GameContent.QuernTopRenderer\", \"OnRenderFrame\", 2)", manifest);
    }

    /// <summary>
    /// An external mod that ships its own standard shader would not have the
    /// writer, so TAA has to switch itself off rather than reproject items by
    /// whatever happens to be in the attachment.
    /// </summary>
    [Fact]
    public void TheScannerDisablesTaaForAnExternalStandardShader()
    {
        string scanner = Read("Optimum.Launcher/ShaderCompatibilityScanner.cs");

        Assert.Contains("HasExternalShader(report, \"standard.vsh\")", scanner);
        Assert.Contains("HasExternalShader(report, \"standard.fsh\")", scanner);
        Assert.Contains("AddFeatureDecision(report, \"Taa\"", scanner);
    }

    /// <summary>
    /// The overrides only reach a running client if `make deploy` and every
    /// packager copy sources/shaders - they do already, directory-wide, so this
    /// only guards against a regression that starts naming files.
    /// </summary>
    [Fact]
    public void DeployAndEveryPackagerShipTheStandardShaderOverrides()
    {
        foreach (string path in new[]
        {
            "Makefile", "scripts/package-linux.sh", "scripts/package-macos.sh", "scripts/package-linux.ps1",
        })
        {
            string text = Read(path);
            Assert.Contains("sources/shaders", text.Replace('\\', '/'));
            Assert.DoesNotContain("standard.vsh", text);
        }
    }

    // ----------------------------------------------------------------- helpers

    private static bool DeclaresUniform(string shader, string name)
    {
        foreach (string line in shader.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("uniform ", StringComparison.Ordinal)) continue;
            string declaration = trimmed.Substring("uniform ".Length);
            int semicolon = declaration.IndexOf(';');
            if (semicolon < 0) continue;
            declaration = declaration.Substring(0, semicolon);
            int assign = declaration.IndexOf('=');
            if (assign >= 0) declaration = declaration.Substring(0, assign);
            int space = declaration.TrimEnd().LastIndexOf(' ');
            if (space < 0) continue;
            if (declaration.TrimEnd().Substring(space + 1) == name) return true;
        }
        return false;
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
