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
/// DLSS plan, Phase 2: the upscaler slot above the device seam -
/// <see cref="DlssUpscaler" />, the piece the real frame drives.
///
/// The device seam itself is covered by <see cref="NgxDlssEvaluateTests" />
/// (resources, one create, one evaluate). What is asserted here is what the
/// frame needs from the host and nothing below it:
/// <list type="bullet">
/// <item>the render size and the preset come from the SDK's own optimal-settings
/// query, and the jitter phase count and LOD bias follow that answer rather than
/// a table;</item>
/// <item>several real frames with the history accumulating produce a
/// display-resolution image - Present between frames, no readback in the loop;</item>
/// <item>a resize rebuilds the feature: a new one created, the old one retired
/// on the frame timeline and really released, nothing leaked;</item>
/// <item>an unavailable NGX degrades to the old path with one log line and no
/// exception, and the setting is stood down for the session.</item>
/// </list>
///
/// Everything that needs the driver skips, never fails, when the shim, the
/// driver library or the NGX feature libraries are absent.
/// </summary>
[Collection(NgxCollection.Name)]
public class DlssUpscalerTests
{
    private readonly ITestOutputHelper _output;
    private readonly NgxRuntime _ngx;
    private readonly List<string> _logged = new();

    public DlssUpscalerTests(ITestOutputHelper output, NgxRuntime ngx)
    {
        _output = output;
        _ngx = ngx;
    }

    private const int DisplayWidth = 2560;
    private const int DisplayHeight = 1490;

    /// <summary>The pattern's grid, as in the evaluate tests: 4x4 alternating blocks.</summary>
    private const int Blocks = 4;

    // --------------------------------------------------------- without a GPU

    /// <summary>
    /// The plan's derived numbers, which every consumer downstream reads: the
    /// upscale ratio, the temporal contract's phase count for that ratio (§2), and
    /// the LOD bias every vendor SDK recommends. None of them is a per-preset
    /// constant; all three follow the render size the query answered.
    /// </summary>
    [Theory]
    // Quality at 2560x1490 is 1707x993 on driver 615.71.09: ~1.5x, so 8 * 1.5^2 = 18 phases.
    [InlineData(1707, 993, 18, -1.585f)]
    // Performance is exactly half: 8 * 2^2 = 32 phases, bias log2(0.5) - 1.
    [InlineData(1280, 745, 32, -2.0f)]
    // DLAA renders at display size: the sequence is the native 8 and there is no bias.
    [InlineData(2560, 1490, 8, 0f)]
    public void ThePlanDerivesThePhaseCountAndTheLodBiasFromTheRenderSize(
        int renderWidth, int renderHeight, int phases, float bias)
    {
        var plan = new UpscalePlan(
            renderWidth, renderHeight, DisplayWidth, DisplayHeight, NgxPerfQuality.MaxQuality);
        Assert.True(plan.IsValid);
        Assert.Equal(phases, plan.JitterPhaseCount);
        Assert.Equal(bias, plan.LodBias, 3);
        Assert.Equal((uint)renderWidth, plan.ToSettings().RenderWidth);
        Assert.Equal(NgxDlssSettings.ContractFlags, plan.ToSettings().Flags);
    }

    /// <summary>A plan that could not be made is never usable, whatever it holds.</summary>
    [Theory]
    [InlineData(0, 0, 100, 100)]
    [InlineData(100, 100, 0, 0)]
    [InlineData(200, 100, 100, 100)] // a render size larger than the display size is not an upscale
    public void AnImpossiblePlanIsNotValid(int rw, int rh, int dw, int dh)
    {
        Assert.False(new UpscalePlan(rw, rh, dw, dh, NgxPerfQuality.MaxQuality).IsValid);
    }

    /// <summary>
    /// The preset names against NVSDK_NGX_PerfQuality_Value's own numbering
    /// (nvsdk_ngx_defs.h): MaxPerf 0, Balanced 1, MaxQuality 2, UltraPerformance 3,
    /// DLAA 5. Spelled as integers because the enum is internal to the renderer.
    /// </summary>
    [Theory]
    [InlineData("dlaa", 5)]
    [InlineData("quality", 2)]
    [InlineData("balanced", 1)]
    [InlineData("performance", 0)]
    [InlineData("ultraperformance", 3)]
    [InlineData("PERFORMANCE", 0)]
    [InlineData("nonsense", 2)]
    [InlineData(null, 2)]
    public void EveryPresetNameMapsOntoNgxsOwnEnum(string? preset, int expected)
    {
        Assert.Equal(expected, (int)DlssUpscaler.QualityOf(preset));
    }

    /// <summary>
    /// The failure path, which is the one a user on an AMD card takes: the host
    /// refuses, says why once, stands the setting down for the session and throws
    /// nothing. Driven here through a bring-up with no device handles, because
    /// that reaches the same single failure path every other cause reaches.
    /// </summary>
    [Fact]
    public void AnUnavailableNgxDegradesToTheOldPathWithOneLogLine()
    {
        string upscaler = OptimumConfig.Upscaler;
        try
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.Upscaler = "dlss";
            Assert.True(OptimumConfig.UpscalerReplacesTaa);

            var host = new DlssUpscaler(Record);
            Assert.False(host.BringUp(null!, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            Assert.False(host.Active);
            Assert.NotNull(host.Unavailable);

            // One line, and the rest of the client is back on the old chain for
            // the session - the setting on disk is untouched.
            Assert.Single(_logged);
            Assert.Contains("DLSS is unavailable", _logged[0]);
            Assert.False(OptimumConfig.UpscalerReplacesTaa);
            Assert.Equal("off", OptimumConfig.EffectiveUpscaler);
            Assert.Equal("dlss", OptimumConfig.Upscaler);

            // A second failure says nothing more: one line per session.
            Assert.False(host.BringUp(null!, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            Assert.Single(_logged);

            // And nothing was planned, created or leaked on the way.
            Assert.False(host.TryPlan(DisplayWidth, DisplayHeight, "quality", out UpscalePlan plan));
            Assert.False(plan.IsValid);
            Assert.Equal(0, host.FeaturesCreated);
            Assert.Equal(0, host.FeaturesRetired);
            host.Dispose();
        }
        finally
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.Upscaler = upscaler;
            OptimumConfig.ClearUpscalerPlan();
        }
    }

    /// <summary>
    /// With the setting off nothing is prepared at all: no session, no NGX, no
    /// device extensions requested - the client runs exactly the chain it ran
    /// before this phase.
    /// </summary>
    [Fact]
    public void WithTheSettingOffNothingIsPrepared()
    {
        string upscaler = OptimumConfig.Upscaler;
        try
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.Upscaler = "off";
            Assert.False(DlssUpscaler.Requested);
            Assert.Null(DlssUpscaler.TryPrepare(System.IO.Path.GetTempPath(), Record));
            Assert.Empty(_logged);
        }
        finally
        {
            OptimumConfig.Upscaler = upscaler;
        }
    }

    // ------------------------------------------------------------ on the GPU

    /// <summary>
    /// The plan really comes from the driver: the optimal-settings query for this
    /// display size at Quality and at Performance, through the host, with the
    /// numbers the SDK answered on driver 615.71.09.
    /// </summary>
    [SkippableFact]
    public void ThePlanComesFromTheSdksOwnOptimalSettingsQuery()
    {
        DlssUpscaler host = Host();

        Assert.True(host.TryPlan(DisplayWidth, DisplayHeight, "quality", out UpscalePlan quality));
        Log("plan at quality: " + quality);
        Assert.Equal(1707, quality.RenderWidth);
        Assert.Equal(993, quality.RenderHeight);
        Assert.Equal(NgxPerfQuality.MaxQuality, quality.Quality);

        Assert.True(host.TryPlan(DisplayWidth, DisplayHeight, "performance", out UpscalePlan performance));
        Log("plan at performance: " + performance);
        Assert.Equal(1280, performance.RenderWidth);
        Assert.Equal(745, performance.RenderHeight);
        Assert.Equal(32, performance.JitterPhaseCount);

        Assert.True(host.TryPlan(DisplayWidth, DisplayHeight, "dlaa", out UpscalePlan dlaa));
        Log("plan at dlaa: " + dlaa);
        Assert.Equal(DisplayWidth, dlaa.RenderWidth);
        Assert.Equal(0f, dlaa.LodBias);

        host.Dispose();
    }

    /// <summary>
    /// The frame shape the client will run: create the feature on the first
    /// frame, evaluate every frame with Present between them and no readback in
    /// the loop, read the display-resolution result back at the end.
    ///
    /// Asserted: every evaluate succeeds, exactly one feature is created for the
    /// eight frames, the output is the display size and still carries the
    /// pattern, the LOD bias the samplers read is the one this plan implies, and
    /// the layers stay clean with synchronization validation and best practices
    /// on.
    /// </summary>
    [SkippableFact]
    public void TheFramePathUpscalesToDisplayResolutionWithHistoryAccumulating()
    {
        DlssUpscaler host = Host(out VulkanDevice seam, out int mark);

        Assert.True(host.TryPlan(DisplayWidth, DisplayHeight, "performance", out UpscalePlan plan));
        Log("plan: " + plan);

        int color = CreateColor(seam, plan);
        int depth = CreateDepth(seam, plan);
        int motion = CreateMotion(seam, plan);
        int output = CreateOutput(seam, plan);

        const int frames = 8;
        float[]? pixels = null;
        for (int index = 0; index < frames; index++)
        {
            seam.BeginFrame();
            Assert.True(host.EnsureFeature(plan), "the feature was refused at frame " + index);

            var frame = new NgxDlssEvaluation
            {
                // Frame 0 is the reset frame: no history to reproject. This is where
                // EnumTemporalResetReason maps onto NGX's reset flag in the client.
                Reset = index == 0,
                MotionVectorScaleX = 1f,
                MotionVectorScaleY = 1f,
            };
            NgxResult evaluated = host.Evaluate(color, depth, motion, output, frame);
            Log("frame " + index + " evaluate: " + NgxInterop.Describe(evaluated));
            Assert.Equal(NgxResult.Success, evaluated);

            if (index == frames - 1) pixels = ReadOutput(seam, output);
            seam.Present();
        }

        // One feature for the whole run: the plan never changed, so nothing was
        // rebuilt and nothing leaked.
        Assert.Equal(1, host.FeaturesCreated);
        Assert.Equal(0, host.FeaturesRetired);
        Assert.Equal(plan, host.Plan);
        Assert.Equal(plan.LodBias, OptimumConfig.UpscalerLodBias, 3);

        Assert.NotNull(pixels);
        Assert.Equal(DisplayWidth * DisplayHeight, pixels!.Length / 4);
        BlockReport report = SampleBlocks(pixels, DisplayWidth, DisplayHeight);
        Log("after " + frames + " frames: " + report);
        Assert.True(report.MinBright > 0.75f, "dimmest bright block " + report.MinBright);
        Assert.True(report.MaxDark < 0.25f, "brightest dark block " + report.MaxDark);

        host.RetireFeature();
        seam.DrainDeferredDeletions();
        Assert.Equal(1, host.FeaturesRetired);
        Assert.Equal(0f, OptimumConfig.UpscalerLodBias);
        host.Dispose();

        GpuTest.AssertCleanSince(seam, mark);
    }

    /// <summary>
    /// A resize is a new plan, and a new plan is a new feature: the old one is
    /// retired onto the frame timeline and really released by the driver, the
    /// counters agree, and the frame after the resize evaluates with the reset
    /// flag - which is what throws the history away rather than reprojecting it
    /// through a size that no longer exists.
    /// </summary>
    [SkippableFact]
    public void AResizeRebuildsTheFeatureWithoutLeakingIt()
    {
        DlssUpscaler host = Host(out VulkanDevice seam, out int mark);

        Assert.True(host.TryPlan(DisplayWidth, DisplayHeight, "performance", out UpscalePlan first));
        Assert.True(host.TryPlan(1920, 1080, "performance", out UpscalePlan second));
        Log("before the resize: " + first);
        Log("after the resize:  " + second);
        Assert.NotEqual(first, second);

        int colorA = CreateColor(seam, first);
        int depthA = CreateDepth(seam, first);
        int motionA = CreateMotion(seam, first);
        int outputA = CreateOutput(seam, first);

        seam.BeginFrame();
        Assert.True(host.EnsureFeature(first));
        NgxDlssFeature? featureA = host.Feature;
        Assert.NotNull(featureA);
        Assert.Equal(NgxResult.Success, host.Evaluate(colorA, depthA, motionA, outputA,
            new NgxDlssEvaluation { Reset = true, MotionVectorScaleX = 1f, MotionVectorScaleY = 1f }));
        seam.Present();

        int colorB = CreateColor(seam, second);
        int depthB = CreateDepth(seam, second);
        int motionB = CreateMotion(seam, second);
        int outputB = CreateOutput(seam, second);

        seam.BeginFrame();
        // The rebuild happens here: a feature created for the old plan cannot
        // serve the new one, so it is retired and another is created.
        Assert.True(host.EnsureFeature(second));
        Assert.NotSame(featureA, host.Feature);
        Assert.Equal(2, host.FeaturesCreated);
        Assert.Equal(1, host.FeaturesRetired);
        Assert.Equal(second, host.Plan);
        Assert.Equal(second.LodBias, OptimumConfig.UpscalerLodBias, 3);

        Assert.Equal(NgxResult.Success, host.Evaluate(colorB, depthB, motionB, outputB,
            new NgxDlssEvaluation { Reset = true, MotionVectorScaleX = 1f, MotionVectorScaleY = 1f }));
        float[] pixels = ReadOutput(seam, outputB);
        seam.Present();

        // The retired feature is released once the timeline says nothing names it.
        seam.DrainDeferredDeletions();
        Log("released the pre-resize feature: " + NgxInterop.Describe(featureA!.LastReleaseResult) +
            ", parameters: " + NgxInterop.Describe(featureA.LastDestroyParametersResult));
        Assert.False(featureA.IsValid);
        Assert.Equal(NgxResult.Success, featureA.LastReleaseResult);
        Assert.Equal(NgxResult.Success, featureA.LastDestroyParametersResult);

        // And the new size is what comes out.
        Assert.Equal(1920 * 1080, pixels.Length / 4);
        BlockReport report = SampleBlocks(pixels, 1920, 1080);
        Log("after the resize: " + report);
        Assert.True(report.MinBright > 0.75f, "dimmest bright block " + report.MinBright);
        Assert.True(report.MaxDark < 0.25f, "brightest dark block " + report.MaxDark);

        // Shutting the host down retires what is left: created and retired agree,
        // which is the whole of "a resize must not leak features".
        host.Shutdown();
        Assert.Equal(host.FeaturesCreated, host.FeaturesRetired);

        GpuTest.AssertCleanSince(seam, mark);
    }

    // ---------------------------------------------------------------- inputs

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

    /// <summary>Constant window depth, 0 = near and not reversed (temporal contract §7.3).</summary>
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

    /// <summary>Display resolution, RGBA16F and STORAGE: without it NGX answers FAIL_RWFlagMissing.</summary>
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

    // --------------------------------------------------------------- harness

    private DlssUpscaler Host() => Host(out _, out _);

    /// <summary>
    /// A host on the process's one NGX session (<see cref="NgxRuntime" />), which
    /// owns Init and Shutdown because the driver allows one lifetime per process.
    /// The host does everything else exactly as it does in the client.
    /// </summary>
    private DlssUpscaler Host(out VulkanDevice seam, out int mark)
    {
        _ngx.Require();
        foreach (string line in _ngx.Diagnostics) Log(line);
        mark = _ngx.MessageMark();
        seam = _ngx.Device;

        var host = new DlssUpscaler(Record);
        Assert.True(host.AdoptSession(_ngx.Session, seam, _ngx.VkDevice));
        Assert.True(host.Active);
        return host;
    }

    private void Record(string line)
    {
        _logged.Add(line);
        Log(line);
    }

    private void Log(string line)
    {
        _output.WriteLine(line);
        Console.Error.WriteLine("[dlss-host] " + line);
        Console.Error.Flush();
    }
}
