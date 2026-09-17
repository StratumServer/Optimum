namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Which latency implementation is active. Exactly one at a time, chosen per
/// present path (plan section "Latency seams").
///
/// This branch carries the frame-marking foundation only (frame identity, markers,
/// present ids, the stats line), so <see cref="None" /> is the one implementation
/// that exists here. The other kinds are the pacing backends of <c>feat/latency</c>;
/// they are named so the stats and log tokens, and the selection that lands with
/// them, keep one vocabulary across both branches.
/// </summary>
internal enum LatencyBackendKind
{
    /// <summary>No sleeping, no vendor calls; CPU timestamps only. The default.</summary>
    None = 0,

    /// <summary>Completion pacing done in the renderer (<c>feat/latency</c>).</summary>
    Native = 1,

    /// <summary>VK_NV_low_latency2 (<c>feat/latency</c>).</summary>
    NvLowLatency2 = 2,

    /// <summary>VK_AMD_anti_lag (<c>feat/latency</c>).</summary>
    AmdAntiLag = 3,
}

/// <summary>The tokens a backend kind is written as, in the "device up" line and the stats sample.</summary>
internal static class LatencyBackends
{
    public static string Token(LatencyBackendKind kind) => kind switch
    {
        LatencyBackendKind.Native => "native",
        LatencyBackendKind.NvLowLatency2 => "nv",
        LatencyBackendKind.AmdAntiLag => "amd",
        _ => "off",
    };
}
