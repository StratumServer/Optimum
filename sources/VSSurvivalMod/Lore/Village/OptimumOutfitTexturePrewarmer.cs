using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

#nullable disable

namespace Vintagestory.GameContent
{
    /// <summary>
    /// Prewarms the entity texture atlas with every outfit variant's textures (both the
    /// variant's own Textures/OverrideTextures and each referenced gear shape's own texture
    /// set) for every entity type with an outfit config, once, during the loading screen
    /// (capi.Event.LevelFinalize - a documented main-thread event, the same event class
    /// Optimum already hooks via GuiManager.OnLevelFinalize).
    ///
    /// Root cause this targets: capi.EntityTextureAtlas.GetOrInsertTexture on a cache miss can
    /// mean a PNG decode from disk, an atlas allocation, a GL upload, and in the worst case an
    /// entire new full-size atlas + mip-map regen - all synchronous, all on the render thread.
    /// EntityDressedHumanoid.OnTesselation calls this per gear texture the first time any NPC
    /// wears it, so the FIRST trader/villager encountered with a given outfit piece pays this
    /// cost mid-gameplay (measured 111-231ms single-call outliers via .optimum stutterwatch).
    /// Doing the exact same insertions during LevelFinalize instead means the cost lands on the
    /// loading screen - invisible to the player - instead of an in-game frame.
    ///
    /// Uses the same public GetOrInsertTexture API EntityDressedHumanoid.CreateCompositeTexture
    /// already calls at runtime; this file changes only WHEN that cost is paid, not what it
    /// costs. Deliberately conservative: catches and logs per-config-file so one broken outfit
    /// config can't break the whole prewarm pass (or the loading screen). Default OFF pending
    /// real gameplay testing - see OptimumConfig.EntityOutfitTexturePrewarmEnabled.
    /// </summary>
    public class OptimumOutfitTexturePrewarmerModSystem : ModSystem
    {
        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            api.Event.LevelFinalize += () => Prewarm(api);
        }

        private void Prewarm(ICoreClientAPI capi)
        {
            if (!OptimumConfig.EntityOutfitTexturePrewarmEnabled) return;

            var humanoidOutfits = capi.ModLoader.GetModSystem<HumanoidOutfits>();
            if (humanoidOutfits == null) return;

            var processedConfigs = new HashSet<string>();
            var processedShapes = new HashSet<string>();
            int texturesInserted = 0;
            int configsProcessed = 0;

            foreach (EntityProperties entityType in capi.World.EntityTypes)
            {
                // Optimum: traders/villagers register under subclass names ("EntityTrader",
                // "EntityVillager", ...), never the literal "EntityDressedHumanoid" - a plain
                // string match against Class here always misses them, which is why this
                // returned "0 configs" against real game data on first test. CreateEntity()
                // just instantiates a bare, uninitialized object (no Initialize() call, no
                // spawn) purely to check its runtime type, then discards it.
                if (entityType?.Class == null) continue;
                if (capi.ClassRegistry.CreateEntity(entityType.Class) is not EntityDressedHumanoid) continue;

                AssetLocation configFilename = AssetLocation.Create(
                    entityType.Attributes?["outfitConfigFileName"].AsString("traderaccessories") ?? "traderaccessories",
                    entityType.Code.Domain);

                string configKey = configFilename.ToString();
                if (!processedConfigs.Add(configKey)) continue;

                try
                {
                    HumanoidWearableProperties props = humanoidOutfits.GetConfig(configKey);
                    if (props?.Variants == null) continue;

                    configsProcessed++;

                    foreach (TexturedWeightedCompositeShape variant in props.Variants.Values)
                    {
                        if (variant == null) continue;

                        texturesInserted += PrewarmTextureDict(capi, variant.Textures);
                        texturesInserted += PrewarmTextureDict(capi, variant.OverrideTextures);

                        if (variant.Base == null) continue;

                        AssetLocation shapePath = variant.Base.CopyWithPathPrefixAndAppendixOnce("shapes/", ".json");
                        string shapeKey = shapePath.ToString();
                        if (!processedShapes.Add(shapeKey)) continue;

                        Shape gearShape = Shape.TryGet(capi, shapePath);
                        if (gearShape?.Textures != null)
                        {
                            texturesInserted += PrewarmTextureDict(capi, gearShape.Textures);
                        }
                    }
                }
                catch (System.Exception e)
                {
                    capi.Logger.Warning("[Optimum] Outfit texture prewarm failed for config {0}, skipping (outfit textures for this config will insert lazily at runtime instead): {1}", configKey, e.Message);
                }
            }

            capi.Logger.Notification("[Optimum] Prewarmed {0} outfit texture(s) across {1} outfit config(s)", texturesInserted, configsProcessed);
        }

        private static int PrewarmTextureDict(ICoreClientAPI capi, IDictionary<string, AssetLocation> textures)
        {
            if (textures == null) return 0;

            int count = 0;
            foreach (var kv in textures)
            {
                var cmpt = new CompositeTexture(kv.Value);
                cmpt.Bake(capi.Assets);
                if (cmpt.Baked?.TextureFilenames == null || cmpt.Baked.TextureFilenames.Length == 0) continue;

                capi.EntityTextureAtlas.GetOrInsertTexture(
                    new AssetLocationAndSource(cmpt.Baked.TextureFilenames[0], "Optimum outfit prewarm"),
                    out _, out _);
                count++;
            }
            return count;
        }
    }
}
