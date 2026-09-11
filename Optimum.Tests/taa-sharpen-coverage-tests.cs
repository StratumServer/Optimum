using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// TAA-PLAN.md P5: the post-resolve sharpen pass (taa-sharpen) and the TAA mip
/// bias. Covers the pieces the GPU harness cannot see - registration, target
/// lifecycle, the pass's placement in the post chain, the no-double-sharpening
/// rule against FSR 1's RCAS, and the two LOD-bias call sites.
/// </summary>
public class TaaSharpenCoverageTests
{
    // --- the shader pair ----------------------------------------------------

    [Fact]
    public void SharpenShaderPairExistsAndIsAFullscreenTriangle()
    {
        string vertex = Read("sources/shaders/taa-sharpen.vsh");
        Assert.Contains("gl_VertexID", vertex);
        // No vertex inputs: the pass is drawn with RenderFullscreenTriangle,
        // which binds no vertex buffer on the device path.
        Assert.DoesNotContain("in vec", vertex);
    }

    [Fact]
    public void SharpenShaderTakesASharpnessUniformInsteadOfTheBakedRcasConstant()
    {
        string fragment = Read("sources/shaders/taa-sharpen.fsh");
        Assert.Contains("uniform float sharpness;", fragment);
        Assert.Contains("uniform sampler2D inputScene;", fragment);
        Assert.Contains("uniform vec2 inputTexelSize;", fragment);
        // fsr-rcas.fsh bakes the strength in as exp2(-0.2); this one must not.
        Assert.DoesNotContain("lobe *= exp2(-0.2);", fragment);
        Assert.Contains("strength * exp2(", fragment);
    }

    [Fact]
    public void SharpnessZeroIsATrueBypassBeforeAnyFilteringOrClamping()
    {
        string fragment = Read("sources/shaders/taa-sharpen.fsh");

        int bypass = fragment.IndexOf("if (!(sharpness > 0.0))", StringComparison.Ordinal);
        Assert.True(bypass >= 0, "the bypass must be an explicit early-out, not lobe = 0");
        // It returns the centre texel itself, unmodified, and does so before the
        // first ring tap - otherwise "off" would not be bit-for-bit identical.
        int returned = fragment.IndexOf("outColor = center;", bypass, StringComparison.Ordinal);
        Assert.True(returned > bypass);
        int firstTap = fragment.IndexOf("vec3 b = texture(", StringComparison.Ordinal);
        Assert.True(returned < firstTap);
        Assert.True(fragment.IndexOf("return;", returned, StringComparison.Ordinal) > returned);
    }

    [Fact]
    public void SharpenKeepsHdrRangeInsteadOfClampingToOne()
    {
        string fragment = Read("sources/shaders/taa-sharpen.fsh");
        // The resolve writes RGBA16F; fsr-rcas.fsh's clamp(x, 0, 1) would crush
        // every value above 1 that reaches this pass.
        Assert.DoesNotContain("clamp(sharpened, 0.0, 1.0)", fragment);
        Assert.Contains("max(sharpened, vec3(0.0))", fragment);
    }

    // --- registration -------------------------------------------------------

    [Fact]
    public void SharpenProgramIsRegisteredAsAnOptionalOptimumProgram()
    {
        string programs = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderPrograms.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderPrograms.cs");
        Assert.Contains("public static ShaderProgram TaaSharpen;", programs);

        string registry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        Assert.Contains(
            "RegisterOptimumShaderProgram(\"taa-sharpen\", ShaderPrograms.TaaSharpen = new ShaderProgram());",
            registry);
        // Optional exactly like taa-resolve: a failed compile sets LoadError on
        // the program instead of failing the whole shader load.
        Assert.Contains("shaderProgram == ShaderPrograms.TaaSharpen", registry);
    }

    // --- target lifecycle ---------------------------------------------------

    [Fact]
    public void SharpenTargetIsCreatedWithTheHistoryTargetsOnBothPaths()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        Assert.Contains("private const int OptimumTaaSharpenIndex = 21;", platform);
        // Device path (VulkanClientPlatform since Phase 1A step 4): RGBA16F, render resolution.
        string vulkan = VulkanPlatformSource.Read();
        Assert.Contains("private const int OptimumTaaSharpenIndex = 21;", vulkan);
        Assert.Contains(
            "list[OptimumTaaSharpenIndex] = CreateOptimumColorTarget(width, height,",
            vulkan);
        Assert.Contains("EnumTextureInternalFormat.Rgba16f);", vulkan);
        // GL path: the same format token (GL_RGBA16F) through setupAttachment.
        Assert.Contains("setupAttachment(optimumSharpen, num, num2, 0, val, (PixelInternalFormat)34842);", platform);
        // Both live inside the taaRequested block, i.e. they are allocated and
        // released with the history slots (DisposeFrameBuffers walks the list).
        int historyDevice = vulkan.IndexOf("list[OptimumTaaHistoryIndexA] = CreateOptimumHistoryTarget(", StringComparison.Ordinal);
        int sharpenDevice = vulkan.IndexOf("list[OptimumTaaSharpenIndex] = CreateOptimumColorTarget(", StringComparison.Ordinal);
        Assert.True(historyDevice >= 0 && sharpenDevice > historyDevice);
        int historyGl = platform.IndexOf("list[OptimumTaaHistoryIndexA] = CreateOptimumHistoryTargetGl(", StringComparison.Ordinal);
        int sharpenGl = platform.IndexOf("FrameBufferRef optimumSharpen = (list[OptimumTaaSharpenIndex]", StringComparison.Ordinal);
        Assert.True(historyGl >= 0 && sharpenGl > historyGl);
    }

    [Fact]
    public void ASharpenTargetFailureCostsTheSharpeningNotTaa()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // Neither failure path may call DisableOptimumTaa - TAA without the
        // sharpen pass is a working configuration. GL here, device in VulkanClientPlatform.
        string vulkan = VulkanPlatformSource.Read();
        Assert.Equal(1, Count(platform, "Optimum disabled the TAA sharpen pass"));
        Assert.Equal(1, Count(platform, "list[OptimumTaaSharpenIndex] = null;"));
        Assert.Equal(1, Count(vulkan, "Optimum disabled the TAA sharpen pass"));
        Assert.Equal(1, Count(vulkan, "list[OptimumTaaSharpenIndex] = null;"));
    }

    // --- the pass -----------------------------------------------------------

    [Fact]
    public void SharpenRunsRightAfterTheResolveAndBeforeEveryConsumer()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        int resolve = platform.IndexOf("\t\tRenderOptimumTaaResolve();", StringComparison.Ordinal);
        int sharpen = platform.IndexOf("postSceneTexture = RenderOptimumTaaSharpen(postSceneTexture);", StringComparison.Ordinal);
        Assert.True(resolve >= 0);
        int bloom = platform.IndexOf("if (RenderBloom)", resolve, StringComparison.Ordinal);
        Assert.True(sharpen > resolve && bloom > sharpen);
        // Reassigning postSceneTexture once is what gets the sharpened image to
        // bloom (Findbright), god rays and the Luma copy the final composition
        // reads, with no further call sites to keep in step.
        Assert.Contains("findbright.ColorTex2D = postSceneTexture;", platform);
        Assert.Contains("godrays.InputTexture2D = postSceneTexture;", platform);
        Assert.Contains("blit.Scene2D = postSceneTexture;", platform);
    }

    [Fact]
    public void SharpenSkipsWhenThereIsNothingToSharpenAndRestoresRenderState()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        string body = MethodBody(platform, "public override int RenderOptimumTaaSharpen(int resolvedScene)");

        Assert.Contains("if (!TaaResolvedThisFrame || OptimumConfig.TaaSharpness <= 0f)", body);
        Assert.Contains("if (sharpen == null || sharpen.LoadError || target == null)", body);
        // Same set/restore discipline as the resolve (TAA-PLAN "Blend state").
        int blendOff = body.IndexOf("GlToggleBlend(on: false);", StringComparison.Ordinal);
        int depthOff = body.IndexOf("GlDisableDepthTest();", StringComparison.Ordinal);
        int draw = body.IndexOf("RenderFullscreenTriangle(screenQuad);", StringComparison.Ordinal);
        int blendOn = body.IndexOf("GlToggleBlend(on: true);", StringComparison.Ordinal);
        int depthOn = body.IndexOf("GlEnableDepthTest();", StringComparison.Ordinal);
        int primary = body.IndexOf("LoadFrameBuffer(EnumFrameBuffer.Primary);", StringComparison.Ordinal);
        Assert.True(blendOff >= 0 && depthOff > blendOff && draw > depthOff);
        Assert.True(blendOn > draw && depthOn > blendOn && primary > depthOn);
        // The strength reaching the shader is the configured one, clamped.
        Assert.Contains("sharpen.Uniform(\"sharpness\", GameMath.Clamp(OptimumConfig.TaaSharpness, 0f, 1f));", body);
    }

    [Fact]
    public void NoDoubleSharpeningWhenTheFsrRcasBlitIsActive()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        // One shared condition, asked by both passes: the sharpen pass skips
        // itself when the blit is going to run FSR's own RCAS at native
        // resolution, so the same pixels are never sharpened twice.
        Assert.Contains("public override bool OptimumFsrBlitActive()", platform);
        Assert.Contains("bool useFsr = OptimumFsrBlitActive();", platform);

        string body = MethodBody(platform, "public override int RenderOptimumTaaSharpen(int resolvedScene)");
        int guard = body.IndexOf("if (OptimumFsrBlitActive())", StringComparison.Ordinal);
        int draw = body.IndexOf("RenderFullscreenTriangle(screenQuad);", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < draw);
        // And the rule is written down where the next reader will look.
        Assert.Contains("two RCAS passes to the same pixels", platform);

        // The shared test still carries every term the old inline condition had.
        string helper = MethodBody(platform, "public override bool OptimumFsrBlitActive()");
        Assert.Contains("!optimumFsrDisabled", helper);
        Assert.Contains("ClientSettings.OptimumRenderScale < 1.0f", helper);
        Assert.Contains("frameBuffers[OptimumFsrFramebufferIndex] != null", helper);
        Assert.Contains("!fsrEasu.LoadError", helper);
        Assert.Contains("!fsrRcas.LoadError", helper);
    }

    // --- mip bias -----------------------------------------------------------

    [Fact]
    public void TerrainLodBiasIsZeroWithTaaOffAtNativeScale()
    {
        WithConfig(() =>
        {
            OptimumConfig.Taa = false;
            OptimumConfig.RenderScale = 1.0f;
            OptimumConfig.TaaMipBias = -0.5f;
            Assert.Equal(0f, OptimumConfig.EffectiveTerrainLodBias);
        });
    }

    [Fact]
    public void TerrainLodBiasAddsTheMipBiasWhileTaaIsOn()
    {
        WithConfig(() =>
        {
            OptimumConfig.RenderScale = 1.0f;
            OptimumConfig.TaaMipBias = -0.5f;
            OptimumConfig.Taa = true;
            Assert.Equal(-0.5f, OptimumConfig.EffectiveTerrainLodBias, 5);

            // And it adds to the render scale's own bias rather than replacing it.
            OptimumConfig.RenderScale = 0.5f;
            Assert.Equal(-1.5f, OptimumConfig.EffectiveTerrainLodBias, 5);
        });
    }

    [Fact]
    public void TerrainLodBiasKeepsTheRenderScaleTermWhenTaaIsOff()
    {
        WithConfig(() =>
        {
            OptimumConfig.Taa = false;
            OptimumConfig.TaaMipBias = -0.5f;
            OptimumConfig.RenderScale = 0.5f;
            Assert.Equal(-1f, OptimumConfig.EffectiveTerrainLodBias, 5);
        });
    }

    [Fact]
    public void TerrainLodBiasClampsTheConfiguredMipBias()
    {
        WithConfig(() =>
        {
            OptimumConfig.RenderScale = 1.0f;
            OptimumConfig.Taa = true;
            OptimumConfig.TaaMipBias = -9f;
            Assert.Equal(-2f, OptimumConfig.EffectiveTerrainLodBias, 5);
            OptimumConfig.TaaMipBias = 9f;
            Assert.Equal(1f, OptimumConfig.EffectiveTerrainLodBias, 5);
        });
    }

    [Fact]
    public void BothLodBiasCallSitesReadTheSharedValue()
    {
        string chunkRenderer = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");
        Assert.Contains(
            "float textureLodBias = Vintagestory.API.Config.OptimumConfig.EffectiveTerrainLodBias;",
            chunkRenderer);
        // A total of zero still makes no TexParameter call at all, which is what
        // keeps TAA off at native scale identical to vanilla.
        Assert.Contains("if (textureLodBias == 0f)", chunkRenderer);
        // The zero branch is not a plain guard: it restores. optimumTextureLodBias
        // caches the last applied value starting at NaN, so a nonzero -> zero
        // transition (TAA switched off, render scale back to 1.0) writes 0 back
        // through SetOptimumTextureLodBias - which resets the atlas texture
        // parameter AND, through ShaderRegistry.ApplyOptimumTerrainSamplerLodBias,
        // the two terrain sampler objects - before returning.
        Assert.Contains("private float optimumTextureLodBias = float.NaN;", chunkRenderer);
        string zeroBranch = BranchAfter(chunkRenderer, "if (textureLodBias == 0f)");
        Assert.Contains("if (!float.IsNaN(optimumTextureLodBias))", zeroBranch);
        Assert.Contains("SetOptimumTextureLodBias(0f);", zeroBranch);
        Assert.Contains("optimumTextureLodBias = float.NaN;", zeroBranch);
        // ...and the branch really is just that branch: the nonzero path below
        // it is outside it.
        Assert.DoesNotContain("SetOptimumTextureLodBias(textureLodBias)", zeroBranch);
        // Both backends keep getting the same value through the same setter.
        Assert.Contains("optimumDevice.SetTextureParameter(textureIds[k],", chunkRenderer);
        Assert.Contains("GL.TexParameter((TextureTarget)3553, (TextureParameterName)34049, bias);", chunkRenderer);

        string registry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        // The sampler objects override the texture parameter on the units they
        // are bound to, so they must carry the same bias.
        Assert.Contains("float terrainLodBias = OptimumConfig.EffectiveTerrainLodBias;", registry);
        Assert.Contains("if (terrainLodBias != 0f)", registry);
        // The load-time call skips zero (vanilla makes no such call), but the
        // shared entry point applies whatever it is handed - the restore above
        // hands it 0f and must reach the samplers.
        string samplerEntry = BranchAfter(registry, "public static void ApplyOptimumTerrainSamplerLodBias(float bias)");
        Assert.DoesNotContain("!= 0f", samplerEntry);
        Assert.Equal(4, Count(samplerEntry, ", bias);"));
    }

    /// <summary>
    /// P5 review: the mip-bias row claims to apply live, and for the two programs
    /// the setting exists for it did not. chunkopaque and chunktopsoil sample the
    /// atlas through sampler OBJECTS, and a bound sampler object overrides the
    /// texture object's parameters on that unit - LOD bias included. So
    /// ChunkRenderer's per-frame TexParameter moved the mip selection of liquid,
    /// transparent and shadow terrain while the two opaque passes kept whatever
    /// bias the last shader load compiled in. Both halves now move together.
    /// </summary>
    [Fact]
    public void ALiveMipBiasChangeReachesTheTerrainSamplerObjectsAsWell()
    {
        string registry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        // The sampler write is a reusable entry point, not inlined into the load.
        Assert.Contains("public static void ApplyOptimumTerrainSamplerLodBias(float bias)", registry);
        Assert.Contains("ApplyOptimumTerrainSamplerLodBias(terrainLodBias);", registry);
        // Both backends, through the same per-sampler helper.
        Assert.Contains(
            "optimumDevice.SetSamplerParameter(sampler, OptimumGlConstants.TextureLodBias, bias);",
            registry);
        Assert.Contains("GL.SamplerParameter(sampler, (SamplerParameterName)34049, bias);", registry);
        // Callable before the samplers exist: ChunkRenderer runs a frame before
        // the first shader load has created them.
        Assert.Contains("program == null || !program.customSamplers.TryGetValue(samplerName, out var sampler)", registry);

        string chunkRenderer = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");
        string setter = MethodBody(chunkRenderer, "private void SetOptimumTextureLodBias(float bias)");
        Assert.Contains("ShaderRegistry.ApplyOptimumTerrainSamplerLodBias(bias);", setter);
        // Before the device/GL split, so both paths reach it.
        Assert.True(
            setter.IndexOf("ShaderRegistry.ApplyOptimumTerrainSamplerLodBias(bias);", StringComparison.Ordinal)
            < setter.IndexOf("if (optimumDevice != null)", StringComparison.Ordinal));

        // And the Cecil transplant carries both new members.
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"ApplyOptimumTerrainSamplerLodBias\"", patcher);
        Assert.Contains("\"ApplyOptimumSamplerLodBias\"", patcher);
    }

    /// <summary>
    /// P5 review: with a 0f initialiser the very first OnBeforeRenderOpaque of a
    /// TAA-off, native-scale session sees "0 wanted, not-NaN cached" and writes an
    /// explicit LOD bias of 0 over the driver default on every atlas - and, since
    /// the fix above, on every terrain sampler too. NaN is what "Optimum has never
    /// touched this" has to mean for that configuration to make no call at all.
    /// </summary>
    [Fact]
    public void TheCachedLodBiasStartsAtNanSoTaaOffTouchesNothing()
    {
        string chunkRenderer = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");
        Assert.Contains("private float optimumTextureLodBias = float.NaN;", chunkRenderer);
        Assert.DoesNotContain("private float optimumTextureLodBias;", chunkRenderer);
    }

    // --- manifests and scanner ---------------------------------------------

    [Fact]
    public void CecilPatcherShipsTheSharpenMembers()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"OptimumTaaSharpenIndex\"", patcher);
        Assert.Contains("\"OptimumFsrBlitActive\"", patcher);
        Assert.Contains("\"RenderOptimumTaaSharpen\"", patcher);
        Assert.Contains("\"TaaSharpen\"", patcher);
        // The bodies the calls live in are transplanted.
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"RenderPostprocessingEffects\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"BlitPrimaryToDefault\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"SetupDefaultFrameBuffers\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"loadRegisteredShaderPrograms\", 0", patcher);
        Assert.Contains("\"ApplyOptimumTextureLodBias\"", patcher);
    }

    [Fact]
    public void ScannerVetoesTaaWhenAModOwnsOneOfItsOwnPasses()
    {
        string scanner = Read("Optimum.Launcher/ShaderCompatibilityScanner.cs");
        Assert.Contains("HasExternalShader(report, \"taa-sharpen.vsh\")", scanner);
        Assert.Contains("HasExternalShader(report, \"taa-sharpen.fsh\")", scanner);
        // The resolve and the debug view were missing from the same list.
        Assert.Contains("HasExternalShader(report, \"taa-resolve.fsh\")", scanner);
        Assert.Contains("HasExternalShader(report, \"taa-debug.fsh\")", scanner);
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>
    /// The text from a method's signature to the start of the next member
    /// declaration at the same indentation ("\n\t}" followed by a blank line).
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "method not found: " + signature);
        int end = source.IndexOf("\n\t}\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "method end not found: " + signature);
        return source.Substring(start, end - start);
    }

    /// <summary>
    /// The brace-delimited block that follows <paramref name="header"/>, matched
    /// by brace depth so a nested block cannot end it early.
    /// </summary>
    private static string BranchAfter(string source, string header)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "not found: " + header);
        int open = source.IndexOf('{', start + header.Length);
        Assert.True(open > start, "no block after: " + header);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        Assert.Fail("unbalanced block after: " + header);
        return string.Empty;
    }

    private static void WithConfig(Action body)
    {
        bool taa = OptimumConfig.Taa;
        float scale = OptimumConfig.RenderScale;
        float mip = OptimumConfig.TaaMipBias;
        try
        {
            body();
        }
        finally
        {
            OptimumConfig.Taa = taa;
            OptimumConfig.RenderScale = scale;
            OptimumConfig.TaaMipBias = mip;
        }
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
