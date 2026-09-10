using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Source coverage for the TAA P3 skinned-entity motion-vector writer: the
/// entityanimated shader pair, the second bone block that carries the previous
/// pose, the per-draw hook that fills it, and the plumbing that has to ship all
/// of it (patcher entries, mod-patcher manifests).
///
/// Text assertions only prove the wiring exists - the GPU test
/// (Optimum.Render.Vulkan.Tests/TaaEntityMotionWriterTests) proves the numbers.
/// What they catch is the failure this project keeps hitting: a change that works
/// in the build tree and never reaches the installed runtime because a patcher
/// entry was missed.
/// </summary>
public class TaaEntityMotionCoverageTests
{
    // ------------------------------------------------------------- the shaders

    [Fact]
    public void TheEntityVertexShaderSkinsTheVertexTwiceThroughTheSameWarpBranch()
    {
        string vertex = Read("sources/shaders/entityanimated.vsh");

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
                uniform + " is not declared by entityanimated.vsh");
        }

        // The second bone block, mirroring "Animation" so the same skinning runs
        // twice. A shader that reused ElementTransforms would silently produce
        // zero motion for every animated entity.
        Assert.Contains("uniform AnimationPrev", vertex);
        Assert.Contains("mat4 values[MAXANIMATEDELEMENTS];", vertex);
        Assert.Contains("} PrevElementTransforms;", vertex);
        Assert.Contains("prevModelMatrix * PrevElementTransforms.values[jointId]", vertex);

        // Same branch as the current position, with the previous warp state.
        Assert.Contains("WarpState taaPrev = previousWarpState();", vertex);
        Assert.Contains("applyLiquidWarpingState(taaPrev, true, taaPrevWorld, 5)", vertex);
        Assert.Contains("applyVertexWarpingState(taaPrev, renderFlags, taaPrevWorld)", vertex);
        Assert.Contains("applyGlobalWarpingState(taaPrev, taaPrevWorld)", vertex);

        // No usable history: camera-only motion, the same rule the terrain writer
        // and the resolve's fallback use.
        Assert.Contains("taaPrevWorld = vec4(worldPos.xyz + cameraPosDelta, 1.0);", vertex);
        Assert.Contains("taaPrevClip = prevProjectionMatrix * (prevViewMatrix * taaPrevWorld);", vertex);

        // The writer has to sit before `int renderFlags = extraGlow + flags;`
        // shadows the flat output, or it warps the previous position with the
        // wrong flags.
        int writer = vertex.IndexOf("taaHistoryValid != 0", StringComparison.Ordinal);
        // LastIndexOf: the comment above the writer quotes the shadowing line.
        int shadow = vertex.LastIndexOf("int renderFlags = extraGlow + flags;", StringComparison.Ordinal);
        Assert.True(writer >= 0 && shadow > writer,
            "the TAA writer must run before renderFlags is shadowed by the local");
    }

    [Fact]
    public void TheEntityFragmentShaderWritesTheMotionAttachmentWithItsOwnDepth()
    {
        string fragment = Read("sources/shaders/entityanimated.fsh");

        Assert.Contains("#if TAAMOTION > 0", fragment);
        Assert.Contains("in vec4 taaPrevClip;", fragment);
        Assert.Contains("layout(location = TAAMOTIONLOCATION) out vec4 outMotion;", fragment);

        // The contract taa-resolve.fsh consumes.
        Assert.Contains("vec2 prevPixel = (taaPrevClip.xy / taaPrevClip.w * 0.5 + 0.5) * taaRenderSize;", fragment);
        Assert.Contains("vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;", fragment);
        Assert.Contains("return vec4(prevPixel - currentPixel, reactive, writerDepth);", fragment);
        Assert.Contains("if (taaPrevClip.w <= 1e-6) return vec4(0.0);", fragment);

        // The first-person hand and item programs write gl_FragDepth, so the
        // writer depth has to carry the same offset or the resolve's depth-match
        // test rejects every hand pixel.
        Assert.Contains("gl_FragDepth = gl_FragCoord.z + depthOffset;", fragment);
        Assert.Contains("outMotion = taaMotionVector(taaReactive, clamp(gl_FragCoord.z + depthOffset, 0.0, 1.0));", fragment);
        Assert.Contains("outMotion = taaMotionVector(taaReactive, gl_FragCoord.z);", fragment);

        // Only the opaque variant writes into Primary; the OIT twin already
        // fills six outputs on Transparent.
        Assert.Contains("#if TAAMOTION > 0 && USEOIT==0", fragment);
        Assert.True(DeclaresUniform(fragment, "taaReactive"));
        Assert.True(DeclaresUniform(fragment, "taaRenderSize"));
        Assert.True(DeclaresUniform(fragment, "taaJitterPx"));
    }

    // ------------------------------------------------------- the previous pose

    /// <summary>
    /// Both programs that compile entityanimated.vsh need their own copy of the
    /// previous-pose block: the shared one from initUbos, and the first-person
    /// hands', which clears the program's UBOs and rebuilds them by hand.
    /// </summary>
    [Fact]
    public void BothEntityanimatedProgramsCreateTheirOwnPreviousBoneBlock()
    {
        string shared = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgramEntityanimated.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgramEntityanimated.cs");

        Assert.Contains("ubos[\"Animation\"] = ScreenManager.Platform.CreateUBO(ProgramId, 0, \"Animation\"", shared);
        Assert.Contains("if (!Oit && Vintagestory.API.Config.OptimumConfig.EffectiveTaa)", shared);
        Assert.Contains("ubos[\"AnimationPrev\"] = ScreenManager.Platform.CreateUBO(ProgramId, 1, \"AnimationPrev\"", shared);

        string fpHands = ReadPatchedOrSource(
            "patches/VSEssentials/EntityRenderer/ModSystemFpHands.cs.patch",
            "VSEssentials/EntityRenderer/ModSystemFpHands.cs");

        Assert.Contains("if (OptimumConfig.EffectiveTaa)", fpHands);
        Assert.Contains(
            "fpModeHandShader.UBOs[\"AnimationPrev\"] = capi.Render.CreateUBO(fpModeHandShader, 1, \"AnimationPrev\"",
            fpHands);
    }

    /// <summary>
    /// A second uniform block only works if the buffer remembers which point it
    /// belongs to. Vanilla's Bind() hard-coded binding point 0, which would make
    /// every Update of "Animation" steal the block "AnimationPrev" was bound to.
    /// </summary>
    [Fact]
    public void TheUniformBufferRemembersItsBlockAndBindingPoint()
    {
        string ubo = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/UBO.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/UBO.cs");

        Assert.Contains("public string BlockName;", ubo);
        Assert.Contains("public int BindingPoint;", ubo);
        Assert.Contains("GL.BindBufferBase((BufferRangeTarget)35345, BindingPoint, Handle);", ubo);
        Assert.DoesNotContain("GL.BindBufferBase((BufferRangeTarget)35345, 0, Handle);", ubo);

        // The bone upload is the gate every entity draw passes through.
        Assert.Contains("if (BlockName == \"Animation\")", ubo);
        Assert.Contains(
            "optimumProgram.ubos.TryGetValue(\"AnimationPrev\", out var optimumPrevBones)",
            ubo);
        Assert.Contains(
            "OptimumEntityMotion.OnAnimationUpload(optimumProgram, optimumPrevBones, data, size);",
            ubo);

        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // Both backends record it, or the GL path binds to point 0 regardless.
        Assert.Equal(2, Count(platform, "BlockName = blockName;"));
        Assert.Equal(2, Count(platform, "BindingPoint = bindingPoint;"));
    }

    // ---------------------------------------------------- the per-draw history

    [Fact]
    public void TheFrameContractKeepsPerEntityHistoryKeyedOnTheAnimator()
    {
        string frame = Read("VintagestoryApi/Client/Render/OptimumTemporalFrame.cs");
        string vertex = Read("sources/shaders/entityanimated.vsh");
        string fragment = Read("sources/shaders/entityanimated.fsh");

        Assert.Contains("public static class OptimumEntityMotion", frame);
        // Identity of the animator's own array is what decides whether last
        // frame's pose belongs to this entity: a respawn, a re-tesselation or a
        // changed animator hands over a different array and gets no history.
        Assert.Contains("ConditionalWeakTable<object, History>", frame);
        Assert.Contains("histories.GetValue(bones, _ => new History());", frame);

        // Every uniform the hook sets must be declared by the shader that reads
        // it, or the HasUniform guard silently drops it.
        foreach (string uniform in new[] { "prevProjectionMatrix", "prevViewMatrix", "prevModelMatrix" })
        {
            Assert.Contains("program.UniformMatrix(\"" + uniform + "\"", frame);
            Assert.True(DeclaresUniform(vertex, uniform), uniform + " is set but declared by no shader");
        }
        Assert.Contains("program.Uniform(\"taaHistoryValid\", valid ? 1 : 0);", frame);
        Assert.True(DeclaresUniform(vertex, "taaHistoryValid"));
        Assert.Contains("program.Uniform(\"taaReactive\", valid ? 0f : 1f);", frame);
        Assert.True(DeclaresUniform(fragment, "taaReactive"));

        // The two warp uniforms an entity overrides for itself; the global
        // previous warp state would replay a warp the entity never had.
        Assert.Contains("uniformName == \"windWaveIntensity\"", frame);
        Assert.Contains("uniformName == \"waterWaveCounter\"", frame);
        Assert.Contains("program.Uniform(\"prevWindWaveIntensity\"", frame);
        Assert.Contains("program.Uniform(\"prevWaterWaveCounter\"", frame);

        // History is only usable when the same entity was drawn last frame, under
        // the same view, with the same joint count, in a frame that did not reset.
        string validity = MethodBodyAfter(frame, "public static void OnAnimationUpload");
        Assert.Contains("!frame.Reset &&", validity);
        Assert.Contains("history.PreviousFrame == frame.FrameIndex - 1 &&", validity);
        Assert.Contains("history.PrevFloatCount == floats &&", validity);
        Assert.Contains("history.PrevView == view &&", validity);
        Assert.Contains("frame.WasViewCaptured(view)", validity);

        // The roll happens once per frame, so an entity drawn twice in a frame
        // still compares against the frame before rather than its own first draw.
        Assert.Contains("if (history.CapturedFrame != frame.FrameIndex)", validity);
    }

    /// <summary>
    /// The hand FOV is a different view with a different previous projection, and
    /// nothing tells a draw which one it is under except the projection last
    /// loaded by Set3DProjection.
    /// </summary>
    [Fact]
    public void TheFrameContractTracksWhichViewTheLoadedProjectionBelongsTo()
    {
        string frame = Read("VintagestoryApi/Client/Render/OptimumTemporalFrame.cs");

        Assert.Contains("EnumTemporalView ActiveView { get; }", frame);
        Assert.Contains("public EnumTemporalView ActiveView { get; private set; }", frame);

        string record = MethodBodyAfter(frame, "public void RecordProjection(EnumTemporalView view, double[] matrix)");
        Assert.Contains("ActiveView = view;", record);

        // Reset per frame, so a frame that never sets up the hand view cannot
        // inherit it from the last one.
        string advance = MethodBodyAfter(frame, "public void Advance(");
        Assert.Contains("ActiveView = EnumTemporalView.World;", advance);

        // The writer reads the previous projection of that same view.
        Assert.Contains("frame.GetPrevProjection(view)", frame);
    }

    [Fact]
    public void TheHooksAreGatedOnASingleFlagRaisedWhereTaaMotionIsStamped()
    {
        string registry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        Assert.Contains("OptimumEntityMotion.Enabled = taaMotion;", registry);

        string shaderProgram = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgramBase.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgramBase.cs");
        Assert.Contains(
            "if (OptimumEntityMotion.Enabled) OptimumEntityMotion.NoteWarpUniform(uniformName, value);",
            shaderProgram);
        Assert.Contains(
            "if (OptimumEntityMotion.Enabled && uniformName == \"modelMatrix\") OptimumEntityMotion.NoteModelMatrix(matrix);",
            shaderProgram);
    }

    // ------------------------------------------------- the draw-buffer windows

    [Fact]
    public void EveryEntityPassOpensTheMotionDrawBufferWindow()
    {
        string entities = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderEntities.cs");

        // Reuses the terrain stage's window rather than adding a second pair.
        Assert.Contains(
            "bool optimumMotionWrite = optimumPlatform != null && optimumPlatform.BeginMotionWrite();",
            entities);
        Assert.Contains("optimumPlatform.EndMotionWrite();", entities);

        // The mod-side renderers reach the same window through the API.
        string frame = Read("VintagestoryApi/Client/Render/OptimumTemporalFrame.cs");
        Assert.Contains("public static class OptimumMotionWrite", frame);
        Assert.Contains("public static Func<bool> BeginHook;", frame);
        Assert.Contains("public static Action EndHook;", frame);

        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.Contains("OptimumMotionWrite.BeginHook = BeginMotionWrite;", platform);
        Assert.Contains("OptimumMotionWrite.EndHook = EndMotionWrite;", platform);
        // Installed on both framebuffer setup paths, device and GL.
        Assert.Equal(2, Count(platform, "InstallOptimumMotionWriteHooks();"));

        foreach ((string patch, string source) in new[]
        {
            ("patches/VSEssentials/EntityRenderer/EntityPlayerShapeRenderer.cs.patch",
                "VSEssentials/EntityRenderer/EntityPlayerShapeRenderer.cs"),
            ("patches/VSSurvivalMod/Lore/ResoArchives/EchoChamberRenderer.cs.patch",
                "VSSurvivalMod/Lore/ResoArchives/EchoChamberRenderer.cs"),
        })
        {
            string renderer = ReadPatchedOrSource(patch, source);
            Assert.Contains("bool optimumMotionWrite = OptimumMotionWrite.Begin();", renderer);
            Assert.Contains("if (optimumMotionWrite) OptimumMotionWrite.End();", renderer);
        }
    }

    // -------------------------------------------------------------- the ship

    [Fact]
    public void CecilPatcherShipsEveryEntityMotionMethodAndMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"BlockName\",", patcher);
        Assert.Contains("\"BindingPoint\",", patcher);
        Assert.Contains("[\"Vintagestory.Client.NoObf.UBO\"]", patcher);
        Assert.Contains("\"InstallOptimumMotionWriteHooks\",", patcher);

        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderProgramEntityanimated\", \"initUbos\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.SystemRenderEntities\", \"OnRenderOpaque3D\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.UBO\", \"Update\", 3", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.UBO\", \"Bind\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"CreateUBO\", 4", patcher);
    }

    [Fact]
    public void ModPatcherManifestsCarryTheChangedModRenderers()
    {
        string manifest = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains(
            "new(\"Vintagestory.GameContent.EntityPlayerShapeRenderer\", \"DoRender3DOpaque\", 2)",
            manifest);
        Assert.Contains("new(\"Vintagestory.GameContent.ModSystemFpHands\", \"LoadShaders\", 0)", manifest);
        Assert.Contains(
            "new(\"Vintagestory.GameContent.EchoChamberRenderer\", \"DoRender3DOpaque\", 2)",
            manifest);
    }

    /// <summary>
    /// The patched shader files only reach a running client if `make deploy` and
    /// every packager copy sources/shaders - they do already, directory-wide, so
    /// this only guards against a regression that starts naming files.
    /// </summary>
    [Fact]
    public void DeployAndEveryPackagerShipTheEntityShaderOverrides()
    {
        foreach (string path in new[]
        {
            "Makefile", "scripts/package-linux.sh", "scripts/package-macos.sh", "scripts/package-linux.ps1",
        })
        {
            string text = Read(path);
            Assert.Contains("sources/shaders", text.Replace('\\', '/'));
            Assert.DoesNotContain("entityanimated.vsh", text);
        }
    }

    // ----------------------------------------------------------------- helpers

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
