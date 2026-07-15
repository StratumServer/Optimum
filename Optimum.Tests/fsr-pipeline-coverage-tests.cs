using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class FsrPipelineCoverageTests
{
    [Fact]
    public void FinalShaderKeepsFsrOutOfReducedResolutionComposition()
    {
        string finalShader = Read("sources/shaders/final.fsh");

        Assert.DoesNotContain("OPTIMUMFSR", finalShader);
        Assert.DoesNotContain("FsrEasu", finalShader);
        Assert.Contains("#if FXAA == 1", finalShader);
    }

    [Fact]
    public void EasuShaderUsesTwelveTapReconstruction()
    {
        string easu = Read("sources/shaders/fsr-easu.fsh");

        Assert.Equal(13, Count(easu, "FsrEasuTap("));
        Assert.Contains("return color.g + 0.5 * (color.r + color.b);", easu);
        Assert.Contains("clamp(result, minimumColor, maximumColor)", easu);
    }

    [Fact]
    public void RcasShaderUsesNativeFiveTapCrossAndFixedSharpness()
    {
        string rcas = Read("sources/shaders/fsr-rcas.fsh");

        Assert.Contains("vec3 b = texture", rcas);
        Assert.Contains("vec3 d = texture", rcas);
        Assert.Contains("vec3 e = texture", rcas);
        Assert.Contains("vec3 f = texture", rcas);
        Assert.Contains("vec3 h = texture", rcas);
        Assert.Contains("lobe *= exp2(-0.2);", rcas);
        Assert.Contains("lobe * (b + d + f + h) + e", rcas);
    }

    [Fact]
    public void BlitRunsEasuBeforeRcasAndKeepsVanillaFallback()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        int easuUse = platform.IndexOf("fsrEasu.Use();", StringComparison.Ordinal);
        int defaultBind = platform.IndexOf("LoadFrameBuffer(EnumFrameBuffer.Default);", easuUse, StringComparison.Ordinal);
        int rcasUse = platform.IndexOf("fsrRcas.Use();", easuUse, StringComparison.Ordinal);

        Assert.True(easuUse >= 0);
        Assert.True(defaultBind > easuUse);
        Assert.True(rcasUse > defaultBind);
        Assert.Contains("OptimumFsrFramebufferIndex = 18", platform);
        Assert.Contains("ShaderProgramBlit blit = ShaderPrograms.Blit;", platform);
        Assert.Contains("&& !fsrEasu.LoadError", platform);
        Assert.Contains("&& !fsrRcas.LoadError", platform);
    }

    [Fact]
    public void TerrainBiasCoversTextureObjectsAndCustomSamplers()
    {
        string chunkRenderer = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");
        string shaderRegistry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");

        Assert.Contains("MathF.Log2(Math.Clamp(ClientSettings.OptimumRenderScale, 0.5f, 1.0f))", chunkRenderer);
        Assert.Contains("(TextureParameterName)34049, textureLodBias", chunkRenderer);
        Assert.Contains("(SamplerParameterName)34049, terrainLodBias", shaderRegistry);
        Assert.Contains("terrainTexLinear", shaderRegistry);
    }

    [Fact]
    public void OptionalFsrCompileFailureKeepsGlobalShaderLoadAlive()
    {
        string shaderRegistry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");

        Assert.Contains("shaderProgram.LoadError |= !compiled;", shaderRegistry);
        Assert.Contains("else\n\t\t\t\t{\n\t\t\t\t\tflag = compiled && flag;", shaderRegistry.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void CecilPatcherShipsEveryFsrMethodAndMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"SetupDefaultFrameBuffers\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"BlitPrimaryToDefault\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"OnBeforeRenderOpaque\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"loadRegisteredShaderPrograms\", 0", patcher);
        Assert.Contains("\"RegisterOptimumShaderProgram\"", patcher);
        Assert.Contains("\"FsrEasu\"", patcher);
        Assert.Contains("\"FsrRcas\"", patcher);
    }

    [Theory]
    [InlineData(1.0f, 0.0f)]
    [InlineData(0.85f, -0.234f)]
    [InlineData(0.77f, -0.377f)]
    [InlineData(0.67f, -0.578f)]
    public void MipBiasMatchesRenderScale(float scale, float expected)
    {
        Assert.InRange(MathF.Log2(scale), expected - 0.001f, expected + 0.001f);
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
    }
}
