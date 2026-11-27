using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public sealed class DynamicRenderScaleTests
{
    [Fact]
    public void TierSnapping_Clamping_And_ViewportResolution_WorkAsContracted()
    {
        Assert.Equal(1.0f, OptimumDynamicRenderScale.SnapToQuantizedTier(1.05f));
        Assert.Equal(1.0f, OptimumDynamicRenderScale.SnapToQuantizedTier(0.95f));
        Assert.Equal(0.85f, OptimumDynamicRenderScale.SnapToQuantizedTier(0.85f));
        Assert.Equal(0.77f, OptimumDynamicRenderScale.SnapToQuantizedTier(0.77f));
        Assert.Equal(0.67f, OptimumDynamicRenderScale.SnapToQuantizedTier(0.50f));

        // Supersampling (SSAA > 1.0, e.g., 4x MegaScreenshot) overrides dynamic scale
        Assert.Equal(4.0f, OptimumDynamicRenderScale.ResolveActiveViewportScale(4.0f, 1.0f, 0.77f, dynamicEnabled: true));
        Assert.Equal(0.25f, OptimumDynamicRenderScale.ResolveActiveViewportScale(0.25f, 1.0f, 0.77f, dynamicEnabled: true));
        // Normal dynamic mode follows current dynamic scale without touching ClientSettings.SSAA (1.0)
        Assert.Equal(0.77f, OptimumDynamicRenderScale.ResolveActiveViewportScale(1.0f, 1.0f, 0.77f, dynamicEnabled: true));
        // Static manual RenderScale when dynamic is off
        Assert.Equal(0.85f, OptimumDynamicRenderScale.ResolveActiveViewportScale(1.0f, 0.85f, 0.85f, dynamicEnabled: false));
        // Manual vanilla SSAA < 1.0 when both Optimum static and dynamic scales are off
        Assert.Equal(0.75f, OptimumDynamicRenderScale.ResolveActiveViewportScale(0.75f, 1.0f, 1.0f, dynamicEnabled: false));
        Assert.Equal(0.5f, OptimumDynamicRenderScale.ResolveActiveViewportScale(0.5f, 1.0f, 1.0f, dynamicEnabled: false));
        // Vanilla SSAA < 1.0 stays on vanilla scale when Optimum RenderScale is Native (1.0), even if DynamicRenderScale is enabled
        Assert.Equal(0.5f, OptimumDynamicRenderScale.ResolveActiveViewportScale(0.5f, 1.0f, 1.0f, dynamicEnabled: true));
        Assert.Equal(0.75f, OptimumDynamicRenderScale.ResolveActiveViewportScale(0.75f, 1.0f, 1.0f, dynamicEnabled: true));
        // Dynamic scale still overrides when Optimum RenderScale is configured below 1.0
        Assert.Equal(0.77f, OptimumDynamicRenderScale.ResolveActiveViewportScale(0.85f, 0.85f, 0.77f, dynamicEnabled: true));
    }

    [Fact]
    public void Controller_DownscaleAndUpscaleHysteresis_PreventsOscillation()
    {
        var controller = new OptimumDynamicRenderScale();
        controller.ResetToBaseline(1.0f, 3840, 2160, 0.67f, 60, OptimumDynamicUpscalerPreference.Fsr);

        // Warmup samples (3) do not move scale
        for (int i = 0; i < 3; i++)
        {
            var w = controller.RecordSample(25.0, 1.5);
            Assert.False(w.ScaleChanged);
            Assert.Equal(1.0f, controller.CurrentScale);
        }

        // Invalid / disjoint GL samples are ignored cleanly
        Assert.False(controller.RecordSample(0.0, 1.0).ScaleChanged);
        Assert.False(controller.RecordSample(double.NaN, 1.0).ScaleChanged);
        Assert.False(controller.RecordSample(5000.0, 1.0).ScaleChanged);
        Assert.False(controller.RecordSample(12.0, -1.0).ScaleChanged);

        // Small over-budget within deadband (<= 1.06 * 16.67ms = 17.67ms) must not downscale
        for (int i = 0; i < 10; i++)
        {
            var d = controller.RecordSample(16.0, 1.2);
            Assert.Equal(1.0f, d.TargetScale);
        }

        // Sustained over-budget triggers single step down to 0.85
        bool steppedTo85 = false;
        for (int i = 0; i < 10; i++)
        {
            var d = controller.RecordSample(21.0, 1.5);
            if (d.ScaleChanged)
            {
                steppedTo85 = true;
                Assert.Equal(0.85f, d.TargetScale);
                Assert.Equal(OptimumDynamicUpscalerKind.Fsr, d.Upscaler);
                break;
            }
        }
        Assert.True(steppedTo85);

        // Immediate light frames inside lockout window must NOT flicker back up
        for (int i = 0; i < 60; i++)
        {
            var d = controller.RecordSample(6.0, 1.0);
            Assert.Equal(0.85f, d.TargetScale);
        }

        // Sustained low load after lockout + stability window restores 1.00
        bool restoredTo100 = false;
        for (int i = 0; i < 180; i++)
        {
            var d = controller.RecordSample(6.0, 1.0);
            if (d.ScaleChanged)
            {
                restoredTo100 = true;
                Assert.Equal(1.0f, d.TargetScale);
                Assert.Equal(OptimumDynamicUpscalerKind.Native, d.Upscaler);
                break;
            }
        }
        Assert.True(restoredTo100);
    }

    [Fact]
    public void Controller_AutoUpscaler_DemotesExpensiveFsrOnWeakIgpu_AndKeepsFsrOnFastGpu()
    {
        // Weak iGPU @ 4K (mirrors measured Intel UHD 620: EASU+RCAS ~22.2ms vs Bilinear ~3.1ms)
        var igpu = new OptimumDynamicRenderScale();
        igpu.ResetToBaseline(1.0f, 3840, 2160, 0.67f, 60, OptimumDynamicUpscalerPreference.Auto);
        for (int i = 0; i < 3; i++) igpu.RecordSample(38.0, 2.5); // warmup
        for (int i = 0; i < 6; i++)
        {
            igpu.RecordSample(38.0, 2.5);
            if (igpu.CurrentScale < 1.0f) break;
        }
        Assert.Equal(0.85f, igpu.CurrentScale);
        Assert.Equal(OptimumDynamicUpscalerKind.Fsr, igpu.ActiveUpscaler);

        bool demoted = false;
        for (int i = 0; i < 8; i++)
        {
            var d = igpu.RecordSample(28.0, 22.2);
            if (d.UpscalerDemotedToBilinear)
            {
                demoted = true;
                Assert.Equal(OptimumDynamicUpscalerKind.Bilinear, d.Upscaler);
                break;
            }
        }
        Assert.True(demoted);
        Assert.Equal(OptimumDynamicUpscalerKind.Bilinear, igpu.ActiveUpscaler);

        // Fast GPU @ 4K (EASU+RCAS ~1.4ms << 35% budget, total improves nicely) keeps FSR
        var dgpu = new OptimumDynamicRenderScale();
        dgpu.ResetToBaseline(1.0f, 3840, 2160, 0.67f, 60, OptimumDynamicUpscalerPreference.Auto);
        for (int i = 0; i < 3; i++) dgpu.RecordSample(23.0, 0.6); // warmup
        for (int i = 0; i < 6; i++)
        {
            dgpu.RecordSample(23.0, 0.6);
            if (dgpu.CurrentScale < 1.0f) break;
        }
        Assert.Equal(0.85f, dgpu.CurrentScale);
        for (int i = 0; i < 10; i++)
        {
            var d = dgpu.RecordSample(15.0, 1.4);
            Assert.False(d.UpscalerDemotedToBilinear);
        }
        Assert.Equal(OptimumDynamicUpscalerKind.Fsr, dgpu.ActiveUpscaler);
    }

    [Fact]
    public void Controller_RespectsMinScaleFloorAndManualCeiling()
    {
        var controller = new OptimumDynamicRenderScale();
        controller.ResetToBaseline(0.85f, 1920, 1080, 0.77f, 60, OptimumDynamicUpscalerPreference.Bilinear);
        Assert.Equal(0.85f, controller.CurrentScale);

        // Fragment-bound GPU load drops proportionally as pixel count shrinks (0.85^2 -> 0.77^2)
        for (int i = 0; i < 80; i++)
        {
            double scene = 36.0 * controller.CurrentScale * controller.CurrentScale;
            controller.RecordSample(scene, 0.8);
        }
        Assert.Equal(0.77f, controller.CurrentScale);

        for (int i = 0; i < 300; i++)
        {
            controller.RecordSample(4.0, 0.5);
        }
        Assert.Equal(0.85f, controller.CurrentScale);

        // Flat / CPU-bound load with zero scene reduction aborts lower tier cleanly back to ceiling
        var flat = new OptimumDynamicRenderScale();
        flat.ResetToBaseline(0.85f, 1920, 1080, 0.67f, 60, OptimumDynamicUpscalerPreference.Bilinear);
        for (int i = 0; i < 12; i++) flat.RecordSample(30.0, 0.8);
        Assert.Equal(0.85f, flat.CurrentScale);
    }

    [Fact]
    public void GpuTimerRing_And_BoundedTwoTierLruCache_WorkWithoutOverflowOrLeaks()
    {
        var ring = new OptimumGpuTimerRing(4);
        Assert.True(ring.TryPeekWriteSlot(out int s0));
        Assert.Equal(0, s0);
        // Uncommitted write slot does not advance write pointer or consume ring capacity
        Assert.False(ring.TryPeekReadSlot(out _));
        ring.CommitWriteSlot();
        Assert.Equal(1, ring.PendingCount);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(ring.TryAcquireWriteSlot(out _));
        }
        Assert.True(ring.IsFull);
        Assert.False(ring.TryPeekWriteSlot(out _));

        for (int i = 0; i < 4; i++)
        {
            Assert.True(ring.TryPeekReadSlot(out int r));
            Assert.Equal(i, r);
            ring.ConsumeReadSlot();
        }
        Assert.Equal(0, ring.PendingCount);

        // Wrap-around check
        Assert.True(ring.TryAcquireWriteSlot(out int wrapSlot));
        Assert.Equal(0, wrapSlot);

        var cache = new OptimumTierCache<string>(2);
        cache.Put(1.0f, 3840, 2160, "tier-100", out string? evicted);
        Assert.Null(evicted);
        cache.Put(0.85f, 3840, 2160, "tier-085", out evicted);
        Assert.Null(evicted);
        Assert.Equal(2, cache.Count);

        // Touch tier-100 so tier-085 becomes LRU; adding 0.77 must evict tier-085 cleanly
        Assert.True(cache.TryGet(1.0f, 3840, 2160, out string? hit100));
        Assert.Equal("tier-100", hit100);
        cache.Put(0.77f, 3840, 2160, "tier-077", out evicted);
        Assert.Equal("tier-085", evicted);
        Assert.Equal(2, cache.Count);

        var drained = new string[4];
        int removed = cache.DrainExcept("tier-100", drained);
        Assert.Equal(1, removed);
        Assert.Equal("tier-077", drained[0]);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void ConfigPersistence_CompatibilityGuard_And_DiagnosticsSummaryWorkCleanly()
    {
        bool origEnabled = OptimumConfig.DynamicRenderScaleEnabled;
        int origFps = OptimumConfig.DynamicRenderScaleTargetFps;
        float origMin = OptimumConfig.DynamicRenderScaleMinScale;
        string origUpscaler = OptimumConfig.DynamicRenderScaleUpscaler;
        string? tempDir = null;
        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), "optimum-dynres-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            OptimumConfig.SetDataPath(tempDir);
            OptimumConfig.DynamicRenderScaleEnabled = true;
            OptimumConfig.DynamicRenderScaleTargetFps = 90;
            OptimumConfig.DynamicRenderScaleMinScale = 0.77f;
            OptimumConfig.DynamicRenderScaleUpscaler = "Bilinear";
            OptimumConfig.Save();

            OptimumConfig.DynamicRenderScaleEnabled = false;
            OptimumConfig.DynamicRenderScaleTargetFps = 60;
            OptimumConfig.DynamicRenderScaleMinScale = 0.67f;
            OptimumConfig.DynamicRenderScaleUpscaler = "Auto";
            OptimumConfig.Load();

            Assert.True(OptimumConfig.DynamicRenderScaleEnabled);
            Assert.Equal(90, OptimumConfig.DynamicRenderScaleTargetFps);
            Assert.Equal(0.77f, OptimumConfig.DynamicRenderScaleMinScale, 3);
            Assert.Equal("Bilinear", OptimumConfig.DynamicRenderScaleUpscaler);
            Assert.True(OptimumConfig.EffectiveDynamicRenderScale);

            // Shader compatibility report blocking RenderScale also disables dynamic scaling
            string optimumDir = Path.Combine(tempDir, ".optimum");
            Directory.CreateDirectory(optimumDir);
            File.WriteAllText(
                Path.Combine(optimumDir, "shader-compatibility.json"),
                "{\"disabledFeatures\":[\"RenderScale\"]}");
            OptimumConfig.SetDataPath(tempDir);
            Assert.False(OptimumConfig.EffectiveDynamicRenderScale);

            File.Delete(Path.Combine(optimumDir, "shader-compatibility.json"));
            OptimumConfig.SetDataPath(tempDir);
            Assert.True(OptimumConfig.EffectiveDynamicRenderScale);

            OptimumDiagnostics.ResetAllCounters();
            OptimumDiagnostics.RecordDynamicRenderScaleState(0.85f, OptimumDynamicUpscalerKind.Fsr, 2, 12.5, 1.8);
            OptimumDiagnostics.RecordDynamicRenderScaleStep(isDownscale: true);
            OptimumDiagnostics.RecordDynamicRenderScaleCacheHit();
            OptimumDiagnostics.RecordDynamicRenderScaleCacheAlloc();
            OptimumDiagnostics.RecordDynamicRenderScaleCacheEvict();
            OptimumDiagnostics.RecordDynamicRenderScaleFsrFallback();

            string summary = OptimumDiagnostics.GetDynamicRenderScaleSummary();
            Assert.Contains("enabled=True", summary, StringComparison.Ordinal);
            Assert.Contains("scale=0.85", summary, StringComparison.Ordinal);
            Assert.Contains("upscaler=Fsr", summary, StringComparison.Ordinal);
            Assert.Contains("cachedTiers=2", summary, StringComparison.Ordinal);
            Assert.Contains("downscales=1", summary, StringComparison.Ordinal);
            Assert.Contains("cacheHits=1", summary, StringComparison.Ordinal);
            Assert.Contains("fsrFallbacks=1", summary, StringComparison.Ordinal);

            OptimumDiagnostics.ResetAllCounters();
            summary = OptimumDiagnostics.GetDynamicRenderScaleSummary();
            Assert.Contains("scale=1.00", summary, StringComparison.Ordinal);
            Assert.Contains("downscales=0", summary, StringComparison.Ordinal);
        }
        finally
        {
            OptimumConfig.SetDataPath(null);
            OptimumConfig.DynamicRenderScaleEnabled = origEnabled;
            OptimumConfig.DynamicRenderScaleTargetFps = origFps;
            OptimumConfig.DynamicRenderScaleMinScale = origMin;
            OptimumConfig.DynamicRenderScaleUpscaler = origUpscaler;
            OptimumDiagnostics.ResetAllCounters();
            if (tempDir != null && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void PatchedVintagestoryLib_ShipsDynamicRenderScaleMethodsAndCecilRegistry()
    {
        string patchPlatform = File.ReadAllText(PatchReader.FindRepositoryFile(
            Path.Combine("patches", "VintagestoryLib", "Vintagestory.Client.NoObf", "ClientPlatformWindows.cs.patch")));
        string patchOit = File.ReadAllText(PatchReader.FindRepositoryFile(
            Path.Combine("patches", "VintagestoryLib", "Vintagestory.Client.NoObf", "SystemRenderOITLayers.cs.patch")));
        string patchScreenMgr = File.ReadAllText(PatchReader.FindRepositoryFile(
            Path.Combine("patches", "VintagestoryLib", "Vintagestory.Client", "ScreenManager.cs.patch")));
        string patchRunningGame = File.ReadAllText(PatchReader.FindRepositoryFile(
            Path.Combine("patches", "VintagestoryLib", "Vintagestory.Client", "GuiScreenRunningGame.cs.patch")));

        Assert.Contains("OptimumUpdateDynamicResolution", patchPlatform);
        Assert.Contains("OptimumPopulateScaledFrameBuffers", patchPlatform);
        Assert.Contains("OptimumTryActivateCachedOit", patchOit);
        Assert.Contains("OptimumUpdateDynamicResolution", patchScreenMgr);
        Assert.Contains("ResolveActiveViewportScale", patchRunningGame);

        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile(Path.Combine("patches", "cecil-owned.list")));
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client/ScreenManager.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client/GuiScreenRunningGame.cs.patch", cecilList);

        string? tempPatchedPath = null;
        try
        {
            string patchedPath = ResolveOrBuildPatchedVintagestoryLibPath(out tempPatchedPath);
            using var asm = AssemblyDefinition.ReadAssembly(patchedPath);

            var platformType = asm.MainModule.GetType("Vintagestory.Client.NoObf.ClientPlatformWindows")!;
            Assert.NotNull(platformType.Fields.SingleOrDefault(f => f.Name == "optimumDynamicController"));
            Assert.NotNull(platformType.Fields.SingleOrDefault(f => f.Name == "optimumTierCache"));
            Assert.NotNull(platformType.Methods.SingleOrDefault(m => m.Name == "OptimumUpdateDynamicResolution"));
            Assert.NotNull(platformType.Methods.SingleOrDefault(m => m.Name == "OptimumActivateScaleTier"));
            var rebuildMethod = platformType.Methods.Single(m => m.Name == "RebuildFrameBuffers");
            AssertCallsMember(rebuildMethod, "OptimumDisposeFallbackTiersBeforeRebuild");

            var oitType = asm.MainModule.GetType("Vintagestory.Client.NoObf.SystemRenderOITLayers")!;
            Assert.NotNull(oitType.Methods.SingleOrDefault(m => m.Name == "OptimumTryActivateCachedOit"));
            var beforeOitType = oitType.NestedTypes.Single(t => t.Name == "BeforeOIT");
            AssertCallsMember(beforeOitType.Methods.Single(m => m.Name == "OnRenderFrame"), "OptimumTryActivateCachedOit");
            AssertCallsMember(beforeOitType.Methods.Single(m => m.Name == "freeResources"), "OptimumFreeAllOitResources");

            var chunkType = asm.MainModule.GetType("Vintagestory.Client.NoObf.ChunkRenderer")!;
            AssertCallsMember(chunkType.Methods.Single(m => m.Name == "ApplyOptimumTextureLodBias"), "ApplyOptimumCustomSamplerLodBias");

            var screenMgrType = asm.MainModule.GetType("Vintagestory.Client.ScreenManager")!;
            AssertCallsMember(screenMgrType.Methods.Single(m => m.Name == "Render"), "OptimumUpdateDynamicResolution");

            var runningGameType = asm.MainModule.GetType("Vintagestory.Client.GuiScreenRunningGame")!;
            AssertCallsMember(runningGameType.Methods.Single(m => m.Name == "RenderToPrimary"), "ResolveActiveViewportScale");
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
    public void RealGlContext_DynamicRenderScaleShaderFxaaToggleLodBiasAndOitTierCacheWorkCleanly()
    {
        GLFWProvider.CheckForMainThread = false;
        using var window = new NativeWindow(new NativeWindowSettings
        {
            StartVisible = false,
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,
            ClientSize = new Vector2i(128, 72),
        });
        window.MakeCurrent();
        while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }

        // Check GL_TIMESTAMP support (llvmpipe / software renderers may report 0 bits)
        GL.GetQuery(QueryTarget.Timestamp, GetQueryParam.QueryCounterBits, out int tsBits);

        string shadersDir = Path.GetDirectoryName(PatchReader.FindRepositoryFile(Path.Combine("sources", "shaders", "final.fsh")))!;
        string finalSrc = File.ReadAllText(Path.Combine(shadersDir, "final.fsh"));
        string easuSrc = File.ReadAllText(Path.Combine(shadersDir, "fsr-easu.fsh"));
        string rcasSrc = File.ReadAllText(Path.Combine(shadersDir, "fsr-rcas.fsh"));
        string easuVert = File.ReadAllText(Path.Combine(shadersDir, "fsr-easu.vsh"));
        string rcasVert = File.ReadAllText(Path.Combine(shadersDir, "fsr-rcas.vsh"));

        Assert.Contains("uniform int optimumDisableFxaa = 0;", finalSrc);

        // Compile real FSR shaders + a minimal FXAA-toggle shader matching final.fsh's uniform contract
        int easuProg = CreateProgram(easuVert, easuSrc);
        int rcasProg = CreateProgram(rcasVert, rcasSrc);
        int finalToggleProg = CreateProgram(easuVert, FxaaToggleFragShader);

        int[] tierPrimaryFbo = new int[2];
        int[] tierPrimaryTex = new int[2];
        int[] tierOitReveal = new int[2];
        int[] tierOitAccum = new int[2];
        int[] widths = { 128, 86 };
        int[] heights = { 72, 48 };
        for (int i = 0; i < 2; i++)
        {
            tierPrimaryFbo[i] = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, tierPrimaryFbo[i]);
            tierPrimaryTex[i] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tierPrimaryTex[i]);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, widths[i], heights[i], 0, PixelFormat.Rgba, PixelType.UnsignedByte, new byte[widths[i] * heights[i] * 4]);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tierPrimaryTex[i], 0);

            // Simulate OIT cache attachments bound once per tier FBO
            tierOitReveal[i] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tierOitReveal[i]);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb8, widths[i], heights[i], 0, PixelFormat.Rgb, PixelType.UnsignedByte, IntPtr.Zero);
            tierOitAccum[i] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2DArray, tierOitAccum[i]);
            GL.TexImage3D(TextureTarget.Texture2DArray, 0, PixelInternalFormat.Rgba16f, widths[i], heights[i], 3, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        }

        int sampler = GL.GenSampler();
        int vao = GL.GenVertexArray();
        int[] qStart = new int[2];
        int[] qEnd = new int[2];
        GL.GenQueries(2, qStart);
        GL.GenQueries(2, qEnd);

        try
        {
            // Verify TEXTURE_LOD_BIAS updates on sampler objects cleanly
            GL.SamplerParameter(sampler, (SamplerParameterName)34049, MathF.Log2(0.77f));
            GL.GetSamplerParameter(sampler, (SamplerParameterName)34049, out float bias);
            Assert.InRange(bias, -0.38f, -0.37f);
            GL.SamplerParameter(sampler, (SamplerParameterName)34049, 0.0f);
            GL.GetSamplerParameter(sampler, (SamplerParameterName)34049, out bias);
            Assert.Equal(0.0f, bias);

            // Verify runtime optimumDisableFxaa toggles shader output with zero recompile
            byte[] pxEnabled = new byte[4];
            byte[] pxDisabled = new byte[4];
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, tierPrimaryFbo[0]);
            GL.Viewport(0, 0, widths[0], heights[0]);
            GL.BindVertexArray(vao);
            GL.UseProgram(finalToggleProg);
            int loc = GL.GetUniformLocation(finalToggleProg, "optimumDisableFxaa");
            Assert.True(loc >= 0);

            if (tsBits > 0) GL.QueryCounter(qStart[0], QueryCounterTarget.Timestamp);
            GL.Uniform1(loc, 0);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pxEnabled);

            GL.Uniform1(loc, 1);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pxDisabled);
            if (tsBits > 0) GL.QueryCounter(qEnd[0], QueryCounterTarget.Timestamp);

            Assert.True(pxEnabled[0] > 200);
            Assert.True(pxDisabled[2] > 200);
            Assert.NotEqual(pxEnabled[0], pxDisabled[0]);

            // Exercise real FSR EASU + RCAS shaders across scaled tier 1 -> native 0 without GL errors
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, tierPrimaryFbo[0]);
            GL.UseProgram(easuProg);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, tierPrimaryTex[1]);
            GL.Uniform1(GL.GetUniformLocation(easuProg, "inputScene"), 0);
            GL.Uniform2(GL.GetUniformLocation(easuProg, "inputTexelSize"), 1f / widths[1], 1f / heights[1]);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.UseProgram(rcasProg);
            GL.BindTexture(TextureTarget.Texture2D, tierPrimaryTex[0]);
            GL.Uniform1(GL.GetUniformLocation(rcasProg, "inputScene"), 0);
            GL.Uniform2(GL.GetUniformLocation(rcasProg, "inputTexelSize"), 1f / widths[0], 1f / heights[0]);
            GL.Uniform1(GL.GetUniformLocation(rcasProg, "sharpness"), 0.2f);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            if (tsBits > 0)
            {
                GL.Finish();
                GL.GetQueryObject(qEnd[0], GetQueryObjectParam.QueryResultAvailable, out int avail);
                Assert.NotEqual(0, avail);
            }

            Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            GL.BindVertexArray(0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteQueries(2, qStart);
            GL.DeleteQueries(2, qEnd);
            GL.DeleteVertexArray(vao);
            GL.DeleteSampler(sampler);
            GL.DeleteProgram(easuProg);
            GL.DeleteProgram(rcasProg);
            GL.DeleteProgram(finalToggleProg);
            for (int i = 0; i < 2; i++)
            {
                GL.DeleteFramebuffer(tierPrimaryFbo[i]);
                GL.DeleteTexture(tierPrimaryTex[i]);
                GL.DeleteTexture(tierOitReveal[i]);
                GL.DeleteTexture(tierOitAccum[i]);
            }
        }
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
        tempPatchedPath = Path.Combine(Path.GetTempPath(), $"optimum-dynres-patched-{Guid.NewGuid():N}.dll");
        int exitCode = Optimum.Patcher.Program.Run(new[] { vanillaLibPath, compiledLibPath, tempPatchedPath });
        Assert.Equal(0, exitCode);
        return tempPatchedPath;
    }

    private static void AssertCallsMember(MethodDefinition method, string memberName)
    {
        Assert.Contains(method.Body.Instructions, i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
            i.Operand is MethodReference mr &&
            mr.Name == memberName);
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

    private const string FxaaToggleFragShader = @"#version 330 core
uniform int optimumDisableFxaa = 0;
in vec2 texCoord;
out vec4 outColor;
void main() {
    outColor = (optimumDisableFxaa == 0)
        ? vec4(1.0, 0.0, 0.0, 1.0)
        : vec4(0.0, 0.0, 1.0, 1.0);
}";
}
