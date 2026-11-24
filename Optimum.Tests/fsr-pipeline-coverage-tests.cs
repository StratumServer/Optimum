using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
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
    public void RcasShaderUsesNativeFiveTapCrossAndConfigurableSharpness()
    {
        string rcas = Read("sources/shaders/fsr-rcas.fsh");

        Assert.Contains("vec3 b = texture", rcas);
        Assert.Contains("vec3 d = texture", rcas);
        Assert.Contains("vec3 e = texture", rcas);
        Assert.Contains("vec3 f = texture", rcas);
        Assert.Contains("vec3 h = texture", rcas);
        Assert.Contains("uniform float sharpness;", rcas);
        Assert.Contains("lobe *= exp2(-sharpness);", rcas);
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
    public void DisableOptimumFsrRestoresSsaaAndRebuildsFramebuffers()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        Assert.Contains("DisableOptimumFsr(Exception error)", platform);
        Assert.Contains("ClientSettings.SSAA = 1f;", platform);
        Assert.Contains("RebuildFrameBuffers();", platform);
        Assert.Contains("optimumFsrDisabled = true;", platform);
        Assert.Contains("logger.Warning(", platform);
        Assert.Contains("!optimumFsrDisabled && ClientSettings.OptimumRenderScale < 1.0f", platform);
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

        // Bias must be skipped entirely at native res (RenderScale >= 1.0) so
        // rendering matches vanilla exactly - vanilla never sets these
        // TexParameter/SamplerParameter calls at all.
        Assert.Contains("if (ClientSettings.OptimumRenderScale >= 1.0f)", chunkRenderer);
        Assert.Contains("MathF.Log2(Math.Clamp(Vintagestory.API.Config.OptimumConfig.EffectiveRenderScale, 0.5f, 1.0f))", chunkRenderer);
        Assert.Contains("(TextureParameterName)34049, textureLodBias", chunkRenderer);
        Assert.Contains("if (OptimumConfig.EffectiveRenderScale < 1.0f)", shaderRegistry);
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
        Assert.Contains("flag = compiled && flag;", shaderRegistry);
        Assert.Contains("else", shaderRegistry);
    }
    [Fact]
    public void CecilPatcherShipsEveryFsrMethodAndMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"SetupDefaultFrameBuffers\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"BlitPrimaryToDefault\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"RenderFinalComposition\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"OnBeforeRenderOpaque\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"loadRegisteredShaderPrograms\", 0", patcher);
        Assert.Contains("\"RegisterOptimumShaderProgram\"", patcher);
        Assert.Contains("\"FsrEasu\"", patcher);
        Assert.Contains("\"FsrRcas\"", patcher);
        Assert.Contains("\"optimumFsrDisabled\"", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"DisableOptimumFsr\", 1", patcher);
        Assert.Contains("\"OptimumFsrSharpness\"", patcher);
        Assert.Contains("\"onOptimumFsrSharpnessChanged\"", patcher);
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

    [Fact]
    public void BlitPrimaryToDefaultCachesFsrUniformTexelSize()
    {
        string platform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");

        Assert.DoesNotContain("_optimumLastFsr", platform);
        Assert.Contains("optimumFinalLastProgram", platform);
        Assert.Contains("optimumLastFsrEasuProgram", platform);
        Assert.Contains("optimumLastFsrEasuProgramId", platform);
        Assert.Contains("optimumLastFsrInputWidth", platform);
        Assert.Contains("optimumLastFsrInputHeight", platform);
        Assert.Contains("optimumLastFsrRcasProgram", platform);
        Assert.Contains("optimumLastFsrRcasProgramId", platform);
        Assert.Contains("optimumLastFsrOutputWidth", platform);
        Assert.Contains("optimumLastFsrOutputHeight", platform);
        Assert.Contains("optimumLastFsrSharpness", platform);
        Assert.Contains("bool easuProgramChanged = !ReferenceEquals(fsrEasu, optimumLastFsrEasuProgram) || fsrEasu.ProgramId != optimumLastFsrEasuProgramId;", platform);
        Assert.Contains("if (easuProgramChanged || inputWidth != optimumLastFsrInputWidth || inputHeight != optimumLastFsrInputHeight)", platform);
        Assert.Contains("bool rcasProgramChanged = !ReferenceEquals(fsrRcas, optimumLastFsrRcasProgram) || fsrRcas.ProgramId != optimumLastFsrRcasProgramId;", platform);
        Assert.Contains("if (rcasProgramChanged || outputWidth != optimumLastFsrOutputWidth || outputHeight != optimumLastFsrOutputHeight)", platform);
        Assert.Contains("if (rcasProgramChanged || sharpness != optimumLastFsrSharpness)", platform);
        Assert.Contains("fsrRcas.Uniform(\"sharpness\", sharpness);", platform);
        Assert.Contains("if (!ReferenceEquals(final, optimumFinalLastProgram) || final.ProgramId != optimumFinalLastProgramId)", platform);

        string gui = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs");
        Assert.DoesNotContain("ClientSettings.SSAA = scale;", gui);

        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.DoesNotContain("_optimumLastFsr", patcher);
        Assert.Contains("\"optimumFinalLastProgram\"", patcher);
        Assert.Contains("\"optimumLastFsrEasuProgram\"", patcher);
        Assert.Contains("\"optimumLastFsrEasuProgramId\"", patcher);
        Assert.Contains("\"optimumLastFsrInputWidth\"", patcher);
        Assert.Contains("\"optimumLastFsrInputHeight\"", patcher);
        Assert.Contains("\"optimumLastFsrRcasProgram\"", patcher);
        Assert.Contains("\"optimumLastFsrRcasProgramId\"", patcher);
        Assert.Contains("\"optimumLastFsrOutputWidth\"", patcher);
        Assert.Contains("\"optimumLastFsrOutputHeight\"", patcher);
        Assert.Contains("\"optimumLastFsrSharpness\"", patcher);

        string allowlist = Read("Optimum.Patcher/lib-static-initializer-allowlist.cs");
        Assert.DoesNotContain("_optimumLastFsr", allowlist);
    }

    [Fact]
    public void PatchedVintagestoryLib_ShipsPerProgramFsrUniformCacheFieldsAndChecks()
    {
        string? tempPatchedPath = null;
        try
        {
            string patchedPath = ResolveOrBuildPatchedVintagestoryLibPath(out tempPatchedPath);
            using var asm = AssemblyDefinition.ReadAssembly(patchedPath);
            var platformType = asm.MainModule.GetType("Vintagestory.Client.NoObf.ClientPlatformWindows")!;

            Assert.Null(platformType.Fields.SingleOrDefault(f => f.Name.StartsWith("_optimumLastFsr", StringComparison.Ordinal)));

            string[] expectedFields =
            {
                "optimumFinalLastProgram",
                "optimumLastFsrEasuProgram",
                "optimumLastFsrEasuProgramId",
                "optimumLastFsrInputWidth",
                "optimumLastFsrInputHeight",
                "optimumLastFsrRcasProgram",
                "optimumLastFsrRcasProgramId",
                "optimumLastFsrOutputWidth",
                "optimumLastFsrOutputHeight",
                "optimumLastFsrSharpness",
            };
            foreach (string fieldName in expectedFields)
            {
                var field = platformType.Fields.SingleOrDefault(f => f.Name == fieldName);
                Assert.NotNull(field);
                Assert.False(field!.IsStatic);
            }

            var blit = platformType.Methods.Single(m => m.Name == "BlitPrimaryToDefault");
            AssertReferencesField(blit, "optimumLastFsrEasuProgram");
            AssertReferencesField(blit, "optimumLastFsrEasuProgramId");
            AssertReferencesField(blit, "optimumLastFsrInputWidth");
            AssertReferencesField(blit, "optimumLastFsrInputHeight");
            AssertReferencesField(blit, "optimumLastFsrRcasProgram");
            AssertReferencesField(blit, "optimumLastFsrRcasProgramId");
            AssertReferencesField(blit, "optimumLastFsrOutputWidth");
            AssertReferencesField(blit, "optimumLastFsrOutputHeight");
            AssertReferencesField(blit, "optimumLastFsrSharpness");
            AssertReferencesField(blit, "ProgramId");

            var finalComp = platformType.Methods.Single(m => m.Name == "RenderFinalComposition");
            AssertReferencesField(finalComp, "optimumFinalLastProgram");
            AssertReferencesField(finalComp, "optimumFinalLastProgramId");
            AssertReferencesField(finalComp, "ProgramId");
        }
        finally
        {
            if (tempPatchedPath != null && File.Exists(tempPatchedPath))
            {
                try { File.Delete(tempPatchedPath); } catch { }
            }
        }
    }

    [GlContextFact]
    public void RealGlContext_FsrReloadAndSameProgramIdReuseReuploadsTexelSizeAndSharpness()
    {
        GLFWProvider.CheckForMainThread = false;
        using var window = new NativeWindow(new NativeWindowSettings
        {
            StartVisible = false,
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,
            ClientSize = new Vector2i(64, 64),
        });
        window.MakeCurrent();
        while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }

        string shadersDir = Path.GetDirectoryName(PatchReader.FindRepositoryFile(Path.Combine("sources", "shaders", "final.fsh")))!;
        string easuVert = File.ReadAllText(Path.Combine(shadersDir, "fsr-easu.vsh"));
        string easuFrag = File.ReadAllText(Path.Combine(shadersDir, "fsr-easu.fsh"));
        string rcasVert = File.ReadAllText(Path.Combine(shadersDir, "fsr-rcas.vsh"));
        string rcasFrag = File.ReadAllText(Path.Combine(shadersDir, "fsr-rcas.fsh"));

        const int inputW = 32;
        const int inputH = 32;
        const int outputW = 64;
        const int outputH = 64;

        byte[] inputPixels = new byte[inputW * inputH * 4];
        for (int y = 0; y < inputH; y++)
        {
            for (int x = 0; x < inputW; x++)
            {
                int idx = (y * inputW + x) * 4;
                bool checker = ((x >> 2) + (y >> 2)) % 2 == 0;
                inputPixels[idx + 0] = checker ? (byte)20 : (byte)220;
                inputPixels[idx + 1] = checker ? (byte)180 : (byte)40;
                inputPixels[idx + 2] = checker ? (byte)240 : (byte)30;
                inputPixels[idx + 3] = 255;
            }
        }

        int inputTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, inputTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, inputW, inputH, 0, PixelFormat.Rgba, PixelType.UnsignedByte, inputPixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        int midFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, midFbo);
        int midTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, midTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, outputW, outputH, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, midTex, 0);

        int vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);

        int easuProg = CreateProgram(easuVert, easuFrag);
        int rcasProg = CreateProgram(rcasVert, rcasFrag);
        object easuToken = new object();
        object rcasToken = new object();

        object? cachedEasuToken = null;
        int cachedEasuId = 0;
        int cachedInW = 0;
        int cachedInH = 0;
        object? cachedRcasToken = null;
        int cachedRcasId = 0;
        int cachedOutW = 0;
        int cachedOutH = 0;
        float cachedSharpness = 0f;

        int uploadCount = 0;

        byte[] RenderFrame(int curEasuId, object curEasuToken, int curRcasId, object curRcasToken, float sharpness, bool invalidateOnProgramChange)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, midFbo);
            GL.Viewport(0, 0, outputW, outputH);
            GL.UseProgram(curEasuId);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, inputTex);
            GL.Uniform1(GL.GetUniformLocation(curEasuId, "inputScene"), 0);
            bool easuChanged = invalidateOnProgramChange && (!ReferenceEquals(curEasuToken, cachedEasuToken) || curEasuId != cachedEasuId);
            if (easuChanged || inputW != cachedInW || inputH != cachedInH)
            {
                GL.Uniform2(GL.GetUniformLocation(curEasuId, "inputTexelSize"), 1f / inputW, 1f / inputH);
                cachedEasuToken = curEasuToken;
                cachedEasuId = curEasuId;
                cachedInW = inputW;
                cachedInH = inputH;
                uploadCount++;
            }
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, outputW, outputH);
            GL.UseProgram(curRcasId);
            GL.BindTexture(TextureTarget.Texture2D, midTex);
            GL.Uniform1(GL.GetUniformLocation(curRcasId, "inputScene"), 0);
            bool rcasChanged = invalidateOnProgramChange && (!ReferenceEquals(curRcasToken, cachedRcasToken) || curRcasId != cachedRcasId);
            if (rcasChanged || outputW != cachedOutW || outputH != cachedOutH)
            {
                GL.Uniform2(GL.GetUniformLocation(curRcasId, "inputTexelSize"), 1f / outputW, 1f / outputH);
                cachedOutW = outputW;
                cachedOutH = outputH;
                uploadCount++;
            }
            if (rcasChanged || sharpness != cachedSharpness)
            {
                GL.Uniform1(GL.GetUniformLocation(curRcasId, "sharpness"), sharpness);
                cachedSharpness = sharpness;
                uploadCount++;
            }
            if (rcasChanged)
            {
                cachedRcasToken = curRcasToken;
                cachedRcasId = curRcasId;
            }
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            byte[] outPixels = new byte[outputW * outputH * 4];
            GL.ReadPixels(0, 0, outputW, outputH, PixelFormat.Rgba, PixelType.UnsignedByte, outPixels);
            return outPixels;
        }

        try
        {
            // Frame 1 (loading screen): initial upload populates caches
            byte[] initialOut = RenderFrame(easuProg, easuToken, rcasProg, rcasToken, 0.2f, invalidateOnProgramChange: true);
            Assert.Equal(3, uploadCount);
            Assert.True(CountDistinctColors(initialOut) > 4);

            // Frame 2 (steady state): zero redundant uniform uploads
            uploadCount = 0;
            byte[] steadyOut = RenderFrame(easuProg, easuToken, rcasProg, rcasToken, 0.2f, invalidateOnProgramChange: true);
            Assert.Equal(0, uploadCount);
            Assert.Equal(initialOut, steadyOut);

            // Reload shaders (same dimensions): simulate old bug without program invalidation -> flat frame
            GL.DeleteProgram(easuProg);
            GL.DeleteProgram(rcasProg);
            easuProg = CreateProgram(easuVert, easuFrag);
            rcasProg = CreateProgram(rcasVert, rcasFrag);
            easuToken = new object();
            rcasToken = new object();

            uploadCount = 0;
            byte[] brokenReloadOut = RenderFrame(easuProg, easuToken, rcasProg, rcasToken, 0.2f, invalidateOnProgramChange: false);
            Assert.Equal(0, uploadCount);
            Assert.Equal(1, CountDistinctColors(brokenReloadOut));

            // With program invalidation (even if OpenGL reused identical numeric ProgramIds after DeleteProgram)
            int reusedStyleEasuId = easuProg;
            int reusedStyleRcasId = rcasProg;
            cachedEasuId = reusedStyleEasuId;
            cachedRcasId = reusedStyleRcasId;
            // Different ShaderProgram reference catches reused GL handle; identical object with new GL handle catches ProgramId change
            byte[] fixedReloadOut = RenderFrame(easuProg, easuToken, rcasProg, rcasToken, 0.2f, invalidateOnProgramChange: true);
            Assert.Equal(3, uploadCount);
            Assert.True(CountDistinctColors(fixedReloadOut) > 4);
            Assert.Equal(initialOut, fixedReloadOut);

            // Same ShaderProgram instance receiving a brand-new ProgramId also invalidates cleanly
            GL.DeleteProgram(easuProg);
            GL.DeleteProgram(rcasProg);
            easuProg = CreateProgram(easuVert, easuFrag);
            rcasProg = CreateProgram(rcasVert, rcasFrag);
            uploadCount = 0;
            cachedEasuId = reusedStyleEasuId - 1;
            cachedRcasId = reusedStyleRcasId - 1;
            byte[] recompiledSameWrapperOut = RenderFrame(easuProg, easuToken, rcasProg, rcasToken, 0.2f, invalidateOnProgramChange: true);
            Assert.Equal(3, uploadCount);
            Assert.Equal(initialOut, recompiledSameWrapperOut);

            // Sharpness update triggers single uniform upload and applies cleanly
            uploadCount = 0;
            byte[] sharpOut = RenderFrame(easuProg, easuToken, rcasProg, rcasToken, 0.9f, invalidateOnProgramChange: true);
            Assert.Equal(1, uploadCount);
            Assert.True(CountDistinctColors(sharpOut) > 4);
            Assert.NotEqual(initialOut, sharpOut);

            Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            GL.BindVertexArray(0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteVertexArray(vao);
            GL.DeleteFramebuffer(midFbo);
            GL.DeleteTexture(midTex);
            GL.DeleteTexture(inputTex);
            GL.DeleteProgram(easuProg);
            GL.DeleteProgram(rcasProg);
        }
    }

    private static int CountDistinctColors(byte[] rgba)
    {
        var colors = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < rgba.Length; i += 4)
        {
            colors.Add((rgba[i] << 16) | (rgba[i + 1] << 8) | rgba[i + 2]);
        }
        return colors.Count;
    }

    private static string ResolveOrBuildPatchedVintagestoryLibPath(out string? tempPatchedPath)
    {
        tempPatchedPath = null;
        string buildLibDir = Path.GetDirectoryName(PatchReader.FindRepositoryFile("build/VintagestoryLib/VintagestoryLib.csproj"))!;
        foreach (string config in new[] { "Release", "Debug" })
        {
            string candidate = Path.GetFullPath(Path.Combine(buildLibDir, "bin", config, "net10.0", "VintagestoryLib-patched.dll"));
            if (File.Exists(candidate)) return candidate;
        }
        string? compiledLibPath = new[] { "Release", "Debug" }
            .Select(config => Path.GetFullPath(Path.Combine(buildLibDir, "bin", config, "net10.0", "VintagestoryLib.dll")))
            .FirstOrDefault(File.Exists);
        if (compiledLibPath == null)
        {
            throw new FileNotFoundException("Neither VintagestoryLib-patched.dll nor donor VintagestoryLib.dll was found.");
        }
        string? vanillaLibPath = new[] { ".vanilla/win-x64/vintagestory/VintagestoryLib.vanilla.dll", ".vanilla/win-x64/vintagestory/VintagestoryLib.dll" }
            .Select(rel => { try { return PatchReader.FindRepositoryFile(rel); } catch (FileNotFoundException) { return null; } })
            .FirstOrDefault(p => p != null && File.Exists(p));
        if (vanillaLibPath == null)
        {
            throw new FileNotFoundException("Vanilla VintagestoryLib.dll not found under .vanilla/win-x64/vintagestory.");
        }
        tempPatchedPath = Path.Combine(Path.GetTempPath(), $"optimum-fsr-patched-{Guid.NewGuid():N}.dll");
        int exitCode = Optimum.Patcher.Program.Run(new[] { vanillaLibPath, compiledLibPath, tempPatchedPath });
        Assert.Equal(0, exitCode);
        return tempPatchedPath;
    }

    private static void AssertReferencesField(MethodDefinition method, string fieldName)
    {
        Assert.Contains(method.Body.Instructions, i =>
            i.Operand is FieldReference fr &&
            fr.Name == fieldName);
    }

    private static int CreateProgram(string vsh, string fsh)
    {
        int vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, vsh);
        GL.CompileShader(vs);
        GL.GetShader(vs, ShaderParameter.CompileStatus, out int okV);
        Assert.Equal(1, okV);

        int fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fsh);
        GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out int okF);
        Assert.Equal(1, okF);

        int p = GL.CreateProgram();
        GL.AttachShader(p, vs);
        GL.AttachShader(p, fs);
        GL.LinkProgram(p);
        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int okL);
        Assert.Equal(1, okL);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        return p;
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
