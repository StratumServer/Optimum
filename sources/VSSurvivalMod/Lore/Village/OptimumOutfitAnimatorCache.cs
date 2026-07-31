using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

#nullable disable

namespace Vintagestory.GameContent
{
    /// <summary>
    /// Caches the animator build (RootElements, RootPoses, Animations) for
    /// EntityDressedHumanoid.OnTesselation, keyed by the same outfit signature as
    /// OptimumOutfitShapeCache. Mirrors vanilla's own AnimationCache (see
    /// AnimationCache.InitManager in the decompiled API source), which already skips rebuilding
    /// the animator from scratch on a cache hit by calling ClientAnimator/ServerAnimator's
    /// CreateForEntity overload that takes pre-built RootPoses+RootElems instead of walking the
    /// whole shape element tree again. Vanilla's own cache can't be reused here because its key
    /// (entity.Code + base shape) can't distinguish outfits - two different outfits on the same
    /// entity type would collide and silently reuse the wrong gear's pose graph. This cache adds
    /// the outfit dimension to the key instead of touching vanilla's cache.
    ///
    /// entityShape.JointsById is always recomputed fresh via InitForAnimations on every call
    /// (hit or miss) since it depends on the specific gear-augmented Shape instance passed in,
    /// not just the outfit key - only the (comparatively expensive) RootElements/RootPoses
    /// element-tree walk is skipped on a hit.
    ///
    /// Root cause this targets: AnimManager.LoadAnimator's uncached path (Shape.InitForAnimations
    /// + ClientAnimator.CreateForEntity's full element-tree walk) is the single largest remaining
    /// per-call cost measured via .optimum stutterwatch (7-8ms/call) even with
    /// OptimumOutfitShapeCache and OptimumOutfitTexturePrewarmerModSystem enabled, because
    /// EntityDressedHumanoid always sets shapeIsCloned = true, which forces vanilla's own
    /// AnimManager.LoadAnimator (cold) instead of AnimManager.LoadAnimatorCached.
    /// </summary>
    public static class OptimumOutfitAnimatorCache
    {
        private sealed class CacheEntry
        {
            public Animation[] Animations;
            public ShapeElement[] RootElems;
            public List<ElementPose> RootPoses;
        }

        private static readonly Dictionary<string, CacheEntry> Cache = new();

        public static bool Enabled => OptimumConfig.EntityOutfitAnimatorCacheEnabled;

        /// <summary>
        /// On a cache hit, builds the animator from the cached RootElements/RootPoses/Animations
        /// (skipping the full element-tree walk) and wires it up on entity.AnimManager, exactly
        /// like vanilla's AnimationCache.InitManager's hit path. Returns false on a miss without
        /// mutating anything, so the caller can fall back to AnimManager.LoadAnimator itself.
        /// </summary>
        public static bool TryApply(
            string key,
            ICoreAPI api,
            Entity entity,
            Shape entityShape,
            RunningAnimation[] copyOverAnims,
            bool requirePosesOnServer,
            string[] disableElements,
            string[] requireJointsForElements)
        {
            if (!Cache.TryGetValue(key, out CacheEntry cached))
            {
                OptimumDiagnostics.EntityOutfitAnimatorCache.Skip();
                return false;
            }

            entityShape.InitForAnimations(
                api.Logger,
                entity.Properties.Client.ShapeForEntity.Base.ToString(),
                disableElements,
                requireJointsForElements);

            var manager = entity.AnimManager;
            manager.Init(api, entity);

            IAnimator animator = api.Side == EnumAppSide.Client
                ? ClientAnimator.CreateForEntity(entity, cached.RootPoses, cached.Animations, cached.RootElems, entityShape.JointsById)
                : ServerAnimator.CreateForEntity(entity, cached.RootPoses, cached.Animations, cached.RootElems, entityShape.JointsById, requirePosesOnServer);

            manager.Animator = animator;
            manager.CopyOverAnimStates(copyOverAnims, animator);
            OptimumDiagnostics.EntityOutfitAnimatorCache.Hit();
            return true;
        }

        /// <summary>
        /// Stores the animator build that was just produced by the vanilla (cold) path, for
        /// reuse by future TryApply calls with the same outfit key.
        /// </summary>
        public static void Store(string key, Shape entityShape, IAnimator animator)
        {
            if (animator is not AnimatorBase animatorBase) return;

            Cache[key] = new CacheEntry
            {
                Animations = entityShape.Animations,
                RootElems = animatorBase.RootElements,
                RootPoses = animatorBase.RootPoses,
            };
        }

        public static void Clear()
        {
            Cache.Clear();
        }
    }
}
