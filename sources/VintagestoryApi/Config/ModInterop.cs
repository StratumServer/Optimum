using System;
using System.Collections.Generic;
using System.Reflection;

namespace Vintagestory.API.Config;

/// <summary>
/// Consumer side of the VS mod interop protocol ("vsmod-interop/1"): the duck typing that lets
/// Optimum ask a peer to stand a feature down without referencing that peer's assembly.
///
/// A provider is a public static class carrying <c>ProtocolId</c> (a const string),
/// <c>ProviderId</c> (a const string) and some subset of the methods below. Optimum exposes the
/// provider half itself (see <see cref="VsModInterop"/>) and consumes Komet's, which is why
/// both directions exist in one codebase.
///
/// Lookups are cached per provider id: the first call happens at client start, after every mod
/// is loaded, so one scan covers the session. A provider that is missing, speaks another
/// protocol, or throws answers nothing - each method returns false or an empty array and the
/// caller keeps the fallback it had before.
///
/// The same lookup is duplicated inside each participating mod on purpose. A shared contract
/// assembly would hand every mod a reference to every other, which is exactly the coupling the
/// protocol is meant to avoid.
/// </summary>
internal static class ModInterop
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Peer> Peers = new(StringComparer.Ordinal);

    private sealed class Peer
    {
        public MethodInfo? TryGetFeatureState;
        public MethodInfo? Negotiate;
        public MethodInfo? TryYield;
        public MethodInfo? Release;
        public MethodInfo? CoordinatedFeatureIds;
    }

    /// <summary>The live state of a feature the provider owns. False means "that provider does not know the feature".</summary>
    public static bool TryGetFeatureState(string providerId, string featureId, out bool active)
    {
        active = false;
        Peer? peer = Resolve(providerId);
        if (peer?.TryGetFeatureState == null) return false;

        object?[] args = { featureId, false };
        try
        {
            if (peer.TryGetFeatureState.Invoke(null, args) is not true) return false;
            active = args[1] is true;
            return true;
        }
        catch (Exception)
        {
            // A provider that throws mid-negotiation answers nothing; the caller keeps its fallback.
            return false;
        }
    }

    /// <summary>
    /// Tells a provider what this side has running and gets back the feature ids it holds down
    /// for that set. Empty when the provider does not answer.
    /// </summary>
    public static string[] Negotiate(string providerId, string requesterId, string[] requesterActiveFeatures)
    {
        Peer? peer = Resolve(providerId);
        if (peer?.Negotiate == null) return Array.Empty<string>();

        object?[] args = { requesterId, requesterActiveFeatures };
        try
        {
            return peer.Negotiate.Invoke(null, args) as string[] ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Asks for one named feature to be held down, for a consumer that knows exactly what it wants.</summary>
    public static bool TryRequestYield(string providerId, string featureId, string requesterId, string reason, out string? detail)
    {
        detail = null;
        Peer? peer = Resolve(providerId);
        if (peer?.TryYield == null) return false;

        object?[] args = { featureId, requesterId, reason, null };
        try
        {
            if (peer.TryYield.Invoke(null, args) is not true) return false;
            detail = args[3] as string;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Drops an earlier request. Safe to call when nothing was requested.</summary>
    public static void Release(string providerId, string featureId, string requesterId)
    {
        Peer? peer = Resolve(providerId);
        if (peer?.Release == null) return;

        try
        {
            peer.Release.Invoke(null, new object?[] { featureId, requesterId });
        }
        catch (Exception)
        {
            // Nothing to do: the provider owns its state either way.
        }
    }

    /// <summary>Everything the provider currently holds down, whoever asked for it.</summary>
    public static string[] CoordinatedFeatureIds(string providerId)
    {
        Peer? peer = Resolve(providerId);
        if (peer?.CoordinatedFeatureIds == null) return Array.Empty<string>();

        try
        {
            return peer.CoordinatedFeatureIds.Invoke(null, null) as string[] ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Whether a peer exposes the required feature-state entry point for this protocol.</summary>
    public static bool IsProviderAvailable(string providerId)
    {
        Peer? peer = Resolve(providerId);
        return peer?.TryGetFeatureState != null && peer.Negotiate != null;
    }

    /// <summary>Forgets the cached lookups. Tests only: the cache is per session on purpose.</summary>
    internal static void ResetCacheForTests()
    {
        lock (Gate)
        {
            Peers.Clear();
        }
    }

    private static Peer? Resolve(string providerId)
    {
        if (string.IsNullOrEmpty(providerId)) return null;

        lock (Gate)
        {
            if (Peers.TryGetValue(providerId, out Peer? cached)) return cached;
        }

        Peer? peer = Find(providerId);
        lock (Gate)
        {
            Peers[providerId] = peer;
        }

        return peer;
    }

    private static Peer? Find(string providerId)
    {
        try
        {
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!TryGetTypes(assembly, out Type[]? types)) continue;
                for (int i = 0; i < types!.Length; i++)
                {
                    Type type = types[i];
                    if (ConstString(type, "ProtocolId") != VsModInterop.ProtocolId) continue;
                    if (ConstString(type, "ProviderId") != providerId) continue;
                    return Describe(type);
                }
            }
        }
        catch (Exception)
        {
            // A scan that cannot finish leaves every provider unresolved; callers fall back.
        }

        return null;
    }

    private static Peer Describe(Type type)
    {
        return new Peer
        {
            TryGetFeatureState = FindMethod(type, "TryGetFeatureState", typeof(bool), typeof(string), typeof(bool).MakeByRefType()),
            Negotiate = FindMethod(type, "Negotiate", typeof(string[]), typeof(string), typeof(string[])),
            TryYield = FindMethod(type, "TryYield", typeof(bool), typeof(string), typeof(string), typeof(string), typeof(string).MakeByRefType()),
            Release = FindMethod(type, "Release", typeof(void), typeof(string), typeof(string)),
            CoordinatedFeatureIds = FindMethod(type, "CoordinatedFeatureIds", typeof(string[])),
        };
    }

    private static MethodInfo? FindMethod(Type type, string name, Type returnType, params Type[] parameterTypes)
    {
        try
        {
            MethodInfo? method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameterTypes, null);
            return method?.ReturnType == returnType ? method : null;
        }
        catch (AmbiguousMatchException)
        {
            return null;
        }
    }

    private static bool TryGetTypes(System.Reflection.Assembly assembly, out Type[]? types)
    {
        try
        {
            types = assembly.GetTypes();
            return true;
        }
        catch (Exception)
        {
            // A partly loadable assembly is not a provider.
            types = null;
            return false;
        }
    }

    /// <summary>
    /// Reads the protocol constants. Both are const strings, so a peer is identified without
    /// running any of its code - which is what makes the lookup safe at startup.
    /// </summary>
    private static string? ConstString(Type type, string name)
    {
        try
        {
            return type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
