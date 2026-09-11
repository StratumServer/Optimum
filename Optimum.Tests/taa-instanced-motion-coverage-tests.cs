using System;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Source coverage for the TAA P3 instanced motion-vector writer: the instanced
/// shader pair, the per-instance previous-transform store that feeds it, the
/// renderers that fill it, and the plumbing that has to ship all of it
/// (mod-patcher manifests, scanner rules, packaging).
///
/// Text assertions only prove the wiring exists - the GPU test
/// (Optimum.Render.Vulkan.Tests/TaaInstancedMotionWriterTests) proves the numbers
/// the shader produces, and TaaInstancedMotionHistoryTests below proves the C#
/// side matches an instance to the right device.
/// </summary>
public class TaaInstancedMotionCoverageTests
{
    // ------------------------------------------------------------- the shaders

    [Fact]
    public void TheInstancedVertexShaderReprojectsThroughThePerInstancePreviousTransform()
    {
        string vertex = Read("sources/shaders/instanced.vsh");

        Assert.Contains("#if TAAMOTION > 0", vertex);
        Assert.Contains("out vec4 taaPrevClip;", vertex);
        Assert.Contains("out float taaInstanceReactive;", vertex);

        // The previous transform is per instance, not a uniform: one draw covers
        // every gear of a shape, and they do not share a previous transform.
        Assert.Contains("layout(location = 9) in mat4 prevTransform;", vertex);
        Assert.Contains("layout(location = 13) in vec4 taaInstanceMeta;", vertex);

        foreach (string uniform in new[] { "prevProjectionMatrix", "prevModelViewMatrix", "cameraPosDelta" })
        {
            Assert.True(DeclaresUniform(vertex, uniform),
                uniform + " is not declared by instanced.vsh");
        }

        Assert.Contains("taaPrevWorld = prevTransform * vec4(vertexPosition, 1.0);", vertex);

        // No usable history: camera-only motion, the same rule the terrain,
        // entity and standard writers and the resolve's fallback use.
        Assert.Contains("taaPrevWorld = vec4(worldPos.xyz + cameraPosDelta, 1.0);", vertex);
        Assert.Contains("taaPrevClip = prevProjectionMatrix * (prevModelViewMatrix * taaPrevWorld);", vertex);
        Assert.Contains("taaInstanceReactive = taaInstanceMeta.y;", vertex);

        // The current position takes no vertex warp and no w-offset in this
        // shader, so neither may appear on the previous one.
        Assert.DoesNotContain("applyVertexWarping", vertex);
        Assert.DoesNotContain("taaPrevClip.w +=", vertex);
    }

    [Fact]
    public void TheInstancedFragmentShaderWritesTheMotionAttachment()
    {
        string fragment = Read("sources/shaders/instanced.fsh");

        Assert.Contains("#if TAAMOTION > 0", fragment);
        Assert.Contains("in vec4 taaPrevClip;", fragment);
        Assert.Contains("in float taaInstanceReactive;", fragment);
        Assert.Contains("layout(location = TAAMOTIONLOCATION) out vec4 outMotion;", fragment);

        // The contract taa-resolve.fsh consumes.
        Assert.Contains("vec2 prevPixel = (taaPrevClip.xy / taaPrevClip.w * 0.5 + 0.5) * taaRenderSize;", fragment);
        Assert.Contains("vec2 currentPixel = gl_FragCoord.xy - taaJitterPx;", fragment);
        Assert.Contains("return vec4(prevPixel - currentPixel, reactive, gl_FragCoord.z);", fragment);
        Assert.Contains("if (taaPrevClip.w <= 1e-6) return vec4(0.0);", fragment);
        Assert.Contains("outMotion = taaMotionVector(taaInstanceReactive);", fragment);

        Assert.True(DeclaresUniform(fragment, "taaRenderSize"));
        Assert.True(DeclaresUniform(fragment, "taaJitterPx"));
    }

    // ------------------------------------------------- the per-instance history

    [Fact]
    public void TheFrameContractKeepsPerInstanceHistoryKeyedOnTheDevice()
    {
        string frame = Read("sources/VintagestoryApi/Client/Render/OptimumTemporalFrame.cs");
        string vertex = Read("sources/shaders/instanced.vsh");

        Assert.Contains("public static class OptimumInstanceMotion", frame);

        // Keyed on the buffer and then the device object, never on the slot: the
        // instance buffer is rebuilt from a dictionary whose order changes as
        // blocks are placed and broken.
        Assert.Contains("ConditionalWeakTable<float[], ConditionalWeakTable<object, Entry>>", frame);

        string write = BodyOf(frame,
            "public static void WriteInstance(float[] values, int index, Vec4f lightRgba, float[] transform)");
        Assert.Contains("Entry entry = entries.GetValue(device, _ => new Entry());", write);
        Assert.Contains("if (entry.CapturedFrame != frame.FrameIndex)", write);
        Assert.Contains("!frame.Reset &&", write);
        Assert.Contains("entry.PreviousFrame == frame.FrameIndex - 1 &&", write);
        Assert.Contains("entry.PrevView == view &&", write);
        Assert.Contains("frame.WasViewCaptured(view)", write);

        string pass = BodyOf(frame, "public static void ApplyPassUniforms(IShaderProgram program)");
        foreach (string uniform in new[] { "prevProjectionMatrix", "prevModelViewMatrix" })
        {
            Assert.Contains("program.UniformMatrix(\"" + uniform + "\"", pass);
            Assert.True(DeclaresUniform(vertex, uniform), uniform + " is set but declared by no shader");
        }
        // The hand FOV is a different view with a different previous projection.
        Assert.Contains("EnumTemporalView view = frame.ActiveView;", pass);
        Assert.Contains("frame.GetPrevProjection(view)", pass);
        // The shared warp/jitter/render-size block comes from the one helper the
        // other three writers use, not from a second copy of it.
        Assert.Contains("frame.ApplyMotionUniforms(program);", pass);
    }

    // ------------------------------------------------ the instrumented renderers

    /// <summary>
    /// Every mechanical-power renderer allocates its instance buffer in the shared
    /// layout and writes its transforms through the shared writer, because the
    /// layout is a contract with instanced.vsh: a renderer that kept vanilla's
    /// 20-float stride would feed the shader another instance's matrix.
    /// </summary>
    [Theory]
    [InlineData("GenericMechBlockRenderer")]
    [InlineData("AngledCageGearRenderer")]
    [InlineData("AngledGearBlockRenderer")]
    [InlineData("TransmissionBlockRenderer")]
    [InlineData("ClutchBlockRenderer")]
    [InlineData("CreativeRotorRenderer")]
    [InlineData("PulverizerRenderer")]
    public void EveryMechanicalRendererUsesTheSharedInstanceLayout(string renderer)
    {
        string source = ReadPatchedOrSource(
            "patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/" + renderer + ".cs.patch",
            "VSSurvivalMod/Systems/MechanicalPower/Renderer/" + renderer + ".cs");

        Assert.Contains("OptimumInstanceMotion.CreateInstanceFloats(", source);
        Assert.Contains("OptimumInstanceMotion.InstanceFloats", source);
        // Vanilla's hand-rolled layout and instance stride must be gone, or the
        // buffer and the shader disagree about where an instance starts.
        Assert.DoesNotContain("InterleaveStride = 16 + 4 * 16", source);
        Assert.DoesNotContain("* 20;", source);
    }

    [Fact]
    public void TheInstanceWriterAndTheDeviceItBelongsToAreNamedTogether()
    {
        string baseRenderer = ReadPatchedOrSource(
            "patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/MechBlockRenderer.cs.patch",
            "VSSurvivalMod/Systems/MechanicalPower/Renderer/MechBlockRenderer.cs");

        // The device is named before its transforms are written, so the history
        // below matches the same gear rather than the same buffer slot.
        Assert.Contains("OptimumInstanceMotion.NoteDevice(dev);", baseRenderer);
        Assert.Contains("OptimumInstanceMotion.WriteInstance(values, index, lightRgba, tmpMat);", baseRenderer);

        // The sub-mesh renderers that write their own transforms do the same.
        foreach (string renderer in new[] { "ClutchBlockRenderer", "CreativeRotorRenderer", "PulverizerRenderer" })
        {
            string source = ReadPatchedOrSource(
                "patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/" + renderer + ".cs.patch",
                "VSSurvivalMod/Systems/MechanicalPower/Renderer/" + renderer + ".cs");
            Assert.Contains("OptimumInstanceMotion.WriteInstance(values, index, lightRgba, tmpMat);", source);
        }
    }

    [Fact]
    public void TheInstancedPassSetsItsUniformsAndOpensTheMotionWindow()
    {
        string renderer = ReadPatchedOrSource(
            "patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/MechNetworkRenderer.cs.patch",
            "VSSurvivalMod/Systems/MechanicalPower/Renderer/MechNetworkRenderer.cs");

        Assert.Contains("OptimumInstanceMotion.ApplyPassUniforms(prog);", renderer);
        Assert.Contains("bool optimumMotionWrite = OptimumMotionWrite.Begin();", renderer);
        Assert.Contains("if (optimumMotionWrite) OptimumMotionWrite.End();", renderer);
    }

    // --------------------------------------------------------------- the ship

    [Fact]
    public void ModPatcherManifestsCarryTheChangedMechanicalRenderers()
    {
        string manifest = Read("Optimum.Patcher/mod-patcher.cs");

        foreach (string entry in new[]
        {
            "new(\"Vintagestory.GameContent.Mechanics.MechNetworkRenderer\", \"OnRenderFrame\", 2)",
            "new(\"Vintagestory.GameContent.Mechanics.MechBlockRenderer\", \"UpdateCustomFloatBuffer\", 0)",
            "new(\"Vintagestory.GameContent.Mechanics.MechBlockRenderer\", \"UpdateLightAndTransformMatrix\", 7)",
            "new(\"Vintagestory.GameContent.Mechanics.GenericMechBlockRenderer\", \".ctor\", 4)",
            "new(\"Vintagestory.GameContent.Mechanics.GenericMechBlockRenderer\", \"OnRenderFrame\", 2)",
            "new(\"Vintagestory.GameContent.Mechanics.AngledCageGearRenderer\", \".ctor\", 4)",
            "new(\"Vintagestory.GameContent.Mechanics.AngledGearsBlockRenderer\", \".ctor\", 4)",
            "new(\"Vintagestory.GameContent.Mechanics.TransmissionBlockRenderer\", \".ctor\", 4)",
            "new(\"Vintagestory.GameContent.Mechanics.ClutchBlockRenderer\", \"UpdateLightAndTransformMatrix\", 9)",
            "new(\"Vintagestory.GameContent.Mechanics.CreativeRotorRenderer\", \"UpdateLightAndTransformMatrix\", 8)",
            "new(\"Vintagestory.GameContent.Mechanics.PulverizerRenderer\", \"UpdateLightAndTransformMatrix\", 8)",
        })
        {
            Assert.Contains(entry, manifest);
        }
    }

    /// <summary>
    /// An external mod that ships its own instanced shader would not have the
    /// writer, so TAA has to switch itself off rather than reproject gears by
    /// whatever happens to be in the attachment.
    /// </summary>
    [Fact]
    public void TheScannerDisablesTaaForAnExternalInstancedShader()
    {
        string scanner = Read("Optimum.Launcher/ShaderCompatibilityScanner.cs");

        Assert.Contains("HasExternalShader(report, \"instanced.vsh\")", scanner);
        Assert.Contains("HasExternalShader(report, \"instanced.fsh\")", scanner);
        Assert.Contains("AddFeatureDecision(report, \"Taa\"", scanner);
    }

    /// <summary>
    /// The overrides only reach a running client if `make deploy` and every
    /// packager copy sources/shaders - they do already, directory-wide, so this
    /// only guards against a regression that starts naming files.
    /// </summary>
    [Fact]
    public void DeployAndEveryPackagerShipTheInstancedShaderOverrides()
    {
        foreach (string path in new[]
        {
            "Makefile", "scripts/package-linux.sh", "scripts/package-macos.sh", "scripts/package-linux.ps1",
        })
        {
            string text = Read(path);
            Assert.Contains("sources/shaders", text.Replace('\\', '/'));
            Assert.DoesNotContain("instanced.vsh", text);
        }
    }

    // ----------------------------------------------------------------- helpers

    internal static bool DeclaresUniform(string shader, string name)
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

    internal static string BodyOf(string source, string signature)
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

    internal static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string? resolvedPatch = TryFind(patchPath);
        return resolvedPatch != null ? PatchReader.ReadPatchedContent(resolvedPatch) : Read(sourcePath);
    }

    internal static string Read(string relativePath)
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
