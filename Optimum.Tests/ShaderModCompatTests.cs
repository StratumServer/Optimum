#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Xunit;

namespace Optimum.Tests;

// Regression guards for external shader mods (Ancestral Bliss Shaders, Coriaender Shaders,
// SheyderMod, and Volumetric Shading) that patch Optimum-managed shaders or transpile
// SystemRenderOITLayers.BeforeOIT.OnRenderFrame while keeping zero runtime overhead when
// those mods are absent.
public sealed class ShaderModCompatTests
{
    [Fact]
    public void CompiledAndShippedBeforeOit_KeepsDrawBuffersEnumAsLocal0AndFirstStloc0()
    {
        string? compiledLibPath = FindRepoFileIfExists(
            "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        if (compiledLibPath != null)
        {
            using var compiled = AssemblyDefinition.ReadAssembly(compiledLibPath);
            AssertBeforeOitLocal0Contract(compiled.MainModule, "compiled VintagestoryLib.dll");
        }

        string? shippedPatchedPath = FindRepoFileIfExists(
            "build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib-patched.dll");
        if (shippedPatchedPath != null)
        {
            using var shipped = AssemblyDefinition.ReadAssembly(shippedPatchedPath);
            AssertBeforeOitLocal0Contract(shipped.MainModule, "VintagestoryLib-patched.dll");
        }
    }

    [Fact]
    public void SsaoShader_PreservesSingleOccurrenceAnchorsAndDepthReconstructContract()
    {
        string src = ReadRepoFile("sources/shaders/ssao.fsh");

        Assert.Equal(1, CountLiteral(src, "uniform sampler2D gPosition;"));
        Assert.Equal(1, CountLiteral(src, "uniform sampler2D gDepth;"));
        Assert.Single(Regex.Matches(
            src,
            @"(?s)const float ditherSize = pow\(64\.0, 2\.0\);\s*float dither = bayer128\(texcoord \* screenSize\);\s*vec3 randomVec = sphereMap\(goldenSpiralN\(ditherSize \+ dither, ditherSize\)\);"));
        Assert.Equal(1, CountLiteral(src, "float occlusion = 0.0;"));
        Assert.Equal(1, CountLiteral(src, "for( int i = 0; i < kernelSize; ++i)"));
        Assert.Equal(1, CountLiteral(src, "sample = fragPos + sample * radius;"));
        Assert.Equal(1, CountLiteral(src, "float rangeCheck = 0;"));
        Assert.Equal(1, CountLiteral(
            src,
            "if (depthDiff >= 0.02 && depthDiff < 0.2 && abs(dot(texture(gNormal, offset.xy).rgb, normal) - 1) > 0.25) {"));
        Assert.Equal(1, CountLiteral(src, "if (depthDiff > 0 && depthDiff < 0.2) {"));
        Assert.Equal(1, CountLiteral(
            src,
            "float occ = clamp(1.0 - min(1, occlusion / kernelSize * distanceFade) * (1-attenuate), 0, 1);"));

        Assert.Contains("optimumOctDecodeNormal", src);
        Assert.Contains("optimumReconstructViewZ", src);
        Assert.Contains("optimumReconstructViewPos", src);
        Assert.Contains("SSAO_DEPTH_RECONSTRUCT", src);
    }

    [Fact]
    public void GodRaysAndFinalShaders_PreserveSingleOccurrenceExternalPatchAnchors()
    {
        string godrays = ReadRepoFile("sources/shaders/godrays.fsh");
        Assert.Equal(1, CountLiteral(godrays, "int samples = int(180 * min(1, intensity * 1.2));"));
        Assert.Equal(1, CountLiteral(godrays, "samples = min(maxGodRaySamples, samples);"));

        string finalFsh = ReadRepoFile("sources/shaders/final.fsh");
        Assert.Equal(1, CountLiteral(finalFsh, "void main(void) {"));
        Assert.Equal(1, CountLiteral(finalFsh, "texture(primaryScene, texCoord)"));
        Assert.Equal(1, CountLiteral(finalFsh, "fxaaTexturePixel(primaryScene, texCoord, invFrameSize)"));
        Assert.Contains("uniform int optimumDisableFxaa = 0;", finalFsh);
        Assert.Contains("optimumReadPrimary(texCoord)", finalFsh);
    }

    [Fact]
    public void SystemRenderOitPatch_RegistersBeforeOitHelpersInPatcher()
    {
        string patcher = ReadRepoFile("Optimum.Patcher/Program.cs");
        Assert.Contains("\"Vintagestory.Client.NoObf.SystemRenderOITLayers/BeforeOIT\"", patcher);
        Assert.Contains("\"optimumSavedShader\"", patcher);
        Assert.Contains("\"optimumNeedsRebuild\"", patcher);
        Assert.Contains("\"OptimumPrepareOitFrame\"", patcher);
        Assert.Contains("\"OptimumRecoverOitFailure\"", patcher);
    }

    private static void AssertBeforeOitLocal0Contract(ModuleDefinition module, string label)
    {
        TypeDefinition? oitType = module.GetType("Vintagestory.Client.NoObf.SystemRenderOITLayers");
        Assert.NotNull(oitType);

        TypeDefinition? beforeOit = oitType!.NestedTypes.SingleOrDefault(t => t.Name == "BeforeOIT");
        Assert.NotNull(beforeOit);

        MethodDefinition? onRenderFrame = beforeOit!.Methods.SingleOrDefault(m =>
            m.Name == "OnRenderFrame" && m.Parameters.Count == 2);
        Assert.NotNull(onRenderFrame);
        Assert.True(onRenderFrame!.HasBody, $"{label}: BeforeOIT.OnRenderFrame must have a method body.");
        Assert.True(onRenderFrame.Body.Variables.Count >= 1, $"{label}: BeforeOIT.OnRenderFrame must have local 0.");

        VariableDefinition local0 = onRenderFrame.Body.Variables[0];
        Assert.Equal("OpenTK.Graphics.OpenGL.DrawBuffersEnum[]", local0.VariableType.FullName);

        var instructions = onRenderFrame.Body.Instructions;
        int stloc0Index = -1;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode == OpCodes.Stloc_0)
            {
                stloc0Index = i;
                break;
            }
        }

        Assert.True(stloc0Index > 0, $"{label}: expected Stloc_0 in BeforeOIT.OnRenderFrame.");

        // Confirm the value stored into local 0 is the DrawBuffersEnum[6] array (initialized via InitializeArray
        // or newarr DrawBuffersEnum), matching vanilla IL expected by Coriaender Shaders's transpiler.
        Instruction prev = instructions[stloc0Index - 1];
        bool initializedDrawBuffersArray =
            (prev.OpCode == OpCodes.Call &&
             prev.Operand is MethodReference mr &&
             mr.Name == "InitializeArray") ||
            (prev.OpCode == OpCodes.Newarr &&
             prev.Operand is TypeReference tr &&
             tr.FullName == "OpenTK.Graphics.OpenGL.DrawBuffersEnum");
        Assert.True(
            initializedDrawBuffersArray,
            $"{label}: instruction before first Stloc_0 was {prev.OpCode} {prev.Operand}, expected DrawBuffersEnum[] init.");

        // Confirm GL.DrawBuffers immediately consumes local 0's length and reference.
        bool callsDrawBuffers = false;
        for (int i = stloc0Index + 1; i < Math.Min(instructions.Count, stloc0Index + 8); i++)
        {
            if (instructions[i].OpCode == OpCodes.Call &&
                instructions[i].Operand is MethodReference callRef &&
                callRef.Name == "DrawBuffers")
            {
                callsDrawBuffers = true;
                break;
            }
        }
        Assert.True(callsDrawBuffers, $"{label}: GL.DrawBuffers must follow Stloc_0.");

        // Verify stack consistency of the patched method via IlStackVerifier.
        var verificationErrors = IlStackVerifier.VerifyMethod(onRenderFrame);
        Assert.Empty(verificationErrors);
    }

    private static int CountLiteral(string text, string value)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }
        return count;
    }

    private static string ReadRepoFile(string relativePath)
    {
        string? fullPath = FindRepoFileIfExists(relativePath);
        Assert.NotNull(fullPath);
        return File.ReadAllText(fullPath!);
    }

    private static string? FindRepoFileIfExists(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
