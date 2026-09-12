using System;

using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The passthrough upscaler: the slot that plans exactly like DLSS and evaluates
/// with a magnifying blit, which makes it both the diagnostic separating our
/// rendering from the vendor's and the fallback upscaler for a GPU with no vendor
/// path at all.
///
/// What is asserted here:
/// <list type="bullet">
/// <item>it plans a render size for every preset while the process holds no NGX
/// session, no feature and no host - the one entry into NGX, <c>TryPrepare</c>,
/// answers null before it looks at the driver;</item>
/// <item>a real frame on a device built without a single NGX extension produces a
/// display-resolution image carrying the render-resolution content, with both
/// filters, and the layers stay clean;</item>
/// <item>dlss -> passthrough -> off leaks no feature: the counters agree, the
/// retired feature is really released, and the passthrough frames in the middle
/// hold none;</item>
/// <item>and the plan it makes without asking the vendor is the vendor's own plan,
/// size for size, on a driver that can be asked.</item>
/// </list>
///
/// In the NGX collection because the cases mutate <c>OptimumConfig</c>'s upscaler
/// slot, which every other upscaler test reads; the two cases that need no driver
/// still run when NGX is absent, which is the machine the fallback exists for.
/// </summary>
[Collection(NgxCollection.Name)]
public class PassthroughUpscalerTests
{
    private readonly ITestOutputHelper _output;
    private readonly NgxRuntime _ngx;

    public PassthroughUpscalerTests(ITestOutputHelper output, NgxRuntime ngx)
    {
        _output = output;
        _ngx = ngx;
    }

    private const int DisplayWidth = 2560;
    private const int DisplayHeight = 1490;

    /// <summary>The presets, with the size the nominal ratio gives at 2560x1490.</summary>
    public static TheoryData<string, int, int> Presets => new()
    {
        { "dlaa", 2560, 1490 },
        { "quality", 1707, 993 },
        { "balanced", 1485, 864 },
        { "performance", 1280, 745 },
        { "ultraperformance", 853, 497 },
    };

    // ----------------------------------------------------- with no NGX at all

    /// <summary>
    /// The render size follows the preset, and nothing on the way there touches a
    /// vendor runtime.
    ///
    /// "Never calls NGX" is asserted where the platform would make the call: the
    /// only entry into NGX from the frame path is <c>DlssUpscaler.TryPrepare</c>,
    /// and with the slot on "passthrough" it answers null on its first line -
    /// before <c>Diagnose</c>, before a session, before the shim is even asked to
    /// load. Nothing is stood down by it either, so a machine that does have DLSS
    /// can still switch to it afterwards.
    /// </summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void ThePlanFollowsThePresetWithoutAskingNgxAnything(string preset, int width, int height)
    {
        using (new UpscalerSlot("passthrough", preset))
        {
            Assert.True(PassthroughUpscaler.Requested);
            Assert.True(OptimumConfig.UpscalerReplacesTaa);

            // The frame's sizing question, answered from the setting alone.
            Assert.True(PassthroughUpscaler.TryPlanForFrame(
                DisplayWidth, DisplayHeight, out int renderWidth, out int renderHeight, out UpscalePlan plan));
            Log(preset + ": " + plan);
            Assert.Equal(width, renderWidth);
            Assert.Equal(height, renderHeight);
            Assert.Equal(DisplayWidth, plan.DisplayWidth);
            Assert.Equal(DisplayHeight, plan.DisplayHeight);
            Assert.Equal(DlssUpscaler.QualityOf(preset), plan.Quality);

            // No vendor host is asked for, and none is prepared: the slot is not "dlss".
            Assert.False(DlssUpscaler.Requested);
            Assert.Null(DlssUpscaler.TryPrepare(System.IO.Path.GetTempPath(), Log));
            Assert.False(OptimumConfig.UpscalerRuntimeDisabled);

            // Publishing is what makes the LOD bias and the jitter sequence length
            // follow the passthrough frame exactly as they follow a DLSS frame.
            PassthroughUpscaler.Publish(plan);
            Assert.Equal(plan.RenderScale, OptimumConfig.UpscalerRenderScale, 4);
            Assert.Equal(plan.LodBias, OptimumConfig.UpscalerLodBias, 3);
            Assert.Equal(plan.JitterPhaseCount, new UpscalePlan(
                width, height, DisplayWidth, DisplayHeight, plan.Quality).JitterPhaseCount);
        }
    }

    /// <summary>
    /// With the slot off or on "dlss" the passthrough path plans nothing and leaves
    /// both sizes at the display size - which is exactly what the client allocates
    /// when no upscaler was ever enabled. The same rule as
    /// <c>DlssUpscaler.TryPlanForFrame</c>: the setting in force decides.
    /// </summary>
    [Theory]
    [InlineData("off")]
    [InlineData("dlss")]
    public void WithAnotherSlotSelectedNothingIsPlanned(string slot)
    {
        using (new UpscalerSlot(slot, "performance"))
        {
            Assert.False(PassthroughUpscaler.Requested);
            Assert.False(PassthroughUpscaler.TryPlanForFrame(
                DisplayWidth, DisplayHeight, out int width, out int height, out UpscalePlan plan));
            Assert.Equal(DisplayWidth, width);
            Assert.Equal(DisplayHeight, height);
            Assert.False(plan.IsValid);
        }
    }

    /// <summary>
    /// A runtime stand-down is the same answer as switching the slot off, here as
    /// everywhere else: the effective setting decides, and the frame goes back to
    /// the display size with the in-house resolve.
    /// </summary>
    [Fact]
    public void ARuntimeStandDownTakesThePassthroughSlotWithIt()
    {
        using (new UpscalerSlot("passthrough", "performance"))
        {
            Assert.True(PassthroughUpscaler.Requested);
            Assert.True(OptimumConfig.DisableUpscalerAtRuntime());
            Assert.False(PassthroughUpscaler.Requested);
            Assert.False(OptimumConfig.UpscalerReplacesTaa);
            Assert.False(PassthroughUpscaler.TryPlanForFrame(
                DisplayWidth, DisplayHeight, out int width, out int height, out _));
            Assert.Equal(DisplayWidth, width);
            Assert.Equal(DisplayHeight, height);
            // The persisted value is untouched, as with every other stand-down.
            Assert.Equal("passthrough", OptimumConfig.Upscaler);
        }
    }

    // ------------------------------------------------------------- on the GPU

    /// <summary>
    /// The evaluate, on a device that has never heard of NGX: the fixture's device
    /// is not used here at all, this one comes up through <c>GpuTest.NewDevice</c>
    /// with no NGX requirement contributor and therefore none of NGX's instance or
    /// device extensions - so a blit that works here works on any GPU the Vulkan
    /// renderer runs on.
    ///
    /// Asserted per filter: the result is the display size, the render-resolution
    /// block pattern survives the magnification, and the layers report nothing with
    /// synchronization validation and best practices on.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePassthroughFrameMagnifiesTheRenderColourToDisplayResolution(bool nearest)
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        using (new UpscalerSlot("passthrough", "performance"))
        {
            VulkanDevice seam = device!;

            // The sizes the frame would really be built at, from the setting alone.
            Assert.True(PassthroughUpscaler.TryPlanForFrame(
                DisplayWidth, DisplayHeight, out int renderWidth, out int renderHeight, out UpscalePlan plan));
            Assert.True(renderWidth < DisplayWidth && renderHeight < DisplayHeight);
            Log("plan: " + plan + ", filter " + (nearest ? "nearest" : "linear"));

            int source = 0, destination = 0;
            try
            {
                source = CreatePattern(seam, renderWidth, renderHeight);
                destination = seam.CreateUpscaleTexture(
                    DisplayWidth, DisplayHeight, Format.R8G8B8A8Unorm, storage: false);

                seam.BeginFrame();
                Assert.True(seam.BlitColorScaled(source, destination, linear: !nearest),
                    "the driver refused the passthrough blit");
                byte[] pixels = seam.ReadBackLevel0ForTests(destination);
                seam.Present();

                // The display size came out, whatever the render size was.
                Assert.Equal(DisplayWidth * DisplayHeight * 4, pixels.Length);

                // And it is the render-resolution image, magnified: the block
                // interiors keep their values, which a blit of the wrong extent or
                // the wrong image could not produce.
                BlockReport report = SampleBlocks(pixels, DisplayWidth, DisplayHeight);
                Log("after the blit: " + report);
                Assert.True(report.MinBright > 0.9f, "dimmest bright block " + report.MinBright);
                Assert.True(report.MaxDark < 0.1f, "brightest dark block " + report.MaxDark);

                GpuTest.AssertClean(seam);
            }
            finally
            {
                if (source > 0) seam.DeleteTexture(source);
                if (destination > 0) seam.DeleteTexture(destination);
            }
        }
    }

    /// <summary>
    /// The lifecycle the settings tab drives: DLSS with a live feature, then the
    /// passthrough slot, then off.
    ///
    /// What has to hold is that the vendor feature is gone for the whole of the
    /// passthrough stretch and never comes back by itself - a feature left alive
    /// while another upscaler owns the resolve holds vendor memory for a frame that
    /// will never evaluate it. The switch is driven exactly as
    /// <c>ApplyOptimumUpscalerSettings</c> drives it: write the setting, retire the
    /// feature, and let the next frame decide for itself.
    /// </summary>
    [SkippableFact]
    public void SwitchingDlssToPassthroughToOffLeaksNoFeature()
    {
        _ngx.Require();
        VulkanDevice seam = _ngx.Device;
        int mark = _ngx.MessageMark();

        using (new UpscalerSlot("dlss", "performance"))
        {
            var host = new DlssUpscaler(Log);
            int color = 0, depth = 0, motion = 0, output = 0, passthrough = 0;
            try
            {
                Assert.True(host.AdoptSession(_ngx.Session, seam, _ngx.VkDevice));
                Assert.True(host.Active);

                // --- dlss: one feature, serving one plan ----------------------
                Assert.True(DlssUpscaler.TryPlanForFrame(
                    host, DisplayWidth, DisplayHeight, out int width, out int height, out UpscalePlan plan));
                Log("dlss: " + plan);
                color = CreatePattern(seam, width, height);
                depth = seam.CreateUpscaleTexture(width, height, Format.R32Sfloat, storage: false);
                motion = seam.CreateUpscaleTexture(width, height, Format.R16G16Sfloat, storage: false);
                output = seam.CreateUpscaleTexture(
                    DisplayWidth, DisplayHeight, Format.R16G16B16A16Sfloat, storage: true);

                seam.BeginFrame();
                Assert.True(host.EnsureFeature(plan));
                NgxDlssFeature? feature = host.Feature;
                Assert.NotNull(feature);
                Assert.Equal(NgxResult.Success, host.Evaluate(color, depth, motion, output,
                    new NgxDlssEvaluation { Reset = true, MotionVectorScaleX = 1f, MotionVectorScaleY = 1f }));
                seam.Present();
                Assert.Equal(1, host.FeaturesCreated);

                // --- passthrough: the tab's own order -------------------------
                // The setting first, then the feature retired, then the rebuild.
                OptimumConfig.Upscaler = "passthrough";
                host.RetireFeature();
                OptimumConfig.ClearUpscalerPlan();
                Assert.Equal(1, host.FeaturesRetired);
                Assert.Null(host.Feature);
                // The host stays up - NGX allows one lifetime per process and the
                // player may switch back - but it plans nothing while the setting
                // names another slot, and the passthrough plan is the same size.
                Assert.True(host.Active);
                Assert.False(DlssUpscaler.TryPlanForFrame(
                    host, DisplayWidth, DisplayHeight, out _, out _, out _));
                Assert.True(PassthroughUpscaler.TryPlanForFrame(
                    DisplayWidth, DisplayHeight, out int passWidth, out int passHeight, out UpscalePlan passPlan));
                Assert.Equal(width, passWidth);
                Assert.Equal(height, passHeight);

                passthrough = seam.CreateUpscaleTexture(
                    DisplayWidth, DisplayHeight, Format.R8G8B8A8Unorm, storage: false);
                for (int frame = 0; frame < 3; frame++)
                {
                    seam.BeginFrame();
                    Assert.True(seam.BlitColorScaled(color, passthrough, linear: true));
                    PassthroughUpscaler.Publish(passPlan);
                    seam.Present();
                    // Three frames of passthrough, and no feature exists across any
                    // of them: nothing recreated one behind the slot's back.
                    Assert.Null(host.Feature);
                    Assert.Equal(1, host.FeaturesCreated);
                }
                // The retired feature is really released once the timeline says
                // nothing names it - not merely dropped on the floor.
                seam.DrainDeferredDeletions();
                Assert.False(feature!.IsValid);
                Assert.Equal(NgxResult.Success, feature.LastReleaseResult);
                Assert.Equal(NgxResult.Success, feature.LastDestroyParametersResult);

                // --- off: nothing plans, nothing holds ------------------------
                OptimumConfig.Upscaler = "off";
                host.RetireFeature();
                OptimumConfig.ClearUpscalerPlan();
                Assert.False(OptimumConfig.UpscalerReplacesTaa);
                Assert.False(PassthroughUpscaler.TryPlanForFrame(
                    DisplayWidth, DisplayHeight, out int offWidth, out int offHeight, out _));
                Assert.Equal(DisplayWidth, offWidth);
                Assert.Equal(DisplayHeight, offHeight);

                host.Shutdown();
                Assert.Equal(host.FeaturesCreated, host.FeaturesRetired);
                Assert.Equal(0f, OptimumConfig.UpscalerLodBias);

                GpuTest.AssertCleanSince(seam, mark);
            }
            finally
            {
                foreach (int texture in new[] { color, depth, motion, output, passthrough })
                {
                    if (texture > 0) seam.DeleteTexture(texture);
                }
            }
        }
    }

    /// <summary>
    /// The claim the whole comparison rests on: the plan the passthrough upscaler
    /// makes without asking the vendor anything is the plan the vendor's own
    /// optimal-settings query answers, size for size, at every preset. Without this
    /// the two configurations would differ in the render size as well as in the
    /// reconstruction, and neither could be judged against the other.
    /// </summary>
    [SkippableFact]
    public void PassthroughPlanMatchesTheVendorPlanSizeForSize()
    {
        _ngx.Require();
        var host = new DlssUpscaler(Log);
        using (new UpscalerSlot("passthrough", "performance"))
        {
            try
            {
                Assert.True(host.AdoptSession(_ngx.Session, _ngx.Device, _ngx.VkDevice));
                foreach (string preset in new[]
                {
                    "dlaa", "quality", "balanced", "performance", "ultraperformance",
                })
                {
                    Assert.True(host.TryPlan(DisplayWidth, DisplayHeight, preset, out UpscalePlan vendor));
                    Assert.True(PassthroughUpscaler.TryPlan(
                        DisplayWidth, DisplayHeight, preset, out UpscalePlan ours));
                    Log(preset + ": vendor " + vendor + " | passthrough " + ours);
                    Assert.Equal(vendor.RenderWidth, ours.RenderWidth);
                    Assert.Equal(vendor.RenderHeight, ours.RenderHeight);
                    Assert.Equal(vendor.Quality, ours.Quality);
                    // Which makes every number derived from the ratio identical too.
                    Assert.Equal(vendor.JitterPhaseCount, ours.JitterPhaseCount);
                    Assert.Equal(vendor.LodBias, ours.LodBias, 4);
                }
            }
            finally
            {
                host.Shutdown();
            }
        }
    }

    // ---------------------------------------------------------------- harness

    /// <summary>
    /// The upscaler slot for the duration of one case, put back exactly as it was.
    /// Every case here writes <c>OptimumConfig</c>'s static slot, which the whole
    /// process shares, so none of them may leave it moved - including the ones that
    /// fail an assert half way through.
    /// </summary>
    private sealed class UpscalerSlot : IDisposable
    {
        private readonly string _upscaler;
        private readonly string _quality;

        public UpscalerSlot(string upscaler, string quality)
        {
            _upscaler = OptimumConfig.Upscaler;
            _quality = OptimumConfig.UpscalerQuality;
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.Upscaler = upscaler;
            OptimumConfig.UpscalerQuality = quality;
        }

        public void Dispose()
        {
            OptimumConfig.Upscaler = _upscaler;
            OptimumConfig.UpscalerQuality = _quality;
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.ClearUpscalerPlan();
        }
    }

    /// <summary>The 4x4 alternating block pattern the DLSS tests use, at the render size.</summary>
    private const int Blocks = 4;

    private static bool Bright(int x, int y, int width, int height) =>
        (x * Blocks / width + y * Blocks / height) % 2 == 0;

    private static unsafe int CreatePattern(VulkanDevice seam, int width, int height)
    {
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
        fixed (byte* data = pixels)
        {
            return seam.CreateUpscaleTexture(width, height, Format.R8G8B8A8Unorm, false, (IntPtr)data, 4);
        }
    }

    private readonly record struct BlockReport(float MinBright, float MaxBright, float MinDark, float MaxDark)
    {
        public override string ToString() =>
            "block interiors: bright " + MinBright.ToString("0.####") + ".." + MaxBright.ToString("0.####") +
            ", dark " + MinDark.ToString("0.####") + ".." + MaxDark.ToString("0.####");
    }

    /// <summary>
    /// The mean of a 32x32 patch at the centre of each block, in 0..1. The patch
    /// interiors are the part magnification leaves alone; the block edges are where
    /// a linear filter blends, and are deliberately not sampled.
    /// </summary>
    private static BlockReport SampleBlocks(byte[] pixels, int width, int height)
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
                        sum += pixels[(y * width + x) * 4 + 1] / 255.0;
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
        Console.Error.WriteLine("[passthrough] " + line);
        Console.Error.Flush();
    }
}
