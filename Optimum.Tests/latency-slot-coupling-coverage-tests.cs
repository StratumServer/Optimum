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

    /// <summary>
    /// Where the two roadmap streams meet: the passthrough comparison upscaler is
    /// an upscaler with no vendor stack, not "no upscaler". It has to be its own
    /// vendor value, because the two answers differ - "no upscaler" leaves the
    /// device auto order (Reflex on an NVIDIA box) and a vendor-less upscaler
    /// takes Optimum's own pacing, which is what keeps passthrough-vs-DLSS a
    /// comparison of the reconstruction alone.
    /// </summary>
    [Fact]
    public void ThePassthroughUpscalerIsVendorLessRatherThanNoUpscaler()
    {
        string coupling = Read("Optimum.Render.Vulkan/Latency/LatencySlotCoupling.cs");

        // Its own enum value, and the setting token maps to it.
        Assert.Contains("VendorLess = 4,", coupling);
        Assert.Contains("case \"passthrough\":\n                return UpscalerVendor.VendorLess;",
            coupling.Replace("\r\n", "\n"));

        // And it asks for our own pacing, before the vendor-match question is
        // ever reached - there is no vendor to match.
        Assert.Contains(
            "if (upscaler == UpscalerVendor.VendorLess)\n        {\n" +
            "            reason = \"vendor-less upscaler; Optimum's own pacing\";\n" +
            "            return LatencyBackendKind.Native;\n        }",
            coupling.Replace("\r\n", "\n"));

        // "off" keeps the untouched auto order, so the two really are distinct.
        Assert.Contains("reason = \"no upscaler; device auto order\";", coupling);
        Assert.Contains("return null;", coupling);

        // The passthrough token is the one the config actually persists.
        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("UpscalerNames = { \"off\", \"dlss\", \"passthrough\" }", config);
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
