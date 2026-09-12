using System;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The sequence the user performed when the client died inside NGX (2026-09-12,
/// pid 98109): a feature is created, the preset is switched, the upscaler is
/// switched off, and then switched on again - on the real driver, in one process.
///
/// <para>What it pins is the lifetime rule. Every one of those steps retires the
/// live feature onto the frame timeline and leaves NGX initialised: the process
/// gets exactly one NGX lifetime, so a settings change that shut NGX down would
/// make DLSS unavailable until the client is restarted - and would then have to be
/// followed by the feature releases the tab's own rebuild does, which is the pair
/// that takes the process down inside the driver. After all four changes
/// <c>NVSDK_NGX_VULKAN_Shutdown1</c> must still not have been called once.</para>
///
/// <para>The crash itself is reproduced by this test run existing: the process's
/// one real <c>Shutdown1</c> happens when the shared <see cref="NgxRuntime" /> is
/// disposed at the end of it. Before the shim fix, every NGX run on this machine
/// died there with SIGSEGV inside <c>libnvidia-ngx.so.1</c> (six of six runs of
/// <c>dotnet test --filter NgxDlssEvaluate</c>), and the test host was reported as
/// crashed rather than passed.</para>
///
/// Skips, never fails, without the shim, the driver library or the NGX feature
/// libraries.
/// </summary>
[Collection(NgxCollection.Name)]
public class UpscalerSettingsChurnTests
{
    private readonly ITestOutputHelper _output;
    private readonly NgxRuntime _ngx;

    public UpscalerSettingsChurnTests(ITestOutputHelper output, NgxRuntime ngx)
    {
        _output = output;
        _ngx = ngx;
    }

    private const int DisplayWidth = 1920;
    private const int DisplayHeight = 1080;

    [SkippableFact]
    public void SwitchingPresetsAndTheUpscalerOffAndOnNeverShutsNgxDown()
    {
        _ngx.Require();
        int mark = _ngx.MessageMark();
        VulkanDevice seam = _ngx.Device;

        int shutdownsBefore = NgxLifetime.ShutdownCalls;
        Assert.True(NgxLifetime.Initialized, "NGX must be up before the sequence starts");

        string upscaler = OptimumConfig.Upscaler;
        string preset = OptimumConfig.UpscalerQuality;
        var host = new DlssUpscaler(Log);
        try
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.UpscalerQuality = "quality";
            Assert.True(host.AdoptSession(_ngx.Session, seam, _ngx.VkDevice));

            // 1. the session as it starts: a feature for the quality preset
            EvaluateOnce(seam, host, "quality");
            Assert.Equal(1, host.FeaturesCreated);
            Assert.True(NgxLifetime.LiveFeatures >= 1);

            // 2. the tab changes the preset: retire, re-plan, create the new one
            ApplySettingsChange(host, "dlss", "performance");
            EvaluateOnce(seam, host, "performance");
            Assert.Equal(2, host.FeaturesCreated);

            // 3. the tab switches the upscaler off: retire, and create nothing
            ApplySettingsChange(host, "off", "performance");
            Assert.Null(host.Feature);
            Assert.False(OptimumConfig.UpscalerReplacesTaa);
            // The host is still alive and still Active across the change - it has to
            // be, because the user may switch back on and NGX cannot be re-initialised.
            Assert.True(host.Active);
            Assert.True(NgxLifetime.Initialized);

            // 4. the tab switches it back on: a new feature on the same NGX lifetime
            ApplySettingsChange(host, "dlss", "quality");
            EvaluateOnce(seam, host, "quality again");
            Assert.Equal(3, host.FeaturesCreated);

            // Nothing in any of that may have ended the NGX lifetime.
            Assert.Equal(shutdownsBefore, NgxLifetime.ShutdownCalls);
            Assert.True(NgxLifetime.Initialized);
            Assert.False(NgxLifetime.Spent);
        }
        finally
        {
            host.Shutdown();
            OptimumConfig.Upscaler = upscaler;
            OptimumConfig.UpscalerQuality = preset;
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.ClearUpscalerPlan();
        }

        // The host's own teardown adopted the session, so it retires and drains but
        // shuts nothing down: the one lifetime belongs to whoever brought NGX up.
        Assert.Equal(shutdownsBefore, NgxLifetime.ShutdownCalls);
        Assert.True(NgxLifetime.Initialized);
        Assert.Equal(host.FeaturesCreated, host.FeaturesRetired);
        GpuTest.AssertCleanSince(seam, mark);
    }

    /// <summary>
    /// What <c>VulkanClientPlatform.ApplyOptimumUpscalerSettings</c> does: the setting
    /// is written first, then the live feature is retired, and the rebuild creates the
    /// replacement lazily on the next frame that evaluates.
    /// </summary>
    private static void ApplySettingsChange(DlssUpscaler host, string upscaler, string preset)
    {
        OptimumConfig.Upscaler = upscaler;
        OptimumConfig.UpscalerQuality = preset;
        host.RetireFeature();
    }

    private void EvaluateOnce(VulkanDevice seam, DlssUpscaler host, string what)
    {
        Assert.True(host.TryPlan(DisplayWidth, DisplayHeight, OptimumConfig.UpscalerQuality,
            out UpscalePlan plan), "no plan for " + what);
        Log("plan for " + what + ": " + plan);

        int color = Upload(seam, plan.RenderWidth, plan.RenderHeight, Format.R8G8B8A8Unorm,
            new byte[plan.RenderWidth * plan.RenderHeight * 4], 4);
        var depths = new float[plan.RenderWidth * plan.RenderHeight];
        Array.Fill(depths, 0.5f);
        int depth = Upload(seam, plan.RenderWidth, plan.RenderHeight, Format.R32Sfloat,
            MemoryMarshal.AsBytes<float>(depths).ToArray(), 4);
        int motion = Upload(seam, plan.RenderWidth, plan.RenderHeight, Format.R16G16Sfloat,
            new byte[plan.RenderWidth * plan.RenderHeight * 4], 4);
        int output = seam.CreateUpscaleTexture(
            plan.DisplayWidth, plan.DisplayHeight, Format.R16G16B16A16Sfloat, storage: true);

        seam.BeginFrame();
        Assert.True(host.EnsureFeature(plan), "no feature for " + what);
        Assert.Equal(NgxResult.Success, host.Evaluate(color, depth, motion, output,
            new NgxDlssEvaluation { Reset = true, MotionVectorScaleX = 1f, MotionVectorScaleY = 1f }));
        seam.Present();
        seam.DrainDeferredDeletions();
    }

    private static unsafe int Upload(
        VulkanDevice seam, int width, int height, Format format, byte[] pixels, int bytesPerPixel)
    {
        fixed (byte* data = pixels)
        {
            return seam.CreateUpscaleTexture(width, height, format, false, (IntPtr)data, bytesPerPixel);
        }
    }

    private void Log(string line) => _output.WriteLine("[churn] " + line);
}
