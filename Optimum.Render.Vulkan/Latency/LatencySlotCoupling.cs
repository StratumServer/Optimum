using System;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The GPU's vendor, read from <c>VkPhysicalDeviceProperties.vendorID</c>. Only
/// the three that own a latency stack are named; everything else is
/// <see cref="Unknown" /> and is treated as a vendor mismatch, i.e. Optimum's own
/// pacing.
/// </summary>
internal enum GpuVendor
{
    Unknown = 0,
    Nvidia = 1,
    Amd = 2,
    Intel = 3,
}

/// <summary>
/// The vendor of the upscaler that is running. This is the upscaler slot's half
/// of the coupling (plan, "The slots are coupled", 2026-09-12): DLSS and DLSS-G
/// are NVIDIA's, FSR is AMD's, XeSS and XeFG are Intel's.
///
/// <see cref="None" /> means no upscaler at all, and only that: nothing owns the
/// resolve, nothing constrains the latency slot, and the device-based auto order
/// runs exactly as it did before the coupling existed.
///
/// <see cref="VendorLess" /> is an upscaler that owns the resolve but has no
/// vendor latency stack behind it - Optimum's own passthrough comparison
/// upscaler. It is not the same as "no upscaler": a vendor latency backend is
/// specified and tested only against its own vendor's upscaler, so a vendor-less
/// upscaler is the same mismatch as a cross-vendor pair and takes Optimum's own
/// pacing. Running Reflex behind the passthrough resolve on an NVIDIA box would
/// also make the passthrough-vs-DLSS comparison differ in the latency stack as
/// well as the reconstruction, which is the one thing that slot exists to hold
/// still.
/// </summary>
internal enum UpscalerVendor
{
    None = 0,
    Nvidia = 1,
    Amd = 2,
    Intel = 3,
    VendorLess = 4,
}

/// <summary>Vendor ids and tokens for <see cref="GpuVendor" />.</summary>
internal static class GpuVendors
{
    public const uint NvidiaVendorId = 0x10DE;
    public const uint AmdVendorId = 0x1002;
    /// <summary>AMD's second PCI id, used by some APUs.</summary>
    public const uint AmdSecondaryVendorId = 0x1022;
    public const uint IntelVendorId = 0x8086;

    /// <summary>The vendor behind a <c>VkPhysicalDeviceProperties.vendorID</c>.</summary>
    public static GpuVendor FromVendorId(uint vendorId) => vendorId switch
    {
        NvidiaVendorId => GpuVendor.Nvidia,
        AmdVendorId => GpuVendor.Amd,
        AmdSecondaryVendorId => GpuVendor.Amd,
        IntelVendorId => GpuVendor.Intel,
        _ => GpuVendor.Unknown,
    };

    /// <summary>The token used in the decision log line.</summary>
    public static string Token(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "nvidia",
        GpuVendor.Amd => "amd",
        GpuVendor.Intel => "intel",
        _ => "unknown",
    };
}

/// <summary>Tokens and the setting mapping for <see cref="UpscalerVendor" />.</summary>
internal static class UpscalerVendors
{
    /// <summary>
    /// The vendor behind an <c>OptimumConfig.EffectiveUpscaler</c> token.
    /// "off" is <see cref="UpscalerVendor.None" /> and leaves the latency slot to
    /// the device-based auto order; Optimum's own "passthrough" is an upscaler
    /// with no vendor stack behind it, so it is
    /// <see cref="UpscalerVendor.VendorLess" /> and takes our own pacing.
    /// </summary>
    public static UpscalerVendor FromSettingToken(string? upscaler)
    {
        string token = upscaler == null ? "" : upscaler.Trim().ToLowerInvariant();
        switch (token)
        {
            case "passthrough":
                return UpscalerVendor.VendorLess;
            case "dlss":
            case "dlssg":
            case "dlss-g":
            case "dlss_g":
                return UpscalerVendor.Nvidia;
            case "fsr":
            case "fsr3":
            case "fsr4":
                return UpscalerVendor.Amd;
            case "xess":
            case "xefg":
            case "xess-fg":
                return UpscalerVendor.Intel;
            default:
                return UpscalerVendor.None;
        }
    }

    /// <summary>The token used in the decision log line.</summary>
    public static string Token(UpscalerVendor vendor) => vendor switch
    {
        UpscalerVendor.Nvidia => "nvidia",
        UpscalerVendor.Amd => "amd",
        UpscalerVendor.Intel => "intel",
        UpscalerVendor.VendorLess => "vendorless",
        _ => "none",
    };
}

/// <summary>
/// The slot coupling (plan, "The slots are coupled: a vendor latency backend only
/// when the upscaler's vendor matches the GPU, else our own", user 2026-09-12):
///
/// <list type="table">
/// <item><term>DLSS / DLSS-G on NVIDIA</term><description>Reflex, VK_NV_low_latency2</description></item>
/// <item><term>FSR on AMD</term><description>VK_AMD_anti_lag</description></item>
/// <item><term>XeSS (+ XeFG) on Intel</term><description>XeLL, but only on the Windows D3D12 bridge; on Vulkan the pacing is ours</description></item>
/// <item><term>any cross-vendor pair</term><description>Optimum's own completion pacing (Native)</description></item>
/// <item><term>a vendor-less upscaler (passthrough)</term><description>Optimum's own completion pacing (Native)</description></item>
/// <item><term>no upscaler at all</term><description>the device-based auto order: NV, AMD, Native</description></item>
/// </list>
///
/// The vendor stacks are only specified and tested against their own upscaler;
/// mixing them risks the frame-attribution and pacing model each one builds, so a
/// mismatch takes Optimum's own pacing rather than the other vendor's.
/// </summary>
internal static class LatencySlotCoupling
{
    /// <summary>
    /// What the active upscaler's vendor demands of the latency slot: the vendor
    /// backend on a match, <see cref="LatencyBackendKind.Native" /> on a mismatch,
    /// and null when no vendor upscaler is running (the device-based auto order).
    /// <paramref name="reason" /> is the human half of the decision line and is
    /// never null.
    /// </summary>
    public static LatencyBackendKind? Requested(
        UpscalerVendor upscaler, GpuVendor gpu, LatencyPresentPath path, out string reason)
    {
        if (upscaler == UpscalerVendor.None)
        {
            reason = "no upscaler; device auto order";
            return null;
        }

        if (upscaler == UpscalerVendor.VendorLess)
        {
            reason = "vendor-less upscaler; Optimum's own pacing";
            return LatencyBackendKind.Native;
        }

        if (!Matches(upscaler, gpu))
        {
            reason = "cross-vendor pair; Optimum's own pacing";
            return LatencyBackendKind.Native;
        }

        switch (upscaler)
        {
            case UpscalerVendor.Nvidia:
                reason = "vendor match; Reflex (" + LatencyBackendSelector.NvLowLatency2ExtensionName + ")";
                return LatencyBackendKind.NvLowLatency2;

            case UpscalerVendor.Amd:
                reason = "vendor match; " + LatencyBackendSelector.AmdAntiLagExtensionName;
                return LatencyBackendKind.AmdAntiLag;

            default:
                // XeLL is the Intel match, and it lives on the Windows D3D12
                // bridge only: that path hosts none of our backends (XeLL owns
                // the sleep there), and on every Vulkan path the pacing is ours.
                reason = path == LatencyPresentPath.D3D12Bridge
                    ? "vendor match; XeLL owns the sleep on the D3D12 bridge"
                    : "vendor match, but XeLL is D3D12-bridge only; Optimum's own pacing";
                return path == LatencyPresentPath.D3D12Bridge
                    ? LatencyBackendKind.None
                    : LatencyBackendKind.Native;
        }
    }

    /// <summary>Whether the upscaler's vendor is the vendor of the GPU it runs on.</summary>
    public static bool Matches(UpscalerVendor upscaler, GpuVendor gpu) => upscaler switch
    {
        UpscalerVendor.Nvidia => gpu == GpuVendor.Nvidia,
        UpscalerVendor.Amd => gpu == GpuVendor.Amd,
        UpscalerVendor.Intel => gpu == GpuVendor.Intel,
        _ => false,
    };

    /// <summary>
    /// The one line the selection logs: the pair it saw and the decision it made.
    /// </summary>
    public static string DecisionLine(
        UpscalerVendor upscaler,
        GpuVendor gpu,
        LatencyBackendKind selected,
        string reason)
    {
        return "latency: upscaler " + UpscalerVendors.Token(upscaler) +
            " on " + GpuVendors.Token(gpu) + " gpu -> " + LatencyBackends.Token(selected) +
            " (" + reason + ")";
    }
}
