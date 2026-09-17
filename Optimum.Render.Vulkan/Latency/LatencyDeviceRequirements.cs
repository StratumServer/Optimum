using System;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// What the physical device advertises for latency work, read once at device
/// creation (plan seam S1, "detect without enabling anything that is not used").
/// </summary>
internal readonly struct LatencyDeviceSupport
{
    public LatencyDeviceSupport(
        bool nvLowLatency2, uint nvLowLatency2SpecVersion, bool amdAntiLag, bool presentId, bool presentId2)
    {
        NvLowLatency2 = nvLowLatency2;
        NvLowLatency2SpecVersion = nvLowLatency2SpecVersion;
        AmdAntiLag = amdAntiLag;
        PresentId = presentId;
        PresentId2 = presentId2;
    }

    /// <summary>VK_NV_low_latency2 is advertised.</summary>
    public bool NvLowLatency2 { get; }

    /// <summary>Its advertised revision; 0 when absent.</summary>
    public uint NvLowLatency2SpecVersion { get; }

    /// <summary>VK_AMD_anti_lag is advertised and VkPhysicalDeviceAntiLagFeaturesAMD.antiLag is supported.</summary>
    public bool AmdAntiLag { get; }

    /// <summary>VK_KHR_present_id with its presentId feature.</summary>
    public bool PresentId { get; }

    /// <summary>VK_KHR_present_id2 with its presentId2 feature.</summary>
    public bool PresentId2 { get; }

    public override string ToString() =>
        "nv_low_latency2=" + (NvLowLatency2 ? "rev " + NvLowLatency2SpecVersion : "no") +
        " amd_anti_lag=" + (AmdAntiLag ? "yes" : "no") +
        " present_id=" + (PresentId ? "yes" : "no") +
        " present_id2=" + (PresentId2 ? "yes" : "no");
}

/// <summary>
/// The latency subsystem's device requirements (plan seam S1): detect what the
/// driver offers and enable what the frame-marking foundation uses.
///
/// That is <c>VK_KHR_present_id</c> alone, and only on a presentable device that
/// supports its feature: one id per present, chained as <c>VkPresentIdKHR</c>, is
/// the frame identity carried through to the display, and chaining it is a
/// validation error unless the extension and the feature are both on. The vendor
/// extensions are detected and reported, never enabled - their backends live on
/// <c>feat/latency</c>, which replaces the selection here with its ranked one.
///
/// Extension revisions and feature bits are recorded either way, because the
/// "device up" line reports what the driver offered, not only what was taken.
/// </summary>
internal sealed unsafe class LatencyDeviceRequirements : IDeviceRequirementContributor
{
    public const string NvLowLatency2ExtensionName = "VK_NV_low_latency2";
    public const string AmdAntiLagExtensionName = "VK_AMD_anti_lag";
    public const string PresentIdExtensionName = "VK_KHR_present_id";
    public const string PresentId2ExtensionName = "VK_KHR_present_id2";
    public const string SwapchainExtensionName = "VK_KHR_swapchain";

    public string Name => "latency";

    /// <summary>What the driver advertises, filled in by <see cref="ContributeDeviceRequirements" />.</summary>
    public LatencyDeviceSupport Support { get; private set; }

    /// <summary>The backend chosen for this device; always None on this branch.</summary>
    public LatencyBackendKind Selected => LatencyBackendKind.None;

    /// <summary>VK_KHR_present_id and its feature are enabled.</summary>
    public bool PresentIdEnabled { get; private set; }

    /// <summary>Nothing on the instance: the foundation needs no instance extension.</summary>
    public void ContributeInstanceExtensions(InstanceRequirements requirements)
    {
    }

    public void ContributeDeviceRequirements(DeviceRequirements requirements)
    {
        uint nvRevision = requirements.SpecVersion(NvLowLatency2ExtensionName);
        bool hasAmd = requirements.Has(AmdAntiLagExtensionName);
        bool hasPresentId = requirements.Has(PresentIdExtensionName);
        bool hasPresentId2 = requirements.Has(PresentId2ExtensionName);

        // The feature bits behind those extensions, queried the way the colour-write
        // probe does: one GetPhysicalDeviceFeatures2 over a chain of only the structs
        // whose extension is present, then re-request just what is used.
        var antiLag = new PhysicalDeviceAntiLagFeaturesAMD
        {
            SType = StructureType.PhysicalDeviceAntiLagFeaturesAmd,
        };
        var presentId = new PhysicalDevicePresentIdFeaturesKHR
        {
            SType = StructureType.PhysicalDevicePresentIDFeaturesKhr,
        };
        var presentId2 = new PhysicalDevicePresentId2FeaturesKHR
        {
            SType = StructureType.PhysicalDevicePresentID2FeaturesKhr,
        };

        void* query = null;
        if (hasAmd) { antiLag.PNext = query; query = &antiLag; }
        if (hasPresentId) { presentId.PNext = query; query = &presentId; }
        if (hasPresentId2) { presentId2.PNext = query; query = &presentId2; }
        if (query != null) requirements.QueryFeatures(query);

        Support = new LatencyDeviceSupport(
            nvRevision > 0, nvRevision,
            hasAmd && antiLag.AntiLag,
            hasPresentId && presentId.PresentId,
            hasPresentId2 && presentId2.PresentId2);

        // VK_KHR_present_id hangs off VK_KHR_swapchain: a headless device reports
        // what it found and enables nothing.
        bool presentable = requirements.IsEnabled(SwapchainExtensionName);
        PresentIdEnabled = presentable && Support.PresentId
            && requirements.Request(PresentIdExtensionName, 0, Name);
        if (PresentIdEnabled)
        {
            requirements.ChainFeature(new PhysicalDevicePresentIdFeaturesKHR
            {
                SType = StructureType.PhysicalDevicePresentIDFeaturesKhr,
                PresentId = true,
            });
        }
    }

    /// <summary>The latency token of the "device up" log line.</summary>
    public string Summary() => Summary(Selected, Support, PresentIdEnabled);

    /// <summary>What runs, what the driver offered, and whether present ids are on.</summary>
    public static string Summary(LatencyBackendKind kind, in LatencyDeviceSupport support, bool presentIdEnabled)
    {
        string text = "latency backend " + LatencyBackends.Token(kind);
        if (support.NvLowLatency2) text += " (low_latency2 rev " + support.NvLowLatency2SpecVersion + " available)";
        if (support.AmdAntiLag) text += " (anti_lag available)";
        if (support.PresentId || support.PresentId2)
        {
            text += ", present id " + (presentIdEnabled ? "ON" : "available") +
                (support.PresentId2 ? " (+id2)" : "");
        }
        return text;
    }
}
