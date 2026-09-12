using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// DLSS Super Resolution running for real on this GPU, from this renderer:
/// synthetic render-resolution inputs in, display-resolution pixels out, read
/// back inside the frame and asserted.
///
/// This is the first thing above <see cref="NgxAvailabilityTests" /> that
/// actually produces pixels. It pins the three pieces the frame-graph
/// integration will sit on:
/// <list type="number">
/// <item><b>Resource tagging.</b> <see cref="NgxResourceVk" /> wraps one of our
/// VkImage/VkImageView pairs the way <c>NVSDK_NGX_Create_ImageView_Resource_VK</c>
/// does; the layout is checked against the headers below and was verified against
/// gcc.</item>
/// <item><b>Feature lifecycle.</b> <see cref="NgxDlssFeature" /> creates the
/// feature with the SDK's creation block on a command buffer inside a frame, and
/// is released on the frame timeline through the retire queue.</item>
/// <item><b>Evaluate.</b> <c>VulkanDevice.EvaluateDlss</c> places our own
/// barriers (NGX places none), calls EvaluateFeature on the frame's command
/// buffer, and restores what NGX leaves behind.</item>
/// </list>
///
/// Everything skips, never fails, when the shim, the driver library or the NGX
/// feature libraries are absent: the feature libraries are NVIDIA
/// redistributables and are not in this repository (point
/// <c>OPTIMUM_NGX_FEATURE_PATH</c> at DLSS SDK 310.9.1's
/// <c>lib/Linux_x86_64/rel</c>, and build the shim with <c>make native</c>).
/// </summary>
[Collection(NgxCollection.Name)]
public class NgxDlssEvaluateTests
{
    private readonly ITestOutputHelper _output;
    private readonly NgxRuntime _ngx;

    public NgxDlssEvaluateTests(ITestOutputHelper output, NgxRuntime ngx)
    {
        _output = output;
        _ngx = ngx;
    }

    /// <summary>
    /// Render resolution: DLSS Performance's optimal size at the display size
    /// below, and inside Quality's dynamic range (1280x745 .. 2560x1490), which
    /// is what lets the feature below be created at Quality with these inputs.
    /// </summary>
    private const int RenderWidth = 1280;

    private const int RenderHeight = 745;
    private const int DisplayWidth = 2560;
    private const int DisplayHeight = 1490;

    /// <summary>The pattern's grid: four columns by four rows of alternating blocks.</summary>
    private const int Blocks = 4;

    // -------------------------------------------------------------- interop

    /// <summary>
    /// <c>NVSDK_NGX_Resource_VK</c> and its image-view member are passed to the
    /// driver by pointer, so a wrong offset is not a wrong answer but a read of
    /// the wrong words inside libnvidia-ngx. These are the numbers gcc produces
    /// from <c>nvsdk_ngx_defs_vk.h</c> on the x86-64 SysV ABI.
    /// </summary>
    [Fact]
    public unsafe void TheResourceStructHasTheLayoutTheHeadersDefine()
    {
        Assert.Equal(48, sizeof(NgxImageViewInfoVk));
        Assert.Equal(56, sizeof(NgxResourceVk));

        var resource = default(NgxResourceVk);
        byte* origin = (byte*)&resource;
        Assert.Equal(16, (int)((byte*)&resource.ImageViewInfo.AspectMask - origin));
        Assert.Equal(36, (int)((byte*)&resource.ImageViewInfo.Format - origin));
        Assert.Equal(40, (int)((byte*)&resource.ImageViewInfo.Width - origin));
        Assert.Equal(44, (int)((byte*)&resource.ImageViewInfo.Height - origin));
        Assert.Equal(48, (int)((byte*)&resource.Type - origin));
        Assert.Equal(52, (int)(&resource.ReadWrite - origin));
    }

    /// <summary>
    /// The flags Optimum's temporal contract v1 implies, spelled out so a change
    /// to either side has to be deliberate: render-resolution motion vectors
    /// (§7.1), not jittered (§7.1), depth not inverted (§7.3), colour not HDR and
    /// auto-exposure on (§7.5).
    /// </summary>
    [Fact]
    public void TheContractFlagsAreTheOnesTheTemporalContractImplies()
    {
        NgxDlssCreateFlags flags = NgxDlssSettings.ContractFlags;
        Assert.True(flags.HasFlag(NgxDlssCreateFlags.MotionVectorsLowRes));
        Assert.True(flags.HasFlag(NgxDlssCreateFlags.AutoExposure));
        Assert.False(flags.HasFlag(NgxDlssCreateFlags.MotionVectorsJittered));
        Assert.False(flags.HasFlag(NgxDlssCreateFlags.DepthInverted));
        Assert.False(flags.HasFlag(NgxDlssCreateFlags.IsHdr));
        Assert.False(flags.HasFlag(NgxDlssCreateFlags.DoSharpening));
    }

    // ---------------------------------------------------------- the deliverable

    /// <summary>
    /// DLSS evaluates on this device and the output really is an upscale of the
    /// input.
    ///
    /// The input is a 4x4 checkerboard of full-bright and black blocks at
    /// 1280x745, with a constant depth, zero motion and zero jitter; the feature
    /// is created at Quality for a 2560x1490 output. After one evaluate the
    /// output is read back inside the same frame and four things are asserted:
    /// the call returned Success; the readback is the display size; the image is
    /// neither blank nor uniform; and the pattern is still there - every block's
    /// interior is on the right side of the midpoint, with a wide margin.
    ///
    /// The margin is 0.25 rather than something tighter because DLSS is a
    /// reconstruction, not a resample: a first frame with no history is allowed
    /// to be soft, and the block edges are allowed to ring. Block interiors are
    /// 640x372 display pixels, and the patch measured at each centre is 64x64, so
    /// nothing within 280 pixels of an edge is sampled at all.
    /// </summary>
    [SkippableFact]
    public void DlssEvaluatesOnTheDeviceAndTheOutputCarriesThePattern()
    {
        VulkanDevice seam = Require(out int mark);

        var settings = new NgxDlssSettings(
            RenderWidth, RenderHeight, DisplayWidth, DisplayHeight,
            NgxPerfQuality.MaxQuality, NgxDlssSettings.ContractFlags);

        int color = CreateColor(seam);
        int depth = CreateDepth(seam);
        int motion = CreateMotion(seam);
        int output = CreateOutput(seam);
        Log("inputs: colour " + RenderWidth + "x" + RenderHeight + " R8G8B8A8_UNORM, depth R32_SFLOAT = 0.5, " +
            "motion R16G16_SFLOAT = 0, output " + DisplayWidth + "x" + DisplayHeight +
            " R16G16B16A16_SFLOAT + STORAGE");

        seam.BeginFrame();

        NgxResult created = seam.CreateDlssFeature(settings, out NgxDlssFeature? feature);
        Log("NVSDK_NGX_VULKAN_CreateFeature1 " + settings + ": " + NgxInterop.Describe(created));
        Assert.Equal(NgxResult.Success, created);
        Assert.NotNull(feature);
        Assert.True(feature!.IsValid);

        var frame = new NgxDlssEvaluation
        {
            // A first frame is a reset frame: there is no history to reproject.
            Reset = true,
            JitterOffsetX = 0f,
            JitterOffsetY = 0f,
            MotionVectorScaleX = 1f,
            MotionVectorScaleY = 1f,
        };

        NgxResult evaluated = seam.EvaluateDlss(feature, color, depth, motion, output, frame);
        Log("NVSDK_NGX_VULKAN_EvaluateFeature: " + NgxInterop.Describe(evaluated));
        Assert.Equal(NgxResult.Success, evaluated);

        // Inside the frame: the copy is recorded into this frame, the recorded
        // part is submitted and waited for, and the frame carries on.
        float[] pixels = ReadOutput(seam, output);
        seam.Present();

        // A change of size or preset is a new feature, never a reconfigured one:
        // NGX sizes its internal buffers at creation and the preset is fixed for
        // the feature's lifetime.
        Assert.True(feature.Matches(settings));
        Assert.False(feature.Matches(settings with { Quality = NgxPerfQuality.MaxPerf }));
        Assert.False(feature.Matches(settings with { RenderWidth = RenderWidth + 64 }));

        // Released on the frame timeline, never inline: the evaluate recorded
        // above is still in flight when Retire is called.
        seam.RetireDlssFeature(feature);
        int drained = seam.DrainDeferredDeletions();
        Log("NVSDK_NGX_VULKAN_ReleaseFeature: " + NgxInterop.Describe(feature.LastReleaseResult) +
            ", DestroyParameters: " + NgxInterop.Describe(feature.LastDestroyParametersResult) +
            " (retire queue drained " + drained + ")");
        Assert.Equal(NgxResult.Success, feature.LastReleaseResult);
        Assert.Equal(NgxResult.Success, feature.LastDestroyParametersResult);
        Assert.False(feature.IsValid);

        Statistics statistics = Measure(pixels);
        Log("output statistics: " + statistics);
        Assert.Equal(DisplayWidth * DisplayHeight, pixels.Length / 4);

        // Not blank, not uniform.
        Assert.True(statistics.Mean > 0.05f, "the output is (nearly) black: mean " + statistics.Mean);
        Assert.True(statistics.Max - statistics.Min > 0.5f,
            "the output is uniform: min " + statistics.Min + " max " + statistics.Max);

        // The pattern survived.
        BlockReport report = SampleBlocks(pixels, DisplayWidth, DisplayHeight);
        Log(report.ToString());
        Assert.True(report.MinBright > 0.75f,
            "a bright block came back dark: dimmest bright block " + report.MinBright);
        Assert.True(report.MaxDark < 0.25f,
            "a dark block came back bright: brightest dark block " + report.MaxDark);

        ReportValidation(seam, mark);
        GpuTest.AssertCleanSince(seam, mark);
    }

    /// <summary>
    /// Several frames in a row with the history accumulating, which is the only
    /// way a temporal feature can be judged (CLAUDE.md's temporal rule): Present
    /// between frames, no readback inside the loop, one readback at the end.
    ///
    /// What is asserted is what a multi-frame run can prove and a single frame
    /// cannot: every evaluate returns Success, the feature stays valid across all
    /// of them, the accumulated output still carries the pattern, and the
    /// validation layers - synchronization validation and best practices
    /// included - stay clean with DLSS's own dispatches in the frame. The inputs
    /// do not move, so the converged image is the same image; a drifting one
    /// would fail the pattern assert.
    /// </summary>
    [SkippableFact]
    public void DlssStaysValidAndCleanAcrossSeveralFramesOfAccumulatingHistory()
    {
        VulkanDevice seam = Require(out int mark);

        var settings = new NgxDlssSettings(
            RenderWidth, RenderHeight, DisplayWidth, DisplayHeight,
            NgxPerfQuality.MaxQuality, NgxDlssSettings.ContractFlags);

        int color = CreateColor(seam);
        int depth = CreateDepth(seam);
        int motion = CreateMotion(seam);
        int output = CreateOutput(seam);

        const int frames = 8;
        NgxDlssFeature? feature = null;
        float[]? pixels = null;

        for (int index = 0; index < frames; index++)
        {
            seam.BeginFrame();

            if (feature == null)
            {
                NgxResult created = seam.CreateDlssFeature(settings, out feature);
                Log("frame " + index + " CreateFeature1: " + NgxInterop.Describe(created));
                Assert.Equal(NgxResult.Success, created);
                Assert.NotNull(feature);
            }

            Assert.True(feature!.IsValid, "the feature stopped being valid at frame " + index);
            Assert.True(feature.Matches(settings),
                "the feature no longer matches the settings it was created for at frame " + index);

            var frame = new NgxDlssEvaluation
            {
                Reset = index == 0,
                MotionVectorScaleX = 1f,
                MotionVectorScaleY = 1f,
            };

            NgxResult evaluated = seam.EvaluateDlss(feature, color, depth, motion, output, frame);
            Log("frame " + index + " EvaluateFeature (reset=" + (index == 0 ? 1 : 0) + "): " +
                NgxInterop.Describe(evaluated));
            Assert.Equal(NgxResult.Success, evaluated);

            // No readback inside the loop: the history has to accumulate on the
            // GPU across real frames, which a per-frame wait would hide.
            if (index == frames - 1) pixels = ReadOutput(seam, output);

            seam.Present();
        }

        seam.RetireDlssFeature(feature!);
        int drained = seam.DrainDeferredDeletions();
        Log("NVSDK_NGX_VULKAN_ReleaseFeature: " + NgxInterop.Describe(feature!.LastReleaseResult) +
            " (retire queue drained " + drained + ")");
        Assert.Equal(NgxResult.Success, feature.LastReleaseResult);

        Assert.NotNull(pixels);
        Statistics statistics = Measure(pixels!);
        Log("output statistics after " + frames + " frames: " + statistics);
        BlockReport report = SampleBlocks(pixels!, DisplayWidth, DisplayHeight);
        Log(report.ToString());

        Assert.True(statistics.Max - statistics.Min > 0.5f,
            "the converged output is uniform: min " + statistics.Min + " max " + statistics.Max);
        Assert.True(report.MinBright > 0.75f, "dimmest bright block after convergence " + report.MinBright);
        Assert.True(report.MaxDark < 0.25f, "brightest dark block after convergence " + report.MaxDark);

        ReportValidation(seam, mark);
        GpuTest.AssertCleanSince(seam, mark);
    }

    // ------------------------------------------------------------- the inputs

    /// <summary>True where the checkerboard is bright, for a point in any resolution.</summary>
    private static bool Bright(int x, int y, int width, int height) =>
        (x * Blocks / width + y * Blocks / height) % 2 == 0;

    private static int CreateColor(VulkanDevice seam)
    {
        var pixels = new byte[RenderWidth * RenderHeight * 4];
        for (int y = 0; y < RenderHeight; y++)
        {
            for (int x = 0; x < RenderWidth; x++)
            {
                byte value = Bright(x, y, RenderWidth, RenderHeight) ? (byte)255 : (byte)0;
                int offset = (y * RenderWidth + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }
        return Upload(seam, RenderWidth, RenderHeight, Format.R8G8B8A8Unorm, storage: false, pixels, 4);
    }

    /// <summary>
    /// Constant depth. Window depth in [0, 1] with 0 = near and no reversal, which
    /// is what the temporal contract stores (§7.3) and what
    /// <see cref="NgxDlssCreateFlags.DepthInverted" /> being off promises.
    /// </summary>
    private static int CreateDepth(VulkanDevice seam)
    {
        var depth = new float[RenderWidth * RenderHeight];
        Array.Fill(depth, 0.5f);
        return Upload(seam, RenderWidth, RenderHeight, Format.R32Sfloat, storage: false,
            MemoryMarshal.AsBytes<float>(depth).ToArray(), 4);
    }

    /// <summary>Zero motion: RG16F, and all-zero bytes are (0, 0) in half floats too.</summary>
    private static int CreateMotion(VulkanDevice seam) => Upload(
        seam, RenderWidth, RenderHeight, Format.R16G16Sfloat, storage: false,
        new byte[RenderWidth * RenderHeight * 4], 4);

    /// <summary>
    /// The output: display resolution, RGBA16F, and STORAGE usage - without it
    /// NGX answers FAIL_RWFlagMissing (DLSS Programming Guide §3.4).
    /// </summary>
    private static int CreateOutput(VulkanDevice seam) =>
        seam.CreateUpscaleTexture(DisplayWidth, DisplayHeight, Format.R16G16B16A16Sfloat, storage: true);

    private static unsafe int Upload(
        VulkanDevice seam, int width, int height, Format format, bool storage, byte[] pixels, int bytesPerPixel)
    {
        fixed (byte* data = pixels)
        {
            return seam.CreateUpscaleTexture(
                width, height, format, storage, (IntPtr)data, bytesPerPixel);
        }
    }

    // ------------------------------------------------------------ the readback

    /// <summary>Level 0 of the RGBA16F output as floats, four per pixel, rows in memory order.</summary>
    private static float[] ReadOutput(VulkanDevice seam, int texture)
    {
        byte[] raw = seam.ReadBackLevel0ForTests(texture);
        ReadOnlySpan<Half> halves = MemoryMarshal.Cast<byte, Half>(raw);
        var values = new float[halves.Length];
        for (int i = 0; i < halves.Length; i++) values[i] = (float)halves[i];
        return values;
    }

    private readonly record struct Statistics(float Min, float Max, float Mean, int NonFinite)
    {
        public override string ToString() =>
            "min " + Min.ToString("0.####") + " max " + Max.ToString("0.####") +
            " mean " + Mean.ToString("0.####") + " non-finite " + NonFinite;
    }

    /// <summary>Luminance statistics over the whole output, green channel as the proxy for a grey pattern.</summary>
    private static Statistics Measure(float[] pixels)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        double sum = 0;
        int nonFinite = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            float value = pixels[i + 1];
            if (!float.IsFinite(value)) { nonFinite++; continue; }
            if (value < min) min = value;
            if (value > max) max = value;
            sum += value;
        }
        int counted = pixels.Length / 4 - nonFinite;
        return new Statistics(
            counted == 0 ? 0 : min, counted == 0 ? 0 : max,
            counted == 0 ? 0 : (float)(sum / counted), nonFinite);
    }

    private readonly record struct BlockReport(float MinBright, float MaxBright, float MinDark, float MaxDark)
    {
        public override string ToString() =>
            "block interiors: bright " + MinBright.ToString("0.####") + ".." + MaxBright.ToString("0.####") +
            ", dark " + MinDark.ToString("0.####") + ".." + MaxDark.ToString("0.####");
    }

    /// <summary>
    /// The mean of a 64x64 patch at the centre of each of the 16 blocks, split
    /// into the blocks the pattern says are bright and the ones it says are dark.
    /// </summary>
    private static BlockReport SampleBlocks(float[] pixels, int width, int height)
    {
        const int patch = 64;
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

    // ---------------------------------------------------------------- harness

    /// <summary>
    /// The process's one NGX device (<see cref="NgxRuntime" />), or a skip. Also
    /// answers the mark from which this test's own layer messages start: the
    /// device is shared, so the ones before it are not this test's to judge.
    /// </summary>
    private VulkanDevice Require(out int mark)
    {
        _ngx.Require();
        foreach (string line in _ngx.Diagnostics) Log(line);
        mark = _ngx.MessageMark();
        return _ngx.Device;
    }

    /// <summary>
    /// Every layer message the run produced, first line each, so the report can
    /// quote what the layers actually said with DLSS in the frame rather than
    /// only whether the assert passed.
    /// </summary>
    private void ReportValidation(VulkanDevice seam, int mark)
    {
        List<string> all = GpuTest.MessagesOf(seam);
        Log("validation messages since this test began: " + Math.Max(0, all.Count - mark));
        for (int i = Math.Max(0, mark); i < all.Count; i++)
        {
            Log("  " + all[i].Split('\n')[0].Trim());
        }
    }

    /// <summary>
    /// Both to the test output and to stderr: xunit only flushes its buffer when a
    /// test ends, and anything that takes the test host down inside NGX would
    /// otherwise lose exactly the line that says where it died.
    /// </summary>
    private void Log(string line)
    {
        _output.WriteLine(line);
        Console.Error.WriteLine("[dlss] " + line);
        Console.Error.Flush();
    }
}
