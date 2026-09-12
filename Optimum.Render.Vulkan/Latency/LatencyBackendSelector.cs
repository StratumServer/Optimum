using System;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Which present path drives the frame's last submission. A latency backend is
/// only valid for the path that drives it (plan seam S8): the vendor sleeps are
/// tied to the swapchain the path presents on, so a path that does not own a
/// Vulkan swapchain cannot host them.
/// </summary>
internal enum LatencyPresentPath
{
    /// <summary>The default <c>BlitPresentPath</c>: owned image blitted into the acquired one.</summary>
    BlitFromOwned = 0,

    /// <summary>The experimental direct path; still a Vulkan swapchain, so the same backends fit.</summary>
    DirectToSwapchain = 1,

    /// <summary>A later Windows D3D12 bridge: Optimum adds no waits there, XeLL owns the cap.</summary>
    D3D12Bridge = 2,
}

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

    /// <summary>
    /// Per-submit attribution (VkLatencySubmissionPresentIdNV) exists from
    /// revision 3; below it the driver attributes by present alone, so submits
    /// are not tagged at all (tagging is all or nothing across a frame).
    /// </summary>
    public bool NvPerSubmitAttribution =>
        NvLowLatency2 && NvLowLatency2SpecVersion >= LatencyBackendSelector.NvPerSubmitAttributionRevision;

    /// <summary>
    /// The NV path needs a present id to attach its markers to; the extension
    /// itself depends on VK_KHR_present_id.
    /// </summary>
    public bool NvUsable => NvLowLatency2 && (PresentId || PresentId2);

    public bool AmdUsable => AmdAntiLag;

    public override string ToString() =>
        "nv_low_latency2=" + (NvLowLatency2 ? "rev " + NvLowLatency2SpecVersion : "no") +
        " amd_anti_lag=" + (AmdAntiLag ? "yes" : "no") +
        " present_id=" + (PresentId ? "yes" : "no") +
        " present_id2=" + (PresentId2 ? "yes" : "no");
}

/// <summary>
/// Ranked selection of the latency backend, the shape
/// <see cref="DeviceCaps.SelectColorWriteTier" /> uses for the colour-write tier:
/// auto takes the best the device really supports, a forced backend the device
/// cannot support degrades to the next one down and says so.
/// </summary>
internal static class LatencyBackendSelector
{
    /// <summary>VkLatencySubmissionPresentIdNV, and therefore per-submit attribution, arrives at revision 3.</summary>
    public const uint NvPerSubmitAttributionRevision = 3;

    public const string NvLowLatency2ExtensionName = "VK_NV_low_latency2";
    public const string AmdAntiLagExtensionName = "VK_AMD_anti_lag";
    public const string PresentIdExtensionName = "VK_KHR_present_id";
    public const string PresentId2ExtensionName = "VK_KHR_present_id2";
    public const string SurfaceCapabilities2ExtensionName = "VK_KHR_get_surface_capabilities2";
    public const string SurfaceExtensionName = "VK_KHR_surface";
    public const string SwapchainExtensionName = "VK_KHR_swapchain";

    /// <summary>
    /// Whether a present path can host a backend. Every Vulkan swapchain path
    /// hosts all of them; the D3D12 bridge hosts none of ours, because XeLL owns
    /// the sleep there.
    /// </summary>
    public static bool Allows(LatencyPresentPath path, LatencyBackendKind kind)
    {
        if (kind == LatencyBackendKind.None) return true;
        return path != LatencyPresentPath.D3D12Bridge;
    }

    /// <summary>The kinds a present path accepts, best first. Used by the tests and the log line.</summary>
    public static LatencyBackendKind[] AllowedBackends(LatencyPresentPath path)
    {
        if (path == LatencyPresentPath.D3D12Bridge) return new[] { LatencyBackendKind.None };
        return new[]
        {
            LatencyBackendKind.NvLowLatency2,
            LatencyBackendKind.AmdAntiLag,
            LatencyBackendKind.Native,
            LatencyBackendKind.None,
        };
    }

    /// <summary>One step down the ladder: NV and AMD fall to Native, Native to None.</summary>
    public static LatencyBackendKind Degrade(LatencyBackendKind kind) => kind switch
    {
        LatencyBackendKind.NvLowLatency2 => LatencyBackendKind.Native,
        LatencyBackendKind.AmdAntiLag => LatencyBackendKind.Native,
        LatencyBackendKind.Native => LatencyBackendKind.None,
        _ => LatencyBackendKind.None,
    };

    /// <summary>
    /// The backend to run on this device and this present path. The vendor rule
    /// is <see cref="LatencyBackends.SelectBackend" /> (auto takes the vendor
    /// path, a forced vendor backend the device lacks falls to Native); this adds
    /// the reason log and the present-path filter.
    /// </summary>
    public static LatencyBackendKind Select(
        in LatencyDeviceSupport support,
        LatencyBackendKind? forced,
        LatencyPresentPath path,
        Action<string>? log = null)
    {
        LatencyBackendKind selected = LatencyBackends.SelectBackend(support.NvUsable, support.AmdUsable, forced);

        if (forced.HasValue && selected != forced.Value)
        {
            log?.Invoke("latency: " + LatencyBackends.Token(forced.Value) + " is not supported on this device (" +
                support + "); using " + LatencyBackends.Token(selected));
        }

        while (!Allows(path, selected))
        {
            LatencyBackendKind lower = Degrade(selected);
            log?.Invoke("latency: " + LatencyBackends.Token(selected) + " is not valid on present path " + path +
                "; using " + LatencyBackends.Token(lower));
            selected = lower;
        }

        return selected;
    }

    /// <summary>
    /// The latency token of the "device up" log line and the stats sample: what
    /// runs, what the driver offered, and whether present ids are on.
    /// </summary>
    public static string Summary(LatencyBackendKind kind, in LatencyDeviceSupport support, bool presentIdEnabled)
    {
        string text = "latency backend " + LatencyBackends.Token(kind);
        if (support.NvLowLatency2) text += " (low_latency2 rev " + support.NvLowLatency2SpecVersion + ")";
        if (support.PresentId || support.PresentId2)
        {
            text += ", present id " + (presentIdEnabled ? "ON" : "available") +
                (support.PresentId2 ? " (+id2)" : "");
        }
        return text;
    }
}

/// <summary>
/// The latency subsystem's device requirements (plan seam S1): detect what the
/// driver has, pick the backend, and enable exactly what that backend needs -
/// nothing else, so a device whose backend is Native or None looks the way it
/// did before this stage existed.
///
/// Extension revisions and feature bits are recorded either way, because the
/// stats line, the "device up" line and the acceptance numbers all report what
/// the driver offered, not only what was taken.
/// </summary>
internal sealed unsafe class LatencyDeviceRequirements : IDeviceRequirementContributor
{
    private readonly LatencyBackendKind? _forced;
    private readonly LatencyPresentPath _path;
    private readonly Action<string>? _log;

    public LatencyDeviceRequirements(
        LatencyBackendKind? forced, LatencyPresentPath path = LatencyPresentPath.BlitFromOwned, Action<string>? log = null)
    {
        _forced = forced;
        _path = path;
        _log = log;
    }

    public string Name => "latency";

    /// <summary>What the driver advertises, filled in by <see cref="ContributeDeviceRequirements" />.</summary>
    public LatencyDeviceSupport Support { get; private set; }

    /// <summary>The backend chosen for this device and present path.</summary>
    public LatencyBackendKind Selected { get; private set; } = LatencyBackendKind.None;

    /// <summary>Whether VK_KHR_get_surface_capabilities2 is on the instance.</summary>
    public bool SurfaceCapabilities2Enabled { get; private set; }

    public bool NvLowLatency2Enabled { get; private set; }
    public bool AmdAntiLagEnabled { get; private set; }
    public bool PresentIdEnabled { get; private set; }
    public bool PresentId2Enabled { get; private set; }

    public void ContributeInstanceExtensions(InstanceRequirements requirements)
    {
        // Needed later for vkGetPhysicalDeviceSurfaceCapabilities2KHR with
        // VkLatencySurfaceCapabilitiesNV chained (seam S5). It depends on
        // VK_KHR_surface, so a headless instance never asks for it, and a loader
        // without it is not an error: the query falls back to the 1.0 form.
        if (!requirements.IsEnabled(LatencyBackendSelector.SurfaceExtensionName)) return;
        SurfaceCapabilities2Enabled =
            requirements.Request(LatencyBackendSelector.SurfaceCapabilities2ExtensionName, Name);
    }

    public void ContributeDeviceRequirements(DeviceRequirements requirements)
    {
        uint nvRevision = requirements.SpecVersion(LatencyBackendSelector.NvLowLatency2ExtensionName);
        bool hasNv = nvRevision > 0;
        bool hasAmd = requirements.Has(LatencyBackendSelector.AmdAntiLagExtensionName);
        bool hasPresentId = requirements.Has(LatencyBackendSelector.PresentIdExtensionName);
        bool hasPresentId2 = requirements.Has(LatencyBackendSelector.PresentId2ExtensionName);

        // The feature bits behind those extensions, queried the way the
        // colour-write probe does: one GetPhysicalDeviceFeatures2 over a chain of
        // only the structs whose extension is present, then re-request just what
        // is used.
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
            hasNv, nvRevision,
            hasAmd && antiLag.AntiLag,
            hasPresentId && presentId.PresentId,
            hasPresentId2 && presentId2.PresentId2);

        // Without a swapchain there is nothing to sleep against: VK_NV_low_latency2
        // and the present-id extensions all hang off VK_KHR_swapchain, so a
        // headless device reports what it found and runs Native.
        bool presentable = requirements.IsEnabled(LatencyBackendSelector.SwapchainExtensionName);
        LatencyDeviceSupport usable = presentable
            ? Support
            : new LatencyDeviceSupport(false, Support.NvLowLatency2SpecVersion, Support.AmdAntiLag, false, false);

        Selected = LatencyBackendSelector.Select(usable, _forced, _path, _log);

        switch (Selected)
        {
            case LatencyBackendKind.NvLowLatency2:
                // VK_NV_low_latency2 depends on VK_KHR_present_id, whose feature
                // has to be requested for the chained VkPresentIdKHR to count.
                PresentIdEnabled = usable.PresentId
                    && requirements.Request(LatencyBackendSelector.PresentIdExtensionName, 0, Name);
                if (PresentIdEnabled)
                {
                    requirements.ChainFeature(new PhysicalDevicePresentIdFeaturesKHR
                    {
                        SType = StructureType.PhysicalDevicePresentIDFeaturesKhr,
                        PresentId = true,
                    });
                }
                NvLowLatency2Enabled = PresentIdEnabled
                    && requirements.Request(LatencyBackendSelector.NvLowLatency2ExtensionName, 0, Name);
                if (!NvLowLatency2Enabled)
                {
                    // The extension vanished between the query and the request:
                    // degrade rather than create a device the backend cannot use.
                    _log?.Invoke("latency: " + LatencyBackendSelector.NvLowLatency2ExtensionName +
                        " could not be enabled; using " + LatencyBackends.Token(LatencyBackendKind.Native));
                    Selected = LatencyBackendKind.Native;
                }
                break;

            case LatencyBackendKind.AmdAntiLag:
                AmdAntiLagEnabled = requirements.Request(LatencyBackendSelector.AmdAntiLagExtensionName, 0, Name);
                if (AmdAntiLagEnabled)
                {
                    requirements.ChainFeature(new PhysicalDeviceAntiLagFeaturesAMD
                    {
                        SType = StructureType.PhysicalDeviceAntiLagFeaturesAmd,
                        AntiLag = true,
                    });
                }
                else
                {
                    _log?.Invoke("latency: " + LatencyBackendSelector.AmdAntiLagExtensionName +
                        " could not be enabled; using " + LatencyBackends.Token(LatencyBackendKind.Native));
                    Selected = LatencyBackendKind.Native;
                }
                break;
        }
    }

    /// <summary>The token for the "device up" log line and the stats sample.</summary>
    public string Summary() => LatencyBackendSelector.Summary(Selected, Support, PresentIdEnabled);
}
