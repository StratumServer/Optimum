using System;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Which latency implementation is active. Exactly one at a time, chosen per
/// present path (plan section "Latency seams"): the Vulkan swapchain path
/// supports all of these; a later D3D12 bridge path supports XeLL only.
/// </summary>
internal enum LatencyBackendKind
{
    /// <summary>No sleeping, no vendor calls; CPU timestamps only. The default.</summary>
    None = 0,

    /// <summary>Completion pacing done in the renderer: wait on the Frame timeline before input, then the cap.</summary>
    Native = 1,

    /// <summary>VK_NV_low_latency2: vkSetLatencySleepModeNV / vkLatencySleepNV / vkSetLatencyMarkerNV.</summary>
    NvLowLatency2 = 2,

    /// <summary>VK_AMD_anti_lag: vkAntiLagUpdateAMD with stage INPUT (the sleep) and stage PRESENT.</summary>
    AmdAntiLag = 3,
}

/// <summary>
/// Selection of the latency backend with an env override that forces a fallback,
/// the same shape as <see cref="DeviceCaps" />'s colour-write tier table
/// (<c>ColorWriteTier.cs</c>): a forced backend the device lacks degrades rather
/// than failing the device.
/// </summary>
internal static class LatencyBackends
{
    /// <summary>auto | off | native | nv | amd.</summary>
    public const string LatencyVariable = "OPTIMUM_VULKAN_LATENCY";

    /// <summary>
    /// Parses an override. Null means "auto": empty, whitespace, the literal
    /// <c>auto</c>, and any unknown value, the last with a note handed to
    /// <paramref name="log" /> (once per parse - this runs at device creation).
    /// </summary>
    public static LatencyBackendKind? ParseBackend(string? value, Action<string>? log = null)
    {
        string token = value == null ? "" : value.Trim().ToLowerInvariant();
        switch (token)
        {
            case "":
            case "auto":
                return null;
            case "off":
            case "none":
            case "0":
                return LatencyBackendKind.None;
            case "native":
            case "completion":
                return LatencyBackendKind.Native;
            case "nv":
            case "nvidia":
            case "reflex":
            case "low_latency2":
                return LatencyBackendKind.NvLowLatency2;
            case "amd":
            case "antilag":
            case "anti-lag":
                return LatencyBackendKind.AmdAntiLag;
            default:
                log?.Invoke(LatencyVariable + "=" + value + " is not one of auto|off|native|nv|amd; using auto");
                return null;
        }
    }

    /// <summary>The override from the environment; null for auto.</summary>
    public static LatencyBackendKind? FromEnvironment(Action<string>? log = null) =>
        ParseBackend(Environment.GetEnvironmentVariable(LatencyVariable), log);

    /// <summary>
    /// The backend to run: the forced one when the device supports it, else the
    /// best one it does support. Auto picks the vendor path when it is there and
    /// falls back to Native, which every vendor and OS supports. Only an
    /// explicit "off" yields <see cref="LatencyBackendKind.None" />.
    /// </summary>
    public static LatencyBackendKind SelectBackend(bool nvLowLatency2, bool amdAntiLag, LatencyBackendKind? forced)
    {
        switch (forced)
        {
            case LatencyBackendKind.None:
                return LatencyBackendKind.None;
            case LatencyBackendKind.Native:
                return LatencyBackendKind.Native;
            case LatencyBackendKind.NvLowLatency2:
                return nvLowLatency2 ? LatencyBackendKind.NvLowLatency2 : LatencyBackendKind.Native;
            case LatencyBackendKind.AmdAntiLag:
                return amdAntiLag ? LatencyBackendKind.AmdAntiLag : LatencyBackendKind.Native;
            default:
                if (nvLowLatency2) return LatencyBackendKind.NvLowLatency2;
                if (amdAntiLag) return LatencyBackendKind.AmdAntiLag;
                return LatencyBackendKind.Native;
        }
    }

    /// <summary>The token written to the "device up" log line and to the stats sample.</summary>
    public static string Token(LatencyBackendKind kind) => kind switch
    {
        LatencyBackendKind.Native => "native",
        LatencyBackendKind.NvLowLatency2 => "nv",
        LatencyBackendKind.AmdAntiLag => "amd",
        _ => "off",
    };

    /// <summary>The token of a parsed override, "auto" for null.</summary>
    public static string Token(LatencyBackendKind? forced) => forced.HasValue ? Token(forced.Value) : "auto";
}
