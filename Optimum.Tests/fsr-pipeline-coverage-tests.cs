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
        Assert.Contains("!optimumFsrDisabled", platform);
        Assert.Contains("catch (Exception error)", platform);
        Assert.Contains("DisableOptimumFsr(error)", platform);
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

        // Bias must be skipped entirely when nothing asks for one (native res
        // with TAA off, which is the only configuration that made a bias before
        // P5 added TaaMipBias to the same value) so rendering matches vanilla
        // exactly - vanilla never sets these TexParameter/SamplerParameter
        // calls at all.
        Assert.Contains("float textureLodBias = Vintagestory.API.Config.OptimumConfig.EffectiveTerrainLodBias;", chunkRenderer);
        Assert.Contains("if (textureLodBias == 0f)", chunkRenderer);
        // ... but "no call" only holds once the bias has been cleared again. The
        // cache starts at NaN and the zero branch is a RESTORE path: after a
        // nonzero bias it writes 0 back through SetOptimumTextureLodBias (atlas
        // TexParameter and, via ShaderRegistry, the terrain sampler objects)
        // before it returns and resets the cache to NaN. Without that a user who
        // turns TAA off, or leaves FSR, would keep the last bias until the next
        // shader reload.
        Assert.Contains("private float optimumTextureLodBias = float.NaN;", chunkRenderer);
        string zeroBranch = BranchAfter(chunkRenderer, "if (textureLodBias == 0f)");
        Assert.Contains("if (!float.IsNaN(optimumTextureLodBias))", zeroBranch);
        Assert.Contains("SetOptimumTextureLodBias(0f);", zeroBranch);
        Assert.Contains("optimumTextureLodBias = float.NaN;", zeroBranch);
        // ...and the branch really is just that branch: the nonzero path below
        // it is outside it.
        Assert.DoesNotContain("SetOptimumTextureLodBias(textureLodBias)", zeroBranch);
        // The render-scale term itself still is log2 of the clamped scale; it
        // now lives in OptimumConfig so both call sites share it.
        string optimumConfig = Read("VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("bias += MathF.Log2(Math.Clamp(scale, 0.5f, 1.0f));", optimumConfig);
        // The bias reaches every block atlas through SetOptimumTextureLodBias,
        // which routes to the device and keeps the GL call as its fallback. The
        // caller still computes the value; only the application moved.
        Assert.Contains("SetOptimumTextureLodBias(textureLodBias)", chunkRenderer);
        // Phase 1A step 5: applied by the platform virtual SetTextureLodBias.
        Assert.Contains("game.Platform.SetTextureLodBias(textureIds, bias);", chunkRenderer);
        Assert.Contains("(TextureParameterName)34049, bias", VulkanPlatformSource.ReadClientPlatformWindows());
        Assert.Contains("OptimumGlConstants.TextureLodBias, bias", VulkanPlatformSource.Read());
        Assert.Contains("float terrainLodBias = OptimumConfig.EffectiveTerrainLodBias;", shaderRegistry);
        Assert.Contains("if (terrainLodBias != 0f)", shaderRegistry);
        // P5 review: the four SamplerParameter calls moved behind
        // ApplyOptimumTerrainSamplerLodBias so ChunkRenderer can reach them too
        // (a bound sampler object overrides the atlas TexParameter, so a live
        // bias change has to write both). The load still passes the same value
        // and still only when it is non-zero.
        Assert.Contains("ApplyOptimumTerrainSamplerLodBias(terrainLodBias);", shaderRegistry);
        // The sampler entry point itself applies the RAW value: no "!= 0f"
        // short-circuit inside it, or the restore path above would reach the
        // atlas parameter and leave the two sampler objects biased.
        string samplerEntry = MethodBody(shaderRegistry, "public static void ApplyOptimumTerrainSamplerLodBias(float bias)");
        Assert.DoesNotContain("!= 0f", samplerEntry);
        Assert.Equal(4, Count(samplerEntry, "ApplyOptimumSamplerLodBias("));
        Assert.Equal(4, Count(samplerEntry, ", bias);"));
        Assert.Contains("platform.SetSamplerLodBias(sampler, bias);", shaderRegistry);
        Assert.Contains("(SamplerParameterName)34049, bias", VulkanPlatformSource.ReadClientPlatformWindows());
        Assert.Contains("OptimumGlConstants.TextureLodBias, bias", VulkanPlatformSource.Read());
        Assert.Contains("terrainTexLinear", shaderRegistry);
    }

    [Fact]
    public void OptionalFsrCompileFailureKeepsGlobalShaderLoadAlive()
    {
        string shaderRegistry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");

        Assert.Contains("shaderProgram.LoadError |= !compiled;", shaderRegistry);
        Assert.Contains("flag = compiled && flag;", shaderRegistry);
        Assert.Contains("else", shaderRegistry);
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
        Assert.Contains("\"optimumFsrDisabled\"", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"DisableOptimumFsr\", 1", patcher);
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

    /// <summary>
    /// The body of the brace-delimited block that follows <paramref name="header"/>.
    /// </summary>
    private static string BranchAfter(string source, string header)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "branch not found: " + header);
        return Block(source, start + header.Length);
    }

    /// <summary>
    /// The text of one method, signature included, up to its matching brace.
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "method not found: " + signature);
        return signature + Block(source, start + signature.Length);
    }

    private static string Block(string source, int offset)
    {
        int open = source.IndexOf('{', offset);
        Assert.True(open > offset - 1, "no block after offset " + offset);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        Assert.Fail("unbalanced block after offset " + offset);
        return string.Empty;
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
