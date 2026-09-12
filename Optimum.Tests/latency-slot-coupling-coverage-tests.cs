using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The slot coupling at source level (plan, "The slots are coupled", user
/// 2026-09-12): the latency selection takes the active upscaler's vendor as an
/// input, the client feeds it the real setting, and the GPU vendor comes from the
/// device properties. The decision table itself is proven in
/// Optimum.Render.Vulkan.Tests/LatencySlotCouplingTests.cs; this is the wiring,
/// which no unit test can see once someone drops the argument at the call site.
/// </summary>
public class LatencySlotCouplingCoverageTests
{
    [Fact]
    public void TheSelectorTakesBothVendorsAndLogsTheDecision()
    {
        string coupling = Read("Optimum.Render.Vulkan/Latency/LatencySlotCoupling.cs");
        Assert.Contains("public static LatencyBackendKind? Requested(", coupling);
        Assert.Contains("UpscalerVendor upscaler, GpuVendor gpu, LatencyPresentPath path, out string reason", coupling);
        // The GPU half is the vendor id from VkPhysicalDeviceProperties.
        Assert.Contains("public const uint NvidiaVendorId = 0x10DE;", coupling);
        Assert.Contains("public const uint AmdVendorId = 0x1002;", coupling);
        Assert.Contains("public const uint IntelVendorId = 0x8086;", coupling);
        // One line naming the pair and the decision.
        Assert.Contains("\"latency: upscaler \" + UpscalerVendors.Token(upscaler) +", coupling);

        string selector = Read("Optimum.Render.Vulkan/Latency/LatencyBackendSelector.cs");
        Assert.Contains("UpscalerVendor upscaler,", selector);
        Assert.Contains("GpuVendor gpu,", selector);
        Assert.Contains("LatencySlotCoupling.Requested(upscaler, gpu, path, out string reason)", selector);
        // OPTIMUM_VULKAN_LATENCY wins over the coupling.
        Assert.Contains("LatencyBackendKind? request = forced ?? coupled;", selector);
        Assert.Contains("LatencySlotCoupling.DecisionLine(upscaler, gpu, selected, reason)", selector);
    }

    [Fact]
    public void TheClientFeedsTheSelectionTheActiveUpscalerAndTheDeviceVendor()
    {
        string device = Read("Optimum.Render.Vulkan/VulkanDevice.cs");
        Assert.Contains(
            "UpscalerVendor = UpscalerVendors.FromSettingToken(OptimumConfig.EffectiveUpscaler),", device);

        string context = Read("Optimum.Render.Vulkan/Core/VulkanContext.cs");
        Assert.Contains("public UpscalerVendor UpscalerVendor = UpscalerVendor.None;", context);
        Assert.Contains("options.UpscalerVendor,", context);
        // The vendor the coupling matches against is a device property.
        Assert.Contains("VendorId = properties.VendorID,", context);
        Assert.Contains("Vendor = GpuVendors.FromVendorId(properties.VendorID),", context);
    }

    private static string Read(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "VintageStory.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!, relativePath));
    }
}
