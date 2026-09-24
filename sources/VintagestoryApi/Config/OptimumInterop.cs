using System;
using System.Collections.Generic;
using System.Text;

namespace Vintagestory.API.Config;

/// <summary>
/// Optimum's provider side of the VS mod interop protocol ("vsmod-interop/1").
///
/// Two projects that replace the same engine code cannot see each other's changes, so the
/// usual outcome is a guessing game: both optimise the same signal, the player finds out by
/// bisecting, and neither log says who won. Where both sides own a runtime switch they can
/// negotiate instead. One side asks the other to stand a feature down, the other decides,
/// and the outcome is visible in <c>.optimum status</c> and in each side's log.
///
/// The protocol is duck-typed on purpose: a provider is one public static class carrying the
/// members below, and a consumer finds it by scanning the loaded assemblies for
/// <see cref="ProtocolId"/> plus the target <see cref="ProviderId"/>. No mod takes a
/// compile-time dependency on another, so each can ship alone, and a peer that does not speak
/// the protocol keeps working through whatever fallback it already has.
///
/// Optimum keeps authority. A request changes the effective runtime state only - never the
/// user's saved optimum.json value - and releasing it restores the configured behaviour. A
/// feature without a runtime switch cannot be negotiated at all and is deliberately not
/// exposed here; this protocol coordinates knobs, it does not declare two builds compatible.
/// </summary>
public static class VsModInterop
{
    /// <summary>
    /// Protocol identifier: a name plus its major version. A consumer that needs a newer
    /// major ignores this provider instead of calling into a shape it does not know.
    /// </summary>
    public const string ProtocolId = "vsmod-interop/1";

    /// <summary>Provider id consumers look up, exposed as a metadata constant.</summary>
    public const string ProviderId = "optimum";

    // Internal call sites use the explicit Value suffix to distinguish this metadata constant.
    public const string ProviderIdValue = ProviderId;

    /// <summary>
    /// Optimum features that a peer may request to stand down while it owns the replacement
    /// runtime path.
    /// </summary>
    public const string FeatureAdaptiveRadius = "optimum.adaptive-radius";

    /// <summary>Separate identity for Optimum's legacy fallback; never used for peer negotiation.</summary>
    public const string RequesterLegacyKometFallback = "optimum:legacy-komet-fallback";

    /// <summary>Stable identity used by Komet when it makes a protocol request.</summary>
    public const string RequesterKomet = "komet";

    /// <summary>Komet's feature that makes Optimum's adaptive radius redundant.</summary>
    public const string ReasonKometInflow = "komet.adaptive-chunk-inflow";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, string>> Yields = new(StringComparer.Ordinal);
    private static volatile int _yieldCount;

    public static int ProtocolVersion => 1;
    public static string ProviderVersion => OptimumConfig.Version;

    /// <summary>Whether this build can negotiate the feature at all.</summary>
    public static bool IsKnownFeature(string featureId) => featureId == FeatureAdaptiveRadius;

    /// <summary>
    /// Whether any requester currently holds this feature down. Called from the radius
    /// controller's tick path, so the common case (nobody asked) is one volatile read.
    /// </summary>
    public static bool IsYielded(string featureId)
    {
        if (_yieldCount == 0) return false;
        lock (Gate)
        {
            return Yields.TryGetValue(featureId, out Dictionary<string, string> byRequester) && byRequester.Count > 0;
        }
    }

    /// <summary>
    /// What a consumer asks before deciding whether it needs to negotiate: is the feature in
    /// force right now. False means "this provider does not know that feature", which is not
    /// the same as "inactive".
    /// </summary>
    public static bool TryGetFeatureState(string featureId, out bool active)
    {
        active = false;
        if (!IsKnownFeature(featureId)) return false;
        active = featureId == FeatureAdaptiveRadius && OptimumConfig.EffectiveAdaptiveRadiusEnabled;
        return true;
    }

    /// <summary>Whether a particular requester currently holds any Optimum feature down.</summary>
    public static bool IsYieldedBy(string requesterId)
    {
        if (string.IsNullOrEmpty(requesterId) || _yieldCount == 0) return false;
        lock (Gate)
        {
            foreach (Dictionary<string, string> byRequester in Yields.Values)
            {
                if (byRequester.ContainsKey(requesterId)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Asks Optimum to stand a feature down for as long as the request is held. Idempotent:
    /// the same requester may repeat it (a tick listener does), and the reason is kept fresh.
    /// A request for an unknown feature or with no requester id is refused, and the caller
    /// gets a reason it can log.
    /// </summary>
    public static bool TryYield(string featureId, string requesterId, string reason, out string detail)
    {
        if (!IsKnownFeature(featureId))
        {
            detail = "unsupported feature: " + featureId;
            return false;
        }

        if (string.IsNullOrEmpty(requesterId))
        {
            detail = "missing requester id";
            return false;
        }

        string effectiveReason = string.IsNullOrEmpty(reason) ? "unspecified" : reason;
        lock (Gate)
        {
            if (!Yields.TryGetValue(featureId, out Dictionary<string, string> byRequester))
            {
                byRequester = new Dictionary<string, string>(StringComparer.Ordinal);
                Yields[featureId] = byRequester;
            }

            bool added = !byRequester.ContainsKey(requesterId);
            byRequester[requesterId] = effectiveReason;
            if (added) _yieldCount++;

            detail = featureId + (added ? " yielded to " : " already yielded to ")
                + requesterId + " (" + effectiveReason + ")";
            return true;
        }
    }

    /// <summary>
    /// Drops one requester's hold. Idempotent, and once the last requester releases, the
    /// feature runs at the user's configured value again on the next tick.
    /// </summary>
    public static void Release(string featureId, string requesterId)
    {
        if (string.IsNullOrEmpty(requesterId)) return;
        lock (Gate)
        {
            if (!Yields.TryGetValue(featureId, out Dictionary<string, string> byRequester)) return;
            if (!byRequester.Remove(requesterId)) return;
            _yieldCount--;
            if (byRequester.Count == 0) Yields.Remove(featureId);
        }
    }

    /// <summary>
    /// Every feature currently held down, for a peer that wants to report what has been
    /// coordinated without knowing Optimum's feature vocabulary in advance.
    /// </summary>
    public static string[] CoordinatedFeatureIds()
    {
        if (_yieldCount == 0) return Array.Empty<string>();
        lock (Gate)
        {
            var ids = new List<string>(Yields.Count);
            foreach (KeyValuePair<string, Dictionary<string, string>> pair in Yields)
            {
                if (pair.Value.Count > 0) ids.Add(pair.Key);
            }

            ids.Sort(StringComparer.Ordinal);
            return ids.ToArray();
        }
    }

    /// <summary>
    /// Provider-side policy: given what a requester currently has running, answer which of
    /// Optimum's features it now holds down for that requester. Reconciling here rather than
    /// accumulating requests keeps the consumer free of bookkeeping - it reports its state and the
    /// provider decides, and calling again with the feature gone hands it back.
    ///
    /// The returned ids are what this requester holds, not what every requester holds, so a
    /// consumer can show the player exactly what its own presence cost.
    /// </summary>
    public static string[] Negotiate(string requesterId, string[] requesterActiveFeatures)
    {
        if (string.IsNullOrEmpty(requesterId)) return Array.Empty<string>();

        if (Contains(requesterActiveFeatures, ReasonKometInflow))
            TryYield(FeatureAdaptiveRadius, requesterId, ReasonKometInflow, out _);
        else
            Release(FeatureAdaptiveRadius, requesterId);

        return HeldBy(requesterId);
    }

    private static bool Contains(string[] values, string value)
    {
        if (values == null) return false;
        for (int i = 0; i < values.Length; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static string[] HeldBy(string requesterId)
    {
        lock (Gate)
        {
            var held = new List<string>();
            foreach (KeyValuePair<string, Dictionary<string, string>> pair in Yields)
            {
                if (pair.Value.ContainsKey(requesterId)) held.Add(pair.Key);
            }

            held.Sort(StringComparer.Ordinal);
            return held.ToArray();
        }
    }

    /// <summary>One line per requester holding this feature down, or an empty string when nobody does.</summary>
    public static string DescribeHold(string featureId)
    {
        lock (Gate)
        {
            if (!Yields.TryGetValue(featureId, out Dictionary<string, string> byRequester) || byRequester.Count == 0)
                return string.Empty;

            var parts = new List<string>(byRequester.Count);
            foreach (KeyValuePair<string, string> request in byRequester)
            {
                parts.Add(request.Key + " (" + request.Value + ")");
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>One line for <c>.optimum status</c> and the startup log.</summary>
    public static string DescribeInterop()
    {
        var sb = new StringBuilder();
        sb.Append(ProviderIdValue).Append(" v").Append(ProviderVersion)
          .Append(" [").Append(ProtocolId).Append("] ")
          .Append(FeatureAdaptiveRadius).Append('=')
          .Append(!OptimumConfig.AdaptiveRadiusEnabled ? "off" : IsYielded(FeatureAdaptiveRadius) ? "yielded" : "on");

        string held = DescribeHold(FeatureAdaptiveRadius);
        sb.Append(held.Length == 0 ? "; no outstanding requests" : "; requested by " + held);
        return sb.ToString();
    }

    /// <summary>Drops every outstanding request. Tests only: the runtime releases per requester.</summary>
    internal static void ResetYieldsForTests()
    {
        lock (Gate)
        {
            Yields.Clear();
            _yieldCount = 0;
        }
    }
}
