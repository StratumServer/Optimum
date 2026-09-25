using Komet.Runtime;

namespace Komet.Interop;

/// <summary>
/// Test-only stand-in for the provider Komet gains from the interop protocol: same namespace,
/// type name and member shape as the real one, answering from the same <see cref="InflowBrake"/>
/// flag. It lets Optimum's consumer side be exercised without Komet in the process, and the two
/// switches below let a test pin which source Optimum actually used.
/// </summary>
public static class KometInterop
{
    public const string ProtocolId = "vsmod-interop/1";
    public const string ProviderId = "komet";
    public const string FeatureAdaptiveChunkInflow = "komet.adaptive-chunk-inflow";

    /// <summary>False plays an older Komet that does not know the feature, so Optimum must fall back.</summary>
    public static bool AnswersFeature = true;

    /// <summary>Pins the reported state so a test can make the protocol and the reflected field disagree.</summary>
    public static bool? OverrideState;

    public static bool TryGetFeatureState(string featureId, out bool active)
    {
        active = false;
        if (!AnswersFeature || featureId != FeatureAdaptiveChunkInflow) return false;
        active = OverrideState ?? InflowBrake.Enabled;
        return true;
    }

    public static string[] Negotiate(string requesterId, string[] requesterActiveFeatures)
        => System.Array.Empty<string>();
}
