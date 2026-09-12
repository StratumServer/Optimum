using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// PR #3, item A: the size the frame buffers are allocated at, across every
/// settings change the upscaling tab can make, against the real driver's own
/// optimal-settings query.
///
/// The bug: switching the upscaler off rebuilds every framebuffer, and the
/// rebuild asked the upscaler <i>host</i> what to render at. The host is still
/// alive and active at that moment - it has to be, NGX allows one lifetime per
/// process and the player may switch back on - so Primary came back at the
/// vendor's reduced size (1707x993 at Quality on a 2560x1490 display) and the
/// in-house TAA resolve then resolved a reduced-resolution frame into a
/// display-resolution chain. DLAA hid it: its ratio is 1.
///
/// What is asserted here is the allocator's own input, taken through the same
/// entry point <c>VulkanClientPlatform.OptimumTryPlanUpscaleRenderSize</c> calls
/// (<c>DlssUpscaler.TryPlanForFrame</c>), immediately after each change and with
/// the host deliberately left up:
/// <list type="bullet">
/// <item>from every preset to off: the display size exactly, which is what the
/// client computes as window size x SSAA and allocates when no upscaler was ever
/// enabled;</item>
/// <item>off to every preset: the vendor's size for that preset again;</item>
/// <item>preset to preset: the new preset's size, never the old one's.</item>
/// </list>
///
/// Skips, never fails, without the shim, the driver library or the NGX feature
/// libraries.
/// </summary>
[Collection(NgxCollection.Name)]
public class UpscalerRenderSizeSwitchTests
{
    private readonly ITestOutputHelper _output;
    private readonly NgxRuntime _ngx;

    public UpscalerRenderSizeSwitchTests(ITestOutputHelper output, NgxRuntime ngx)
    {
        _output = output;
        _ngx = ngx;
    }

    /// <summary>The display size for a 2560x1490 window at SSAA 1.</summary>
    private const int DisplayWidth = 2560;

    private const int DisplayHeight = 1490;

    private static readonly string[] Presets =
    {
        "dlaa", "quality", "balanced", "performance", "ultraperformance",
    };

    [SkippableFact]
    public void SwitchingTheUpscalerOffPutsTheRenderSizeBackToTheDisplaySize()
    {
        _ngx.Require();
        VulkanDevice seam = _ngx.Device;

        string upscaler = OptimumConfig.Upscaler;
        string preset = OptimumConfig.UpscalerQuality;
        var host = new DlssUpscaler(Log);
        try
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            Assert.True(host.AdoptSession(_ngx.Session, seam, _ngx.VkDevice));
            Assert.True(host.Active);

            var planned = new Dictionary<string, (int Width, int Height)>();
            foreach (string quality in Presets)
            {
                // --- the tab turns the upscaler on at this preset -------------
                OptimumConfig.Upscaler = "dlss";
                OptimumConfig.UpscalerQuality = quality;
                Assert.True(
                    DlssUpscaler.TryPlanForFrame(
                        host, DisplayWidth, DisplayHeight, out int onWidth, out int onHeight, out UpscalePlan plan),
                    "no plan at " + quality);
                Log(quality + ": " + plan);
                planned[quality] = (onWidth, onHeight);

                Assert.Equal(plan.RenderWidth, onWidth);
                Assert.Equal(plan.RenderHeight, onHeight);
                Assert.True(onWidth <= DisplayWidth && onHeight <= DisplayHeight);
                if (!string.Equals(quality, "dlaa", StringComparison.Ordinal))
                {
                    // Every preset but DLAA really renders below the display size -
                    // which is exactly why a leftover of it is visible as softness.
                    Assert.True(onWidth < DisplayWidth,
                        quality + " must render below the display width: " + onWidth);
                }

                // --- the tab turns it off -------------------------------------
                // The whole point: the host stays up and active across this, and the
                // live feature has been retired by ApplyOptimumUpscalerSettings, which
                // is the state the rebuild runs in.
                OptimumConfig.Upscaler = "off";
                host.RetireFeature();
                Assert.True(host.Active, "the host stays up across a switch-off; that is the trap");

                Assert.False(
                    DlssUpscaler.TryPlanForFrame(
                        host, DisplayWidth, DisplayHeight, out int offWidth, out int offHeight, out _),
                    "nothing may be planned with the upscaler off (" + quality + ")");
                Assert.Equal(DisplayWidth, offWidth);
                Assert.Equal(DisplayHeight, offHeight);
                // And the frame really has handed the resolve back to TAA.
                Assert.False(OptimumConfig.UpscalerReplacesTaa);
            }

            // --- off to a preset, and preset to preset ------------------------
            // The reverse direction answers the vendor's size again, and a preset
            // change answers the new preset's, never the previous one's.
            string previous = null;
            foreach (string quality in Presets)
            {
                OptimumConfig.Upscaler = "dlss";
                OptimumConfig.UpscalerQuality = quality;
                Assert.True(DlssUpscaler.TryPlanForFrame(
                    host, DisplayWidth, DisplayHeight, out int width, out int height, out _));
                Assert.Equal(planned[quality].Width, width);
                Assert.Equal(planned[quality].Height, height);
                if (previous != null && planned[previous].Width != planned[quality].Width)
                {
                    Assert.NotEqual(planned[previous].Width, width);
                }
                previous = quality;
            }

            // A stand-down at runtime is the same answer as the switch: the
            // effective setting is what decides, not the host.
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.UpscalerQuality = "performance";
            Assert.True(OptimumConfig.DisableUpscalerAtRuntime());
            Assert.True(host.Active);
            Assert.False(DlssUpscaler.TryPlanForFrame(
                host, DisplayWidth, DisplayHeight, out int downWidth, out int downHeight, out _));
            Assert.Equal(DisplayWidth, downWidth);
            Assert.Equal(DisplayHeight, downHeight);
        }
        finally
        {
            host.Shutdown();
            OptimumConfig.Upscaler = upscaler;
            OptimumConfig.UpscalerQuality = preset;
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.ClearUpscalerPlan();
        }
    }

    /// <summary>
    /// The other half of the same rule, and the only one that needs no driver:
    /// with no host at all - the session that started with the setting off - the
    /// answer is the display size, whatever the setting says.
    /// </summary>
    [Fact]
    public void WithNoHostTheAnswerIsAlwaysTheDisplaySize()
    {
        string upscaler = OptimumConfig.Upscaler;
        string preset = OptimumConfig.UpscalerQuality;
        try
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            foreach (string quality in Presets)
            {
                OptimumConfig.Upscaler = "dlss";
                OptimumConfig.UpscalerQuality = quality;
                Assert.False(DlssUpscaler.TryPlanForFrame(
                    null, DisplayWidth, DisplayHeight, out int width, out int height, out _));
                Assert.Equal(DisplayWidth, width);
                Assert.Equal(DisplayHeight, height);
            }
        }
        finally
        {
            OptimumConfig.Upscaler = upscaler;
            OptimumConfig.UpscalerQuality = preset;
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
        }
    }

    private void Log(string line) => _output.WriteLine(line);
}
