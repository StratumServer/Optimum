using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// DLSS plan, Phase 6: what the settings tab does to a live device.
///
/// The tab writes <c>OptimumConfig.UpscalerQuality</c> or
/// <c>OptimumConfig.Upscaler</c> and then calls
/// <c>ClientPlatformAbstract.ApplyOptimumUpscalerSettings</c>, whose Vulkan
/// override retires the live feature and rebuilds the frame buffers; the next
/// frame plans again and the host creates the feature the new plan needs. This
/// exercises that sequence on the real driver, which is the only place the two
/// failures it guards against can happen: a preset change that keeps evaluating
/// through a feature sized for the old plan, and a switch to off that leaves a
/// vendor feature alive holding its internal buffers.
///
/// Skips, never fails, without the shim, the driver library or the NGX feature
/// libraries.
/// </summary>
[Collection(NgxCollection.Name)]
public class UpscalerPresetSwitchTests
{
    private readonly ITestOutputHelper _output;
    private readonly NgxRuntime _ngx;

    public UpscalerPresetSwitchTests(ITestOutputHelper output, NgxRuntime ngx)
    {
        _output = output;
        _ngx = ngx;
    }

    private const int DisplayWidth = 2560;
    private const int DisplayHeight = 1490;

    /// <summary>
    /// Quality to Performance and then to off, the way the tab drives it.
    ///
    /// Asserted: the preset change really produces a different plan from the
    /// driver's own query; the old feature is retired and a new one created for
    /// that plan (never reconfigured - NGX sizes its internal buffers at
    /// creation); the retired one is really released once the frame timeline says
    /// nothing names it; the evaluate after the change succeeds and still carries
    /// the pattern; switching to off leaves no feature alive, no LOD bias and no
    /// render scale published, and hands the temporal resolve back to TAA
    /// (<c>UpscalerReplacesTaa</c> false); created and retired agree at the end,
    /// which is the whole of "nothing leaked"; and the layers stay clean with
    /// synchronization validation and best practices on.
    /// </summary>
    [SkippableFact]
    public void APresetChangeRebuildsTheFeatureAndSwitchingOffStandsItDown()
    {
        _ngx.Require();
        foreach (string line in _ngx.Diagnostics) Log(line);
        int mark = _ngx.MessageMark();
        VulkanDevice seam = _ngx.Device;

        string upscaler = OptimumConfig.Upscaler;
        string preset = OptimumConfig.UpscalerQuality;
        var host = new DlssUpscaler(Log);
        try
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.UpscalerQuality = "quality";
            Assert.True(host.AdoptSession(_ngx.Session, seam, _ngx.VkDevice));
            Assert.True(host.Active);

            // --- the frame as it runs at the first preset -------------------
            Assert.True(host.TryPlan(DisplayWidth, DisplayHeight, OptimumConfig.UpscalerQuality,
                out UpscalePlan quality));
            Log("plan at quality: " + quality);

            int colorA = CreateColor(seam, quality);
            int depthA = CreateDepth(seam, quality);
            int motionA = CreateMotion(seam, quality);
            int outputA = CreateOutput(seam, quality);

            seam.BeginFrame();
            Assert.True(host.EnsureFeature(quality));
            NgxDlssFeature? featureAtQuality = host.Feature;
            Assert.NotNull(featureAtQuality);
            Assert.Equal(NgxResult.Success, host.Evaluate(colorA, depthA, motionA, outputA, Reset()));
            seam.Present();
            Assert.Equal(quality.LodBias, OptimumConfig.UpscalerLodBias, 3);

            // --- the tab changes the preset ---------------------------------
            // onOptimumUpscalerQualityChanged: persist, then the platform's
            // ApplyOptimumUpscalerSettings override retires the live feature and
            // rebuilds the targets, which re-plans through the vendor's query.
            OptimumConfig.UpscalerQuality = "performance";
            host.RetireFeature();
            Assert.Equal(1, host.FeaturesRetired);
            // Nothing claims a plan while there is no feature: the bias and the
            // render scale the samplers and the jitter read are cleared with it.
            Assert.Equal(0f, OptimumConfig.UpscalerLodBias);
            Assert.Equal(0f, OptimumConfig.UpscalerRenderScale);

            Assert.True(host.TryPlan(DisplayWidth, DisplayHeight, OptimumConfig.UpscalerQuality,
                out UpscalePlan performance));
            Log("plan at performance: " + performance);
            Assert.NotEqual(quality, performance);
            Assert.True(performance.RenderWidth < quality.RenderWidth,
                "performance must render below quality: " + performance + " vs " + quality);

            int colorB = CreateColor(seam, performance);
            int depthB = CreateDepth(seam, performance);
            int motionB = CreateMotion(seam, performance);
            int outputB = CreateOutput(seam, performance);

            seam.BeginFrame();
            Assert.True(host.EnsureFeature(performance));
            Assert.NotSame(featureAtQuality, host.Feature);
            Assert.Equal(2, host.FeaturesCreated);
            Assert.Equal(1, host.FeaturesRetired);
            Assert.Equal(performance, host.Plan);
            Assert.Equal(performance.LodBias, OptimumConfig.UpscalerLodBias, 3);
            Assert.Equal(performance.RenderScale, OptimumConfig.UpscalerRenderScale, 3);

            Assert.Equal(NgxResult.Success, host.Evaluate(colorB, depthB, motionB, outputB, Reset()));
            float[] pixels = ReadOutput(seam, outputB);
            seam.Present();

            // The feature the preset change retired is really gone.
            seam.DrainDeferredDeletions();
            Log("released the pre-change feature: " +
                NgxInterop.Describe(featureAtQuality!.LastReleaseResult) + ", parameters: " +
                NgxInterop.Describe(featureAtQuality.LastDestroyParametersResult));
            Assert.False(featureAtQuality.IsValid);
            Assert.Equal(NgxResult.Success, featureAtQuality.LastReleaseResult);
            Assert.Equal(NgxResult.Success, featureAtQuality.LastDestroyParametersResult);

            // The frame after the change is still a display-resolution upscale of
            // the pattern, not a stale or torn image.
            Assert.Equal(DisplayWidth * DisplayHeight, pixels.Length / 4);
            BlockReport report = SampleBlocks(pixels, DisplayWidth, DisplayHeight);
            Log("after the preset change: " + report);
            Assert.True(report.MinBright > 0.75f, "dimmest bright block " + report.MinBright);
            Assert.True(report.MaxDark < 0.25f, "brightest dark block " + report.MaxDark);

            // --- the tab switches the slot off ------------------------------
            NgxDlssFeature? featureAtPerformance = host.Feature;
            Assert.NotNull(featureAtPerformance);
            OptimumConfig.Upscaler = "off";
            host.RetireFeature();
            seam.DrainDeferredDeletions();

            Assert.Null(host.Feature);
            Assert.Equal(host.FeaturesCreated, host.FeaturesRetired);
            Assert.False(featureAtPerformance!.IsValid);
            Assert.Equal(NgxResult.Success, featureAtPerformance.LastReleaseResult);
            // Off is the pre-upscaler chain exactly: no bias, no render scale, and
            // the in-house TAA resolve owns the frame again.
            Assert.Equal(0f, OptimumConfig.UpscalerLodBias);
            Assert.Equal(0f, OptimumConfig.UpscalerRenderScale);
            Assert.False(OptimumConfig.UpscalerReplacesTaa);
            Assert.Equal("off", OptimumConfig.EffectiveUpscaler);

            // A frame recorded after the stand-down still runs and presents; the
            // upscaler simply is not in it.
            seam.BeginFrame();
            seam.Present();
        }
        finally
        {
            host.Shutdown();
            OptimumConfig.Upscaler = upscaler;
            OptimumConfig.UpscalerQuality = preset;
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.ClearUpscalerPlan();
        }

        Assert.Equal(host.FeaturesCreated, host.FeaturesRetired);
        GpuTest.AssertCleanSince(seam, mark);
    }

    // ---------------------------------------------------------------- inputs

    private const int Blocks = 4;

    private static NgxDlssEvaluation Reset() => new NgxDlssEvaluation
    {
        Reset = true,
        MotionVectorScaleX = 1f,
        MotionVectorScaleY = 1f,
    };

    private static bool Bright(int x, int y, int width, int height) =>
        (x * Blocks / width + y * Blocks / height) % 2 == 0;

    private static int CreateColor(VulkanDevice seam, in UpscalePlan plan)
    {
        int width = plan.RenderWidth, height = plan.RenderHeight;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = Bright(x, y, width, height) ? (byte)255 : (byte)0;
                int offset = (y * width + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }
        return Upload(seam, width, height, Format.R8G8B8A8Unorm, pixels, 4);
    }

    private static int CreateDepth(VulkanDevice seam, in UpscalePlan plan)
    {
        var depth = new float[plan.RenderWidth * plan.RenderHeight];
        Array.Fill(depth, 0.5f);
        return Upload(seam, plan.RenderWidth, plan.RenderHeight, Format.R32Sfloat,
            MemoryMarshal.AsBytes<float>(depth).ToArray(), 4);
    }

    private static int CreateMotion(VulkanDevice seam, in UpscalePlan plan) => Upload(
        seam, plan.RenderWidth, plan.RenderHeight, Format.R16G16Sfloat,
        new byte[plan.RenderWidth * plan.RenderHeight * 4], 4);

    private static int CreateOutput(VulkanDevice seam, in UpscalePlan plan) =>
        seam.CreateUpscaleTexture(
            plan.DisplayWidth, plan.DisplayHeight, Format.R16G16B16A16Sfloat, storage: true);

    private static unsafe int Upload(
        VulkanDevice seam, int width, int height, Format format, byte[] pixels, int bytesPerPixel)
    {
        fixed (byte* data = pixels)
        {
            return seam.CreateUpscaleTexture(width, height, format, false, (IntPtr)data, bytesPerPixel);
        }
    }

    private static float[] ReadOutput(VulkanDevice seam, int texture)
    {
        byte[] raw = seam.ReadBackLevel0ForTests(texture);
        ReadOnlySpan<Half> halves = MemoryMarshal.Cast<byte, Half>(raw);
        var values = new float[halves.Length];
        for (int i = 0; i < halves.Length; i++) values[i] = (float)halves[i];
        return values;
    }

    private readonly record struct BlockReport(float MinBright, float MaxBright, float MinDark, float MaxDark)
    {
        public override string ToString() =>
            "block interiors: bright " + MinBright.ToString("0.####") + ".." + MaxBright.ToString("0.####") +
            ", dark " + MinDark.ToString("0.####") + ".." + MaxDark.ToString("0.####");
    }

    private static BlockReport SampleBlocks(float[] pixels, int width, int height)
    {
        const int patch = 32;
        float minBright = float.MaxValue, maxBright = float.MinValue;
        float minDark = float.MaxValue, maxDark = float.MinValue;
        for (int by = 0; by < Blocks; by++)
        {
            for (int bx = 0; bx < Blocks; bx++)
            {
                int centreX = (int)((bx + 0.5) * width / Blocks);
                int centreY = (int)((by + 0.5) * height / Blocks);
                double sum = 0;
                int count = 0;
                for (int y = centreY - patch / 2; y < centreY + patch / 2; y++)
                {
                    for (int x = centreX - patch / 2; x < centreX + patch / 2; x++)
                    {
                        if ((uint)x >= width || (uint)y >= height) continue;
                        sum += pixels[(y * width + x) * 4 + 1];
                        count++;
                    }
                }
                float mean = count == 0 ? 0 : (float)(sum / count);
                if (Bright(centreX, centreY, width, height))
                {
                    minBright = Math.Min(minBright, mean);
                    maxBright = Math.Max(maxBright, mean);
                }
                else
                {
                    minDark = Math.Min(minDark, mean);
                    maxDark = Math.Max(maxDark, mean);
                }
            }
        }
        return new BlockReport(minBright, maxBright, minDark, maxDark);
    }

    private void Log(string line)
    {
        _output.WriteLine(line);
        Console.Error.WriteLine("[dlss-settings] " + line);
        Console.Error.Flush();
    }
}
