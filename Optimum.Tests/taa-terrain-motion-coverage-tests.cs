using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Source coverage for the TAA P3 terrain motion-vector writers: the shared
/// vertexwarp WarpState include, the chunkopaque/chunktopsoil writers, the
/// draw-buffer window on both backends, and the plumbing that has to ship them
/// (patcher entries, deploy and package copies, the compatibility scanner).
///
/// These are text assertions, which only prove the wiring exists - the GPU test
/// (Optimum.Render.Vulkan.Tests/TaaMotionWriterTests) proves the numbers. What
/// they catch is the failure this project keeps hitting: a change that works in
/// the build tree and never reaches the installed runtime because a patcher
/// entry or a packaging copy was missed.
/// </summary>
public class TaaTerrainMotionCoverageTests
{
    // ------------------------------------------------------------- the include

    [Fact]
    public void TheVertexWarpOverrideCarriesAWarpStateAndKeepsTheVanillaEntryPoints()
    {
        string warp = Read("sources/shaderincludes/vertexwarp.vsh");

        // Every uniform the warp functions read, in one struct, so a writer can
        // evaluate them twice.
        Assert.Contains("struct WarpState", warp);
        foreach (string member in new[]
        {
            "float timeCounter;", "float windWaveCounter;", "float windWaveCounterHighFreq;",
            "float waterWaveCounter;", "float windSpeed;", "vec3 playerpos;",
            "float globalWarpIntensity;", "float glitchWaviness;", "float windWaveIntensity;",
            "float waterWaveIntensity;", "int perceptionEffectId;", "float perceptionEffectIntensity;",
        })
        {
            Assert.Contains(member, warp);
        }

        // The previous-frame uniforms and the accessor pair.
        foreach (string uniform in new[]
        {
            "prevTimeCounter", "prevWindWaveCounter", "prevWindWaveCounterHighFreq",
            "prevWaterWaveCounter", "prevWindSpeed", "prevPlayerpos", "prevGlobalWarpIntensity",
            "prevGlitchWaviness", "prevWindWaveIntensity", "prevWaterWaveIntensity",
            "prevPerceptionEffectId", "prevPerceptionEffectIntensity",
        })
        {
            Assert.Contains("uniform ", warp);
            Assert.Contains(uniform, warp);
        }
        Assert.Contains("WarpState currentWarpState()", warp);
        Assert.Contains("WarpState previousWarpState()", warp);

        // The state-taking forms every writer calls.
        Assert.Contains("vec3 applyPerceptionWarpingState(WarpState st, vec3 worldPos)", warp);
        Assert.Contains("vec4 applyLiquidWarpingState(WarpState st, bool windAffected, vec4 worldPos, float div)", warp);
        Assert.Contains("vec4 applyVertexWarpingState(WarpState st, int renderFlags, vec4 worldPos)", warp);
        Assert.Contains("vec4 applyGlobalWarpingState(WarpState st, vec4 worldPos)", warp);

        // And the vanilla entry points, unchanged in signature, delegating to the
        // current state - every other shader in the game includes this file.
        Assert.Contains("vec3 applyPerceptionWarping(vec3 worldPos) {\n\treturn applyPerceptionWarpingState(currentWarpState(), worldPos);", warp.Replace("\r\n", "\n"));
        Assert.Contains("vec4 applyLiquidWarping(bool windAffected, vec4 worldPos, float div) {\n\treturn applyLiquidWarpingState(currentWarpState(), windAffected, worldPos, div);", warp.Replace("\r\n", "\n"));
        Assert.Contains("vec4 applyVertexWarping(int renderFlags, vec4 worldPos) {\n\treturn applyVertexWarpingState(currentWarpState(), renderFlags, worldPos);", warp.Replace("\r\n", "\n"));
        Assert.Contains("vec4 applyGlobalWarping(vec4 worldPos) {\n\treturn applyGlobalWarpingState(currentWarpState(), worldPos);", warp.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// The maths inside the state-taking functions is the vanilla maths, with the
    /// uniform reads replaced by struct reads and nothing else. Compared against
    /// the vanilla include when the checkout has been bootstrapped; skipped
    /// rather than failed when the proprietary assets are absent.
    /// </summary>
    [Fact]
    public void TheStateOverloadsAreVanillaMathsWithTheUniformsReadFromTheStruct()
    {
        // From the release archive, never from .vanilla/win-x64/...: `make deploy`
        // copies sources/shaderincludes straight into that tree, so this
        // comparison was reading Optimum's own include as the vanilla reference
        // (and failing) after the first deploy. The vanilla shaders are
        // proprietary and never committed, so a checkout that has not
        // bootstrapped has nothing to compare against and skips.
        string? vanilla = VanillaShaderArchive.TryRead("shaderincludes/vertexwarp.vsh");
        if (vanilla == null) return;
        string ours = Read("sources/shaderincludes/vertexwarp.vsh");

        foreach ((string vanillaSignature, string ourSignature) in new[]
        {
            ("vec3 applyPerceptionWarping(vec3", "vec3 applyPerceptionWarpingState(WarpState st, vec3"),
            ("vec4 applyLiquidWarping(bool", "vec4 applyLiquidWarpingState(WarpState st, bool"),
            ("vec4 applyVertexWarping(int", "vec4 applyVertexWarpingState(WarpState st, int"),
            ("vec4 applyGlobalWarping(vec4", "vec4 applyGlobalWarpingState(WarpState st, vec4"),
        })
        {
            string expected = Normalize(BodyOf(vanilla, vanillaSignature));
            // The only permitted differences: the uniform reads became struct
            // reads, and the two internal calls reach the state-taking form.
            string actual = Normalize(BodyOf(ours, ourSignature))
                .Replace("applyPerceptionWarpingState(st, ", "applyPerceptionWarping(")
                .Replace("applyLiquidWarpingState(st, ", "applyLiquidWarping(")
                .Replace("st.", "");
            Assert.Equal(expected, actual);
        }
    }

    // ------------------------------------------------------------- the writers

    [Theory]
    [InlineData("chunkopaque")]
    [InlineData("chunktopsoil")]
    public void TheTerrainWritersEmitTheMotionContract(string program)
    {
        string vertex = Read("sources/shaders/" + program + ".vsh");
        string fragment = Read("sources/shaders/" + program + ".fsh");

        // Compiled in only while TAA is on, so TAA off preprocesses to vanilla.
        Assert.Contains("#if TAAMOTION > 0", vertex);
        Assert.Contains("#if TAAMOTION > 0", fragment);

        // Previous transforms and the camera's own movement.
        Assert.Contains("uniform mat4 prevProjectionMatrix;", vertex);
        Assert.Contains("uniform mat4 prevModelViewMatrix;", vertex);
        Assert.Contains("uniform vec3 cameraPosDelta;", vertex);
        Assert.Contains("out vec4 taaPrevClip;", vertex);

        // prevRel = truePos + cameraPosDelta, warped with the previous state,
        // through the previous unjittered projection and view.
        Assert.Contains("WarpState taaPrev = previousWarpState();", vertex);
        Assert.Contains("vec4 taaPrevPos = vec4(truePos.xyz + cameraPosDelta, 1.0);", vertex);
        Assert.Contains("taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevPos);", vertex);
        Assert.Contains("taaPrevPos = applyGlobalWarpingState(taaPrev, taaPrevPos);", vertex);

        // The output goes to the attachment index the C# side stamps.
        Assert.Contains("layout(location = TAAMOTIONLOCATION) out vec4 outMotion;", fragment);
        Assert.Contains("uniform vec2 taaRenderSize;", fragment);
        Assert.Contains("uniform vec2 taaJitterPx;", fragment);

        // rg = previousPixel - currentUnjitteredPixel, b = reactive, a = window depth.
        Assert.Contains("vec2 prevPixel = (taaPrevClip.xy / taaPrevClip.w * 0.5 + 0.5) * taaRenderSize;", fragment);
        Assert.Contains("vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;", fragment);
        Assert.Contains("return vec4(prevPixel - currentPixel, reactive, gl_FragCoord.z);", fragment);
        // A previous position behind the previous camera is not a motion vector;
        // a zero alpha routes the pixel to the resolve's camera fallback.
        Assert.Contains("if (taaPrevClip.w <= 1e-6) return vec4(0.0);", fragment);
        // Opaque terrain is never reactive.
        Assert.Contains("taaMotionVector(0.0)", fragment);
    }

    /// <summary>
    /// chunkopaque applies a w-offset to beat z-fighting, which moves where the
    /// fragment lands. Leaving it off the previous position would report that
    /// offset as motion on every z-offset block.
    /// </summary>
    [Fact]
    public void ChunkopaquesZFightingOffsetIsAppliedToThePreviousClipPositionToo()
    {
        string vertex = Read("sources/shaders/chunkopaque.vsh");

        Assert.Contains("gl_Position.w += zOffset * 0.00025 / ((gl_Position.z + 3) * 0.05);", vertex);
        Assert.Contains("taaPrevClip.w += taaPrevZOffset * 0.00025 / ((taaPrevClip.z + 3) * 0.05);", vertex);
        Assert.Contains("int taaPrevZOffset = (renderFlags & ZOffsetBitMask) >> 8;", vertex);
    }

    /// <summary>
    /// Vanilla topsoil has applyVertexWarping commented out and only applies the
    /// global warp. The previous position must reproduce that asymmetry, or every
    /// grass-topped block reports a warp it never had.
    /// </summary>
    [Fact]
    public void TopsoilsPreviousPositionSkipsTheVertexWarpJustAsVanillaDoes()
    {
        string vertex = Read("sources/shaders/chunktopsoil.vsh");

        Assert.Contains("//worldPos = applyVertexWarping(renderFlags, worldPos);", vertex);
        Assert.Contains("worldPos = applyGlobalWarping(worldPos);", vertex);
        Assert.DoesNotContain("applyVertexWarpingState(", vertex);
        Assert.Contains("applyGlobalWarpingState(taaPrev, taaPrevPos);", vertex);
        // And no z-offset: vanilla topsoil has none either.
        Assert.DoesNotContain("ZOffsetBitMask", vertex);
    }

    // ------------------------------------------------------------ the defines

    [Fact]
    public void ShaderRegistryStampsTheMotionDefinesOnBothStages()
    {
        string registry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");

        Assert.Contains("bool taaMotion = OptimumConfig.EffectiveTaa;", registry);
        // The location must follow the same condition SetupDefaultFrameBuffers
        // sizes Primary's colour attachments with (SetupSSAO), or the writer
        // emits into a slot the framebuffer does not have.
        Assert.Contains("int taaMotionLocation = ((ClientSettings.SSAOQuality > 0) ? 4 : 2);", registry);
        Assert.Contains("#define TAAMOTION \" + (taaMotion ? 1 : 0) + \"\\r\\n#define TAAMOTIONLOCATION \" + taaMotionLocation", registry);
        Assert.Contains("taaFrag.PrefixCode = taaFrag.PrefixCode + taaDefines;", registry);
        Assert.Contains("taaVert.PrefixCode = taaVert.PrefixCode + taaDefines;", registry);
    }

    // ------------------------------------------------- the draw-buffer window

    [Fact]
    public void ThePlatformOpensAndClosesTheMotionDrawBufferOnBothBackends()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        Assert.Contains("public bool BeginMotionWrite()", platform);
        Assert.Contains("public void EndMotionWrite()", platform);
        Assert.Contains("public bool OptimumMotionWriteActive { get; private set; }", platform);

        // Guards: no motion attachment, no TAA targets, TAA switched off, or a
        // window already open - all no-ops, so callers can wrap unconditionally.
        Assert.Contains("if (MotionAttachmentIndex < 0 || !TaaTargetsReady) return false;", platform);
        Assert.Contains("if (!Vintagestory.API.Config.OptimumConfig.EffectiveTaa) return false;", platform);
        Assert.Contains("if (OptimumMotionWriteActive) return false;", platform);

        // Device path: mask including the motion attachment, then back to the
        // default set (whose size is the attachment's own index).
        Assert.Contains(
            "optimumDevice.SetDrawBuffers(frameBuffers[0].FboId, (1 << (MotionAttachmentIndex + 1)) - 1);",
            platform);
        Assert.Contains(
            "optimumDevice.SetDrawBuffers(frameBuffers[0].FboId, (1 << MotionAttachmentIndex) - 1);",
            platform);

        // GL path: the same two sets, as DrawBuffers arrays - built once and
        // kept, not allocated per window. The narrow windows open per draw (every
        // held item, dropped item and block-entity model), so allocating them
        // inside would be two garbage arrays per instrumented draw per frame.
        Assert.Contains("private DrawBuffersEnum[] optimumMotionDrawBuffersOn;", platform);
        Assert.Contains("private DrawBuffersEnum[] optimumMotionDrawBuffersOff;", platform);
        Assert.Contains("GL.DrawBuffers(optimumMotionDrawBuffersOn.Length, optimumMotionDrawBuffersOn);", platform);
        Assert.Contains("GL.DrawBuffers(optimumMotionDrawBuffersOff.Length, optimumMotionDrawBuffersOff);", platform);

        // Both cached fields have to be transplanted, or the Cecil'd build has
        // BeginMotionWrite referring to members the shipped type does not carry.
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"optimumMotionDrawBuffersOn\"", patcher);
        Assert.Contains("\"optimumMotionDrawBuffersOff\"", patcher);

        // And nothing inside the window allocates.
        int begin = platform.IndexOf("public bool BeginMotionWrite()", StringComparison.Ordinal);
        int end = platform.IndexOf("private void ApplyOptimumMotionBlendState()", begin, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin);
        string window = platform.Substring(begin, end - begin);
        Assert.Equal(2, Count(window, "new DrawBuffersEnum["));
        foreach (string allocation in new[] { "new float[", "new int[", "new List<" })
        {
            Assert.False(window.Contains(allocation, StringComparison.Ordinal),
                "the per-draw motion window must not allocate: " + allocation);
        }
    }

    /// <summary>
    /// The two backends must not disagree about WHICH framebuffer the window
    /// changes. The device call names Primary; GL.DrawBuffers applies to whatever
    /// is bound, so a caller that reached BeginMotionWrite under the shadow map or
    /// the Transparent target would rewrite that target's draw-buffer set on GL
    /// and Primary's on Vulkan. Refusing the window unless Primary is the bound
    /// target makes both paths behave the same and costs the caller only the
    /// camera fallback it would have to use anyway.
    /// </summary>
    [Fact]
    public void TheMotionWindowOnlyOpensWhilePrimaryIsTheBoundTarget()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        int begin = platform.IndexOf("public bool BeginMotionWrite()", StringComparison.Ordinal);
        Assert.True(begin >= 0);
        int drawBuffers = platform.IndexOf("optimumDevice.SetDrawBuffers(frameBuffers[0].FboId, (1 << (MotionAttachmentIndex + 1)) - 1);", begin, StringComparison.Ordinal);
        Assert.True(drawBuffers > begin);

        string guards = platform.Substring(begin, drawBuffers - begin);
        Assert.Contains("if (!ReferenceEquals(CurrentFrameBuffer, frameBuffers[0])) return false;", guards);
    }

    /// <summary>
    /// Terrain passes 2 and 8 draw with blending on. A blended motion vector is a
    /// weighted average of two surfaces' displacements and belongs to neither, so
    /// the attachment gets replace-blending re-applied after every global blend
    /// change - the same treatment the SSAO G-buffer already gets.
    /// </summary>
    [Fact]
    public void TheMotionAttachmentNeverBlends()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        Assert.Contains("private void ApplyOptimumMotionBlendState()", platform);
        Assert.Contains("optimumDevice.SetBlendFuncSeparate(MotionAttachmentIndex, 1, 0, 1, 0);", platform);
        Assert.Contains("GL.BlendFunc(MotionAttachmentIndex, (BlendingFactorSrc)1, (BlendingFactorDest)0);", platform);

        // GlToggleBlend re-applies it, including on the early-returning blend
        // modes, because glBlendFunc resets every attachment's function.
        int toggle = platform.IndexOf("public override void GlToggleBlend(bool on, EnumBlendMode blendMode", StringComparison.Ordinal);
        Assert.True(toggle >= 0);
        string body = platform.Substring(toggle);
        Assert.True(Count(body, "ApplyOptimumMotionBlendState();") >= 6,
            "every blend-mode branch has to re-apply the motion attachment's replace blending");
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"GlToggleBlend\", 2", Read("Optimum.Patcher/Program.cs"));
    }

    // ------------------------------------------------------------- the caller

    [Fact]
    public void ChunkRendererWrapsTheOpaqueAndAfterOitPassesAndSetsThePreviousTransforms()
    {
        string chunk = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");

        // The previous transforms come from the frame contract, and the
        // projection is the UNJITTERED one - a jittered matrix would put two
        // frames' jitter difference into every vector.
        Assert.Contains("private void SetOptimumMotionUniforms(ShaderProgram program)", chunk);
        Assert.Contains("program.UniformMatrix(\"prevProjectionMatrix\", frame.GetPrevProjection(EnumTemporalView.World));", chunk);
        Assert.Contains("program.UniformMatrix(\"prevModelViewMatrix\", frame.PrevCameraMatrixOrigin);", chunk);
        Assert.Contains("frame.ApplyMotionUniforms(program);", chunk);

        // Both passes open and close the window, and both terrain programs get
        // the uniforms.
        Assert.Equal(2, Count(chunk, "bool optimumMotionWrite = optimumPlatform != null && optimumPlatform.BeginMotionWrite();"));
        Assert.Equal(2, Count(chunk, "optimumPlatform.EndMotionWrite();"));
        Assert.Contains("SetOptimumMotionUniforms(chunkopaque);", chunk);
        Assert.Contains("SetOptimumMotionUniforms(chunktopsoil);", chunk);
    }

    /// <summary>
    /// The LiquidDepth prepass keeps the jittered projection (its depth is
    /// compared against the jittered scene) and writes no motion: it renders into
    /// its own framebuffer and never opens the window.
    /// </summary>
    [Fact]
    public void TheLiquidDepthPrepassStaysJitteredAndWritesNoMotion()
    {
        string chunk = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");

        string prepass = MethodBodyAfter(chunk, "public void OnRenderBefore(float dt)");

        Assert.Contains("chunkliquiddepth.ProjectionMatrix = game.CurrentProjectionMatrix;", prepass);
        Assert.DoesNotContain("BeginMotionWrite", prepass);
        Assert.DoesNotContain("SetOptimumMotionUniforms", prepass);
    }

    // ------------------------------------------------------ contract uniforms

    /// <summary>
    /// The shared uniform names the writers declare and the ones the frame
    /// contract sets have to be the same strings; a typo on either side is a
    /// silent zero, which looks exactly like "the surface did not move".
    /// </summary>
    /// <summary>
    /// Where the frame contract takes the camera position decides whether the
    /// terrain writer's cameraPosDelta belongs to the frame being drawn.
    /// PlayerCamera.OnBeforeRenderFrame3D writes EntityPlayer.CameraPos and
    /// shUniforms.PlayerPos from inside the Before render stage, which runs after
    /// Advance; taking them in Advance read the previous frame's values and paired
    /// a stale translation with a fresh previous rotation. Both halves belong next
    /// to CaptureCamera, after the Before stage and before the Opaque one.
    /// </summary>
    [Fact]
    public void TheCameraPositionIsCapturedAfterTheBeforeStageWroteIt()
    {
        string main = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

        int advance = main.IndexOf("OptimumTemporal.Frame.Advance(", StringComparison.Ordinal);
        int beforeStage = main.IndexOf("TriggerRenderStage(EnumRenderStage.Before, dt);", StringComparison.Ordinal);
        int captureCamera = main.IndexOf("OptimumTemporal.Frame.CaptureCamera(", StringComparison.Ordinal);
        int capturePos = main.IndexOf("OptimumTemporal.Frame.CaptureCameraPosition(", StringComparison.Ordinal);

        Assert.True(advance >= 0, "the frame contract is never advanced");
        Assert.True(beforeStage >= 0, "the Before render stage is not in the patched body");
        Assert.True(captureCamera >= 0, "the camera matrices are never frozen");
        Assert.True(capturePos >= 0, "the camera position is never captured");
        Assert.True(advance < beforeStage, "Advance has to run before the Before stage so the prepass sees the jitter");
        Assert.True(beforeStage < capturePos, "the camera position must be read after PlayerCamera wrote it");

        // The rotation and the translation of the frame's camera are taken
        // together, so the previous view matrix and the previous position always
        // belong to the same frame.
        Assert.True(Math.Abs(captureCamera - capturePos) < 700,
            "the camera matrices and the camera position have to be captured together");

        // And Advance must no longer be able to read it: the parameter is gone.
        int advanceEnd = main.IndexOf(';', advance);
        Assert.DoesNotContain("CameraPos", main.Substring(advance, advanceEnd - advance));
    }

    [Fact]
    public void TheFrameContractSetsExactlyTheUniformNamesTheWritersDeclare()
    {
        string frame = Read("sources/VintagestoryApi/Client/Render/OptimumTemporalFrame.cs");
        string warp = Read("sources/shaderincludes/vertexwarp.vsh");
        string fragment = Read("sources/shaders/chunkopaque.fsh");
        string vertex = Read("sources/shaders/chunkopaque.vsh");

        Assert.Contains("public void ApplyMotionUniforms(IShaderProgram program)", frame);

        // The previous warp state lives in the include both writers pull in.
        foreach (string name in new[]
        {
            "prevTimeCounter", "prevWindWaveCounter", "prevWindWaveCounterHighFreq",
            "prevWaterWaveCounter", "prevWindSpeed", "prevGlobalWarpIntensity",
            "prevGlitchWaviness", "prevWindWaveIntensity", "prevWaterWaveIntensity",
            "prevPerceptionEffectId", "prevPerceptionEffectIntensity", "prevPlayerpos",
        })
        {
            // Guarded by HasUniform, because ShaderProgram.Uniform throws on a
            // name the program does not declare and the writers vanish with TAA off.
            Assert.Contains("if (program.HasUniform(\"" + name + "\")) program.Uniform(\"" + name + "\"", frame);
            Assert.True(DeclaresUniform(warp, name), name + " is set by the frame contract but declared by no shader");
        }

        // The screen-space pair is declared by the fragment writer, the camera
        // delta by the vertex writer.
        foreach (string name in new[] { "taaRenderSize", "taaJitterPx" })
        {
            Assert.Contains("if (program.HasUniform(\"" + name + "\")) program.Uniform(\"" + name + "\"", frame);
            Assert.True(DeclaresUniform(fragment, name), name + " is set by the frame contract but declared by no shader");
        }
        Assert.Contains("if (program.HasUniform(\"cameraPosDelta\")) program.Uniform(\"cameraPosDelta\"", frame);
        Assert.True(DeclaresUniform(vertex, "cameraPosDelta"));
    }

    // -------------------------------------------------------------- the ship

    [Fact]
    public void CecilPatcherShipsEveryTerrainMotionMethodAndMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"BeginMotionWrite\"", patcher);
        Assert.Contains("\"EndMotionWrite\"", patcher);
        Assert.Contains("\"OptimumMotionWriteActive\"", patcher);
        Assert.Contains("\"ApplyOptimumMotionBlendState\"", patcher);
        Assert.Contains("\"SetOptimumMotionUniforms\"", patcher);

        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"RenderOpaque\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"RenderAfterOIT\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"OnRenderBefore\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"registerDefaultShaderCodePrefixes\", 2", patcher);
    }

    /// <summary>
    /// Shader includes are a separate asset directory from shaders, so they need
    /// their own copy in the deploy target and in every packaging script -
    /// without it the WarpState vertexwarp.vsh never reaches a running client and
    /// the writers silently compile against the vanilla one.
    /// </summary>
    [Fact]
    public void DeployAndEveryPackagerShipTheShaderIncludes()
    {
        Assert.Contains("sources/shaderincludes", Read("Makefile"));
        Assert.Equal(2, Count(Read("Makefile"), "assets/game/shaderincludes"));

        // Derived, never a hand-kept list: any script that overlays
        // sources/shaders is a packaging path a user can install from, so it has
        // to overlay sources/shaderincludes too. Enumerating them by hand is how
        // scripts/package.ps1 (the Windows packager) and scripts/package-macos.ps1
        // were left behind in the first place, which would have shipped every
        // TAA writer calling WarpState overloads the vanilla include never
        // declares - a compile failure on every terrain, entity, item and
        // instanced program the moment TAA is switched on.
        string scriptsDirectory = Path.GetDirectoryName(PatchReader.FindRepositoryFile("scripts/package-linux.sh"))!;
        var packagers = new List<string>();
        foreach (string path in Directory.EnumerateFiles(scriptsDirectory, "package*"))
        {
            string text = File.ReadAllText(path);
            if (!text.Contains("sources/shaders", StringComparison.Ordinal)) continue;
            packagers.Add(Path.GetFileName(path));

            Assert.True(text.Contains("sources/shaderincludes", StringComparison.Ordinal),
                Path.GetFileName(path) + " overlays sources/shaders but not sources/shaderincludes");
            Assert.True(text.Contains("assets/game/shaderincludes", StringComparison.Ordinal),
                Path.GetFileName(path) + " has no assets/game/shaderincludes destination");

            // The vanilla client tree has no shaderincludes directory of its own,
            // so the destination has to be created before the copy: cp into a
            // missing directory fails and Copy-Item writes a single file named
            // after the directory instead - either way the includes never ship.
            bool createsDestination = Path.GetExtension(path) == ".ps1"
                ? text.Contains("New-Item -ItemType Directory -Force -Path $shaderIncDst", StringComparison.Ordinal)
                : text.Contains("mkdir -p \"$SHADER_INC_DST\"", StringComparison.Ordinal);
            Assert.True(createsDestination,
                Path.GetFileName(path) + " copies shader includes without creating the destination directory");
        }

        // A guard on the guard: if the enumeration ever finds nothing, the loop
        // above passes vacuously.
        foreach (string expected in new[]
        {
            "package-linux.sh", "package-macos.sh", "package-linux.ps1",
            "package-macos.ps1", "package.ps1",
        })
        {
            Assert.Contains(expected, packagers);
        }

        // The Windows packager asserts its staged tree before sealing it; the
        // include belongs in that list, so a silently skipped overlay fails the
        // package instead of shipping.
        Assert.Contains("'assets/game/shaderincludes/vertexwarp.vsh'",
            File.ReadAllText(PatchReader.FindRepositoryFile("scripts/package.ps1")));
    }

    /// <summary>
    /// A mod that ships its own copy of a shader the writers live in - or of the
    /// vertexwarp include they evaluate twice - replaces the writer with one that
    /// emits nothing. The resolve would then reproject those pixels by camera
    /// motion alone while their neighbours use real vectors.
    /// </summary>
    [Fact]
    public void TheCompatibilityScannerDisablesTaaForAnExternalMotionWriterShader()
    {
        string scanner = Read("Optimum.Launcher/ShaderCompatibilityScanner.cs");

        Assert.Contains("AddFeatureDecision(report, \"Taa\", externalMotionShader,", scanner);
        foreach (string shader in new[]
        {
            "chunkopaque.vsh", "chunktopsoil.vsh", "entityanimated.vsh",
            "standard.vsh", "instanced.vsh", "vertexwarp.vsh",
        })
        {
            Assert.Contains("HasExternalShader(report, \"" + shader + "\")", scanner);
        }

        // shaderincludes has to be a recognised shader path at all, or an
        // external vertexwarp.vsh is invisible to the scan.
        Assert.Contains("normalized.IndexOf(\"/shaderincludes/\", StringComparison.OrdinalIgnoreCase)", scanner);
        Assert.Contains("normalized.StartsWith(\"shaderincludes/\", StringComparison.OrdinalIgnoreCase)", scanner);
    }

    /// <summary>
    /// The Vulkan translation corpus has to overlay Optimum's shader includes the
    /// way `make deploy` does, or it keeps translating the vanilla vertexwarp.vsh
    /// while the client runs the WarpState one.
    /// </summary>
    [Fact]
    public void TheVulkanShaderCorpusOverlaysOptimumsIncludesAndCoversTaaOn()
    {
        string corpus = Read("Optimum.Render.Vulkan.Tests/ShaderCorpus.cs");

        Assert.Contains("Path.Combine(RepositoryRoot, \"sources\", \"shaderincludes\")", corpus);
        Assert.Contains("#define TAAMOTION {variant.TaaMotion}", corpus);
        Assert.Contains("#define TAAMOTIONLOCATION {variant.TaaMotionLocation}", corpus);
        Assert.Contains("Name = \"taa-no-ssao\"", corpus);
        Assert.Contains("Name = \"taa-with-ssao\"", corpus);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// The text of one method: from its signature to the matching closing brace,
    /// so a helper declared after it cannot leak into the assertions.
    /// </summary>
    private static string MethodBodyAfter(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "no such method: " + signature);
        return signature + BodyOf(source, signature);
    }

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

    private static string Normalize(string body) => body.Replace("\r\n", "\n").Trim();

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
