using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

#nullable disable

namespace Vintagestory.GameContent
{
    /// <summary>
    /// Caches the fully assembled outfit shape (post gear step-parenting) and resolved texture
    /// set for EntityDressedHumanoid.OnTesselation, keyed by outfit signature. Root cause: gear
    /// shape assembly - Shape.TryGet's uncached JSON parse plus SubclassForStepParenting's
    /// element-tree walk, once per gear slot, on every re-tesselation - measured at 50-230ms per
    /// dressed humanoid via .optimum stutterwatch, and a village/caravan loading in can put
    /// several such re-tesselations in one frame. Many NPCs share the exact same outfit
    /// (entity.Code + OutfitConfigFileName + outfit codes), so the assembly work is repeatable.
    ///
    /// Every consumer gets its own Shape.Clone() - the cache's own copy is never handed out or
    /// mutated - so there is no cross-entity mutation risk. CompositeTexture instances in the
    /// cached texture dict ARE shared directly (safe: immutable once Bake()'d, holding only an
    /// AssetLocation and a resolved atlas TextureSubId).
    ///
    /// Deliberately scoped: this does NOT cache or share the animator/InitForAnimations output
    /// (Shape.JointsById, ClientAnimator poses) the way vanilla's AnimationCache does for
    /// undressed entities - EntityDressedHumanoid always takes the "shapeIsCloned" branch in
    /// Entity.OnTesselation, and safely sharing that data requires a cache key vanilla's own
    /// AnimationCache doesn't have (it keys only on entity.Code + base shape, which can't tell
    /// outfits apart). That is a separate, higher-risk follow-up, not attempted here.
    /// </summary>
    public static class OptimumOutfitShapeCache
    {
        private sealed class CacheEntry
        {
            public Shape AssembledShape;
            public FastSmallDictionary<string, CompositeTexture> Textures;
        }

        private static readonly Dictionary<string, CacheEntry> Cache = new();

        public static bool Enabled => OptimumConfig.EntityOutfitShapeCacheEnabled;

        public static string BuildKey(string entityCode, string outfitConfigFileName, string[] outfitSlots, string[] outfitCodes)
        {
            return entityCode + "|" + outfitConfigFileName + "|"
                + (outfitSlots != null ? string.Join(",", outfitSlots) : "") + "|"
                + (outfitCodes != null ? string.Join(",", outfitCodes) : "");
        }

        /// <summary>
        /// On a cache hit, returns an independent clone of the assembled shape and a fresh
        /// texture dictionary populated from the cached (shared, immutable) CompositeTexture
        /// instances - safe for the caller to mutate freely.
        /// </summary>
        public static bool TryGet(string key, out Shape clonedShape, out FastSmallDictionary<string, CompositeTexture> textures)
        {
            if (Cache.TryGetValue(key, out CacheEntry entry))
            {
                clonedShape = entry.AssembledShape.Clone();
                textures = new FastSmallDictionary<string, CompositeTexture>(entry.Textures.Count);
                foreach (var kv in entry.Textures)
                {
                    textures[kv.Key] = kv.Value;
                }
                OptimumDiagnostics.EntityOutfitShapeCache.Hit();
                return true;
            }

            clonedShape = null;
            textures = null;
            OptimumDiagnostics.EntityOutfitShapeCache.Skip();
            return false;
        }

        /// <summary>
        /// Stores an independent clone of the assembled shape (the caller keeps using the shape
        /// instance it just built - the cache never hands out or mutates the one passed here).
        /// </summary>
        public static void Store(string key, Shape assembledShape, FastSmallDictionary<string, CompositeTexture> textures)
        {
            Cache[key] = new CacheEntry
            {
                AssembledShape = assembledShape.Clone(),
                Textures = textures,
            };
        }

        public static void Clear()
        {
            Cache.Clear();
        }
    }
}
