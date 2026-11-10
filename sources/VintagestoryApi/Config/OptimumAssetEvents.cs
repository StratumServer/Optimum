using System;

namespace Vintagestory.API.Config;

/// <summary>
/// Provides global lifecycle notifications for runtime asset updates, allowing
/// network caches and asset lookups to invalidate when mods inject assets dynamically.
/// </summary>
public static class OptimumAssetEvents
{
    /// <summary>
    /// Event invoked whenever server assets are modified or reloaded at runtime.
    /// ServerMain subscribes to this to invalidate its cached ServerAssets wire packet.
    /// </summary>
    public static event Action? OnServerAssetsInvalidated;

    /// <summary>
    /// Triggers invalidation of cached server asset wire payloads.
    /// Call this after registering dynamic blocks, items, recipes, or entity types at runtime.
    /// </summary>
    public static void InvalidateServerAssets()
    {
        OnServerAssetsInvalidated?.Invoke();
    }
}
