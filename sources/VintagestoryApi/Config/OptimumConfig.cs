using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

[assembly: InternalsVisibleTo("Optimum.Tests")]

namespace Vintagestory.API.Config;

/// <summary>
/// Runtime config for Optimum optimizations. Persists to ModConfig/optimum.json.
/// VintagestoryLib syncs these from ClientSettings at startup; forks read the static fields.
/// </summary>
public static class OptimumConfig
{
    /// <summary>
    /// Supplies the version to every managed assembly. Packaging scripts read
    /// the root VERSION file. Keep both values equal for each release.
    /// </summary>
    public const string Version = "0.3.3";

    public static bool RepulsionGateEnabled = true;
    public static int RepulsionDistance = 64;
    public static double RepulsionDistanceSq = 64.0 * 64.0;

    public static bool AnimBlockLodEnabled = true;

    /// <summary>
    /// Hard cap on animator updates per render frame, independent of the
    /// near/mid/far distance tiers above. Blocks over budget defer through
    /// the same skip-time accumulator the mid tier uses and catch up once
    /// their turn comes. 0 disables the cap.
    /// </summary>
    public static int AnimBlockLodFrameBudget = 256;

    public static bool WeatherWindThrottleEnabled = true;
    public static bool ParticleDistanceGateEnabled = true;
    public static bool ChiselLodEnabled = true;

    /// <summary>
    /// Playtested and measured in a running client (2026-07-09 and
    /// 2026-07-10 sessions, docs/benchmarking.md): the fragment-shader
    /// wrap (bug B3) works, OFF compiles bit-identical vanilla shaders,
    /// and ON at 8x8 trades ~3% mean FPS for a large stutter reduction
    /// (+84% 1% low FPS, p99 44->30 ms), ~10pp less CPU and ~30 MB less
    /// VRAM on a fragment-bound scene. Still default false: that is one
    /// trial per config on one route/GPU, and the 1%-low direction
    /// inverted between the two days' scenes, so the flip to true waits
    /// for a repeat run confirming the pattern (V2.2 in docs/todo.md).
    /// </summary>
    public static bool GreedyMeshEnabled = false;

    /// <summary>
    /// Caps the greedy merge span. Merged quads tile the texture via a
    /// UV-space fract() wrap in chunkopaque.fsh. Max 8 (3 bits in
    /// renderFlags). At 1x1 the emitter skips the whole pass (a 1x1
    /// "merge" would replace one vanilla quad with an identical one -
    /// all cost, no benefit), so 1 = merging off. Default 8 since the
    /// 2026-07-10 re-benchmark: 8x8 matched 4x4 on mean FPS and beat it
    /// on 1% lows (docs/benchmarking.md), and a default of 1 made
    /// enabling GreedyMeshEnabled silently do nothing. Inert while the
    /// master switch is false.
    /// </summary>
    public static int GreedyMeshMaxMergeWidth = 8;
    public static int GreedyMeshMaxMergeHeight = 8;

    /// <summary>
    /// Light quantization for greedy merge eligibility, 0-4. 0 = exact:
    /// faces merge only when all 4 corner light values are identical
    /// (pixel-identical to vanilla, but merges little with smooth
    /// lighting on). t > 0 quantizes each light channel to steps of 2^t
    /// (out of 255) before the equality test and emits the quantized
    /// value, letting faces that differ by under 2^t light levels merge.
    /// 1-2 is visually imperceptible in most scenes; the cost is a
    /// floor-quantization darkening of at most 2^t - 1 levels.
    /// </summary>
    public static int GreedyMeshLightTolerance = 0;

    /// <summary>
    /// Distance band for aggressive merging, in blocks, horizontal. 0 =
    /// uniform (GreedyMeshLightTolerance applies everywhere). > 0: chunks
    /// beyond this distance from the player merge with tolerance 4
    /// (16-level light) regardless of the base tolerance - a stretched
    /// light gradient 100+ blocks away is invisible, and far chunks are
    /// where vertex counts accumulate. Chunks pick up the new mode on
    /// their next natural retesselation when crossing the band, same as
    /// chisel LOD.
    /// </summary>
    public static int GreedyMeshFarDistance = 0;
    public static double GreedyMeshFarDistanceSq = 0;

    /// <summary>
    /// When true (default), merged quads sample the atlas with
    /// textureGrad() and derivatives taken from the unwrapped UV, which
    /// keeps mip selection seamless across the fract() wrap. When false,
    /// merged quads use a plain texture() lookup: visible mip seams can
    /// appear at tile boundaries on distant merged quads, but the
    /// explicit-gradient sampler cost (reduced-rate on some GPUs) goes
    /// away. Exists to isolate where the measured ON cost comes from
    /// (2026-07-10: ON at 8x8 costs ~3% mean FPS on a fragment-bound
    /// scene, docs/benchmarking.md) - flip to false, re-run the same
    /// route, and compare. Only affects shading when GreedyMeshEnabled
    /// is true and merges happen; requires a restart like the rest.
    /// </summary>
    public static bool GreedyMeshTextureGrad = true;

    /// <summary>
    /// Set by the ShaderRegistry patch each time chunk shaders compile:
    /// true when they were stamped with #define GREEDYMESH 1. The emitter
    /// refuses to emit merged (tiled) quads unless this is true, so a
    /// config/shader mismatch (e.g. config edited mid-session before a
    /// shader reload) degrades to 1x1 merges instead of feeding sentinel
    /// bits to a shader that won't decode them. Not persisted.
    /// </summary>
    public static volatile bool GreedyMeshShadersCompiledOn;

    public static int ChiselLodDistance = 48;
    public static double ChiselLodDistanceSq = 48.0 * 48.0;

    /// <summary>
    /// R3: scale ChunkCuller's occlusion-culling engagement threshold by view
    /// distance instead of a fixed 100-chunk floor, so culling still pays for
    /// its own traversal cost at low view distances where fewer than 100
    /// chunks ever load.
    /// </summary>
    public static bool OcclusionCullingScaleEnabled = true;

    /// <summary>
    /// Reuse the dynamic-light entity scan from the previous frame while the
    /// player is roughly stationary, instead of rescanning every frame.
    /// Refreshes on player movement past a small threshold or every 15
    /// frames, whichever comes first, so an entity crossing into or out of
    /// range is picked up within a quarter second at most while standing still.
    /// </summary>
    public static bool DynamicLightCacheEnabled = true;

    public static bool EntityLightBatchEnabled = true;
    public static bool EntityShaderStateCacheEnabled = true;

    /// <summary>
    /// Caps how many entities may re-tesselate their shape (EntityShapeRenderer.TesselateShape)
    /// in a single frame. A re-tesselation's synchronous cost - shape parse, clone,
    /// step-parenting, texture atlas insertion, animator rebuild - runs 50-230ms per entity for
    /// geared/dressed humanoids (measured via .optimum stutterwatch), and a burst of entities
    /// needing it at once (e.g. a trader caravan loading in) front-loads all of that into one
    /// frame. Entities over budget simply retry next frame - ShapeFresh stays false, no state
    /// to unwind. An entity's very first tesselation (no mesh yet) is never gated, so newly
    /// spawned/loaded entities don't sit invisible waiting for a budget slot. 0 disables the cap.
    /// </summary>
    public static int EntityTesselationFrameBudget = 2;

    /// <summary>
    /// Caches the fully assembled outfit shape (post gear step-parenting) and resolved texture
    /// set for EntityDressedHumanoid.OnTesselation, keyed by outfit signature - see
    /// OptimumOutfitShapeCache in VSSurvivalMod for the full mechanism and why it's safe (every
    /// consumer gets an independent Shape.Clone(), never a shared mutable instance). Deliberately
    /// does NOT share the animator/InitForAnimations output the way vanilla's own AnimationCache
    /// does for undressed entities - that needs a cache key vanilla's AnimationCache doesn't have,
    /// and is a separate, higher-risk follow-up. Default OFF until played with real gameplay:
    /// this is new code on the entity-appearance path, and only compile/unit-test verified so far.
    /// </summary>
    public static bool EntityOutfitShapeCacheEnabled = false;

    /// <summary>
    /// The higher-risk follow-up scoped out of EntityOutfitShapeCacheEnabled: shares the
    /// animator build (ClientAnimator/ServerAnimator RootElements+RootPoses+Animations) across
    /// entities with the same outfit signature, mirroring vanilla's own AnimationCache - see
    /// OptimumOutfitAnimatorCache in VSSurvivalMod. Vanilla's own cache keys only on
    /// entity.Code + base shape, which can't tell outfits apart, so EntityDressedHumanoid always
    /// takes the uncached AnimManager.LoadAnimator path (Shape.InitForAnimations +
    /// ClientAnimator.CreateForEntity's full element-tree walk) - this is the single largest
    /// remaining cost in the stutter this file's other outfit settings target (measured
    /// 7-8ms/call even with the shape+texture cache and prewarm enabled). Requires duplicating
    /// Entity.OnTesselation(ref Shape, string, ref bool)'s overlay/behavior/willDisableElements
    /// handling in EntityDressedHumanoid to intercept only the final animator build - safe today
    /// (trader/villager entity types have no shape overlays and no client behaviors that mutate
    /// the shape) but higher-risk than the other outfit settings if that ever changes. Default
    /// OFF pending real gameplay testing: only compile/unit-test verified so far.
    /// </summary>
    public static bool EntityOutfitAnimatorCacheEnabled = false;

    /// <summary>
    /// Prewarms the entity texture atlas with every outfit variant's textures, for every
    /// entity type with an outfit config, once during the loading screen (capi.Event.LevelFinalize)
    /// - see OptimumOutfitTexturePrewarmerModSystem in VSSurvivalMod for the full mechanism.
    /// Uses the same GetOrInsertTexture call EntityDressedHumanoid already makes at runtime;
    /// this only changes WHEN that cost is paid (loading screen, invisible) vs mid-gameplay
    /// (a 111-231ms single-call outlier the first time any NPC wears a given outfit piece,
    /// measured via .optimum stutterwatch). Default OFF pending real gameplay testing: this
    /// extends loading time by an amount proportional to outfit variant count, and is only
    /// compile/unit-test verified so far.
    /// </summary>
    public static bool EntityOutfitTexturePrewarmEnabled = false;

    [ThreadStatic]
    public static bool RouteChiselLodMeshes;

    // Settings that live in VintagestoryLib (read per-frame from ClientSettings).
    // Mirrored here for persistence only.
    public static bool EntityShadowCull = true;
    public static int ShadowCullDistance = 80;
    public static bool DynamicLightScale = true;
    public static bool BackgroundFpsLimit = true;
    public static bool PreciseFramePacing = true;
    public static bool ShadowFarVegetation = true;

    /// <summary>
    /// T3: Reduce position-packet send frequency for distant entities (IsTracked==1,
    /// beyond ~50 blocks). Default OFF: this changes observable network behavior.
    /// FarTrackedTickStride=2 sends at 15Hz instead of 30Hz. Fast-moving entities
    /// and EntityItems bypass the throttle.
    /// </summary>
    public static bool DistanceSendFrequencyEnabled = false;
    public static int FarTrackedTickStride = 2;

    /// <summary>
    /// T4: Split the random tick pass into N sub-passes (default 8) at
    /// BlockTickInterval/N intervals. Each pass processes only 1/N of chunks
    /// (modulo filtering). Reduces tick-time sawtooth from ~48ms max to ~6ms
    /// per pass. Chunks still receive the same aggregate tick rate per cycle.
    /// Default ON: pure scheduling change, no gameplay logic change.
    /// </summary>
    public static bool RandomTickSliceEnabled = true;

    /// <summary>
    /// Lets singleplayer world generation share passes across worker threads.
    /// The scheduler owns one pass per worker and returns to the vanilla chunk
    /// thread when it detects a scheduler fault. Default ON: the statistical
    /// parity gate showed the divergence from serial generation matches what
    /// vanilla's own multithreaded worldgen (MaxWorldgenThreads > 1) produces,
    /// and vanilla worldgen is not run-to-run deterministic to begin with (see
    /// docs/superpowers/specs/2026-07-16-per-pass-mutual-exclusion.md).
    /// </summary>
    public static bool WorldgenWorkStealingEnabled = true;

    /// <summary>
    /// Replace the vanilla ChunkMapLayer upload pipeline with a page-cached
    /// renderer. Pages (8x8 chunks, 256x256 pixels) persist to disk as
    /// zstd-compressed RGBA and upload to a GL_TEXTURE_2D_ARRAY for
    /// single-draw-call rendering. Explored chunks never re-generate on map
    /// open: the cache serves them in sub-100ms. Default true.
    /// </summary>
    public static bool MapPageCacheEnabled = true;

    /// <summary>
    /// Number of layers in the GL_TEXTURE_2D_ARRAY used for map page
    /// rendering. Each layer holds one 256x256 page. 128 covers most
    /// viewport sizes at normal zoom; raise for extreme view distances.
    /// </summary>
    public static int MapPageCacheMaxLayers = 128;

    /// <summary>
    /// Use BC7 compressed textures (GL_ARB_texture_compression_bptc) for
    /// map pages when the GPU supports it. Cuts VRAM 4x and upload bandwidth
    /// proportionally. Falls back to RGBA8 when the extension is absent.
    /// </summary>
    public static bool MapPageCacheBc7 = true;

    /// <summary>
    /// Runtime flag: true when the GPU reports GL_ARB_texture_compression_bptc.
    /// Set at startup, not persisted.
    /// </summary>
    public static volatile bool MapPageCacheBc7Supported;

    /// <summary>
    /// Generate approximate biome-colored map tiles for chunks the player
    /// has not explored. Uses the world seed plus climate, ocean, and forest
    /// region maps to produce a low-fidelity terrain overview. The pregen
    /// pixels carry a desaturation tint so the player can distinguish them
    /// from real explored terrain at a glance. Default false (opt-in).
    /// </summary>
    public static bool MapPageCachePregen = false;

    /// <summary>
    /// Enables the parallel read-only SQLite connection pool for chunk column
    /// loading (OptimumChunkReadPool). In singleplayer, DB I/O dominates chunk
    /// thread time; the pool fans the per-Y-level SELECT queries out across
    /// several read-only connections in WAL mode. Default true.
    /// </summary>
    public static bool ChunkReadPoolEnabled = true;

    /// <summary>
    /// Number of read-only SQLite connections in the chunk read pool.
    /// Clamped to [1, 8] by OptimumChunkReadPool itself. Default 4.
    /// </summary>
    public static int ChunkReadPoolWorkers = 4;

    /// <summary>
    /// Adaptive chunk generation radius: reduces the effective view radius
    /// under gen-queue pressure (player exploring fast) so fewer columns
    /// queue at once. Recovers to full radius when the queue drains.
    /// Default ON in singleplayer. Multiplayer servers already have dedicated
    /// gen threads and a capped MaxChunkRadius, so this mostly helps SP.
    /// </summary>
    public static bool AdaptiveRadiusEnabled = true;

    /// <summary>
    /// Floor radius in chunks: the controller never drops below this value.
    /// 4 chunks = 128 blocks = still enough to avoid visible pop-in at
    /// normal walk speed.
    /// </summary>
    public static int AdaptiveRadiusFloor = 4;

    /// <summary>
    /// When the EWMA-smoothed queue depth exceeds this count, the controller
    /// shrinks the radius by 1 per tick. Default 60 = ~2x the typical
    /// steady-state queue when walking at normal speed.
    /// </summary>
    public static int AdaptiveRadiusHighThreshold = 60;

    /// <summary>
    /// When the smoothed queue depth falls below this count, the controller
    /// recovers the radius by 1 per tick. Default 20 = the queue has drained
    /// enough to safely grow the radius back.
    /// </summary>
    public static int AdaptiveRadiusLowThreshold = 20;

    /// <summary>
    /// Runtime-only: the current effective max chunk radius after adaptive
    /// scaling. Written by OptimumAdaptiveRadiusController.Tick(), read by
    /// ServerSystemSendChunks to cap how far out it requests chunks. When
    /// AdaptiveRadiusEnabled is false, stays at int.MaxValue (no cap).
    /// Not persisted.
    /// </summary>
    public static volatile int AdaptiveRadiusEffective = int.MaxValue;

    /// <summary>
    /// FSR render scale: 1.0 = native (off), 0.85 = quality, 0.77 = balanced, 0.67 = performance.
    /// Multiplies ssaaLevel in SetupDefaultFrameBuffers. Disables FXAA when < 1.0.
    /// </summary>
    public static float RenderScale = 1.0f;

    private static string? _configPath;

    public static void SetRepulsionDistance(int blocks)
    {
        RepulsionDistance = blocks;
        RepulsionDistanceSq = (double)blocks * blocks;
    }

    public static void SetChiselLodDistance(int blocks)
    {
        ChiselLodDistance = blocks;
        ChiselLodDistanceSq = (double)blocks * blocks;
    }

    /// <summary>
    /// Worker count policy derived from benchmark data (6-core WSL2, 2026-07-16):
    ///   serial=30s, 1w=23s(1.30x), 2w=17s(1.76x), 3w=15s(2.00x), 4w=17s(regress), 5w=16s.
    /// Three workers saturate the E.2 illuminator lock; adding more just piles up
    /// contention without reducing generation time. The chunk thread itself needs
    /// one core, so the policy keeps workers at floor(cores/2) capped at 3.
    ///
    /// During spawn-chunk generation (before RunGame), returns the conservative
    /// count (2 for 5-6 cores) to avoid starving the client renderer on the same
    /// CPU. After RunGame, the adaptive controller raises to the ceiling.
    /// </summary>
    public static int GetWorldgenWorkerCount(int logicalProcessors, bool reducedServerThreads)
    {
        if (!WorldgenWorkStealingEnabled || reducedServerThreads)
        {
            return 0;
        }

        // 4 cores or fewer: overhead exceeds gains (1-worker barely breaks even on 6c)
        if (logicalProcessors <= 4)
        {
            return 0;
        }

        // 5-6 cores: start with 2 during spawn (client is loading, GPU busy).
        // Post-spawn, the adaptive controller raises to GetWorldgenWorkerCeiling().
        if (logicalProcessors <= 6)
        {
            return 2;
        }

        // 7-8 cores: start with 2, ceiling at 3
        return 2;
    }

    /// <summary>
    /// Maximum worker count the adaptive controller may raise to after spawn
    /// chunks finish. The benchmark-proven ceiling for the hardware.
    /// </summary>
    public static int GetWorldgenWorkerCeiling(int logicalProcessors, bool reducedServerThreads)
    {
        if (!WorldgenWorkStealingEnabled || reducedServerThreads)
        {
            return 0;
        }

        if (logicalProcessors <= 4) return 0;
        if (logicalProcessors <= 6) return 3;  // 2.00x measured on 6c
        return 3;  // E.2 lock caps useful parallelism at 3 regardless of core count
    }

    /// <summary>
    /// One entry per field OptimumConfigData persists, keyed by the persisted
    /// name rather than the backing static field's own identifier (they differ
    /// for a few toggles, e.g. RepulsionGateEnabled persists as RepulsionGate).
    /// Drives .optimum status and the coverage test that keeps this in sync
    /// with OptimumConfigData whenever a field gets added or removed.
    /// </summary>
    public static (string Name, string Value)[] DescribeToggles() => new (string, string)[]
    {
        (nameof(OptimumConfigData.EntityShadowCull), EntityShadowCull.ToString()),
        (nameof(OptimumConfigData.ShadowCullDistance), ShadowCullDistance.ToString()),
        (nameof(OptimumConfigData.DynamicLightScale), DynamicLightScale.ToString()),
        (nameof(OptimumConfigData.BackgroundFpsLimit), BackgroundFpsLimit.ToString()),
        (nameof(OptimumConfigData.PreciseFramePacing), PreciseFramePacing.ToString()),
        (nameof(OptimumConfigData.RepulsionGate), RepulsionGateEnabled.ToString()),
        (nameof(OptimumConfigData.RepulsionDistance), RepulsionDistance.ToString()),
        (nameof(OptimumConfigData.AnimBlockLod), AnimBlockLodEnabled.ToString()),
        (nameof(OptimumConfigData.AnimBlockLodFrameBudget), AnimBlockLodFrameBudget.ToString()),
        (nameof(OptimumConfigData.ShadowFarVegetation), ShadowFarVegetation.ToString()),
        (nameof(OptimumConfigData.WeatherWindThrottle), WeatherWindThrottleEnabled.ToString()),
        (nameof(OptimumConfigData.ParticleDistanceGate), ParticleDistanceGateEnabled.ToString()),
        (nameof(OptimumConfigData.ChiselLod), ChiselLodEnabled.ToString()),
        (nameof(OptimumConfigData.ChiselLodDistance), ChiselLodDistance.ToString()),
        (nameof(OptimumConfigData.OcclusionCullingScale), OcclusionCullingScaleEnabled.ToString()),
        (nameof(OptimumConfigData.DynamicLightCache), DynamicLightCacheEnabled.ToString()),
        (nameof(OptimumConfigData.EntityLightBatch), EntityLightBatchEnabled.ToString()),
        (nameof(OptimumConfigData.EntityShaderStateCache), EntityShaderStateCacheEnabled.ToString()),
        (nameof(OptimumConfigData.EntityTesselationFrameBudget), EntityTesselationFrameBudget.ToString()),
        (nameof(OptimumConfigData.EntityOutfitShapeCache), EntityOutfitShapeCacheEnabled.ToString()),
        (nameof(OptimumConfigData.EntityOutfitAnimatorCache), EntityOutfitAnimatorCacheEnabled.ToString()),
        (nameof(OptimumConfigData.EntityOutfitTexturePrewarm), EntityOutfitTexturePrewarmEnabled.ToString()),
        (nameof(OptimumConfigData.GreedyMeshEnabled), GreedyMeshEnabled.ToString()),
        (nameof(OptimumConfigData.GreedyMeshMaxMergeWidth), GreedyMeshMaxMergeWidth.ToString()),
        (nameof(OptimumConfigData.GreedyMeshMaxMergeHeight), GreedyMeshMaxMergeHeight.ToString()),
        (nameof(OptimumConfigData.GreedyMeshLightTolerance), GreedyMeshLightTolerance.ToString()),
        (nameof(OptimumConfigData.GreedyMeshFarDistance), GreedyMeshFarDistance.ToString()),
        (nameof(OptimumConfigData.GreedyMeshTextureGrad), GreedyMeshTextureGrad.ToString()),
        (nameof(OptimumConfigData.RenderScale), RenderScale.ToString("F2")),
        (nameof(OptimumConfigData.MapPageCache), MapPageCacheEnabled.ToString()),
        (nameof(OptimumConfigData.MapPageCacheMaxLayers), MapPageCacheMaxLayers.ToString()),
        (nameof(OptimumConfigData.MapPageCacheBc7), MapPageCacheBc7.ToString()),
        (nameof(OptimumConfigData.RandomTickSlice), RandomTickSliceEnabled.ToString()),
        (nameof(OptimumConfigData.WorldgenWorkStealing), WorldgenWorkStealingEnabled.ToString()),
        (nameof(OptimumConfigData.ChunkReadPoolEnabled), ChunkReadPoolEnabled.ToString()),
        (nameof(OptimumConfigData.ChunkReadPoolWorkers), ChunkReadPoolWorkers.ToString()),
        (nameof(OptimumConfigData.AdaptiveRadius), AdaptiveRadiusEnabled.ToString()),
        (nameof(OptimumConfigData.AdaptiveRadiusFloor), AdaptiveRadiusFloor.ToString()),
        (nameof(OptimumConfigData.AdaptiveRadiusHighThreshold), AdaptiveRadiusHighThreshold.ToString()),
        (nameof(OptimumConfigData.AdaptiveRadiusLowThreshold), AdaptiveRadiusLowThreshold.ToString()),
    };

    /// <summary>
    /// Set the data path root (e.g. GamePaths.DataPath). Call once at startup.
    /// </summary>
    public static void SetDataPath(string dataPath)
    {
        string dir = Path.Combine(dataPath, "ModConfig");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "optimum.json");
    }

    /// <summary>
    /// Load config from optimum.json. Missing keys keep their compiled
    /// defaults. After a successful load the file is written back, so
    /// clamped values are normalized on disk and fields added since the
    /// file was written appear in it automatically; a missing file is
    /// created with the defaults on first run. A file that fails to
    /// parse is left untouched (never clobber what the user typed).
    /// </summary>
    public static void Load()
    {
        if (_configPath == null) return;

        if (!File.Exists(_configPath))
        {
            Save();
            return;
        }

        try
        {
            string json = File.ReadAllText(_configPath);
            var data = JsonSerializer.Deserialize<OptimumConfigData>(json);
            if (data == null) return;

            EntityShadowCull = data.EntityShadowCull;
            ShadowCullDistance = data.ShadowCullDistance;
            DynamicLightScale = data.DynamicLightScale;
            BackgroundFpsLimit = data.BackgroundFpsLimit;
            PreciseFramePacing = data.PreciseFramePacing;
            RepulsionGateEnabled = data.RepulsionGate;
            RepulsionDistance = data.RepulsionDistance;
            RepulsionDistanceSq = (double)data.RepulsionDistance * data.RepulsionDistance;
            AnimBlockLodEnabled = data.AnimBlockLod;
            AnimBlockLodFrameBudget = data.AnimBlockLodFrameBudget;
            ShadowFarVegetation = data.ShadowFarVegetation;
            WeatherWindThrottleEnabled = data.WeatherWindThrottle;
            ParticleDistanceGateEnabled = data.ParticleDistanceGate;
            ChiselLodEnabled = data.ChiselLod;
            ChiselLodDistance = data.ChiselLodDistance;
            ChiselLodDistanceSq = (double)data.ChiselLodDistance * data.ChiselLodDistance;
            OcclusionCullingScaleEnabled = data.OcclusionCullingScale;
            DynamicLightCacheEnabled = data.DynamicLightCache;
            EntityLightBatchEnabled = data.EntityLightBatch;
            EntityShaderStateCacheEnabled = data.EntityShaderStateCache;
            EntityTesselationFrameBudget = Math.Max(0, data.EntityTesselationFrameBudget);
            EntityOutfitShapeCacheEnabled = data.EntityOutfitShapeCache;
            EntityOutfitAnimatorCacheEnabled = data.EntityOutfitAnimatorCache;
            EntityOutfitTexturePrewarmEnabled = data.EntityOutfitTexturePrewarm;
            GreedyMeshEnabled = data.GreedyMeshEnabled;
            // Clamped to the tile-count encoding's ceiling (3 bits, max 8)
            // so a hand-edited optimum.json can't request a merge wider
            // than the shader can tile.
            GreedyMeshMaxMergeWidth = Math.Clamp(data.GreedyMeshMaxMergeWidth, 1, 8);
            GreedyMeshMaxMergeHeight = Math.Clamp(data.GreedyMeshMaxMergeHeight, 1, 8);
            GreedyMeshLightTolerance = Math.Clamp(data.GreedyMeshLightTolerance, 0, 4);
            GreedyMeshFarDistance = Math.Max(0, data.GreedyMeshFarDistance);
            GreedyMeshFarDistanceSq = (double)GreedyMeshFarDistance * GreedyMeshFarDistance;
            GreedyMeshTextureGrad = data.GreedyMeshTextureGrad;
            RenderScale = Math.Clamp(data.RenderScale, 0.5f, 1.0f);
            MapPageCacheEnabled = data.MapPageCache;
            MapPageCacheMaxLayers = Math.Clamp(data.MapPageCacheMaxLayers, 16, 512);
            MapPageCacheBc7 = data.MapPageCacheBc7;
            RandomTickSliceEnabled = data.RandomTickSlice;
            WorldgenWorkStealingEnabled = data.WorldgenWorkStealing;
            ChunkReadPoolEnabled = data.ChunkReadPoolEnabled;
            ChunkReadPoolWorkers = Math.Clamp(data.ChunkReadPoolWorkers, 1, 8);
            AdaptiveRadiusEnabled = data.AdaptiveRadius;
            AdaptiveRadiusFloor = Math.Clamp(data.AdaptiveRadiusFloor, 1, 12);
            AdaptiveRadiusHighThreshold = Math.Max(1, data.AdaptiveRadiusHighThreshold);
            AdaptiveRadiusLowThreshold = Math.Max(1, data.AdaptiveRadiusLowThreshold);
        }
        catch (Exception)
        {
            // Corrupt file: ignore, use defaults, and do NOT write back.
            return;
        }

        // Successful parse: re-persist so the on-disk file always carries
        // the full field set at the (clamped) values actually in effect.
        Save();
    }

    /// <summary>
    /// Persist current state to optimum.json.
    /// </summary>
    public static void Save()
    {
        if (_configPath == null) return;

        var data = new OptimumConfigData
        {
            EntityShadowCull = EntityShadowCull,
            ShadowCullDistance = ShadowCullDistance,
            DynamicLightScale = DynamicLightScale,
            BackgroundFpsLimit = BackgroundFpsLimit,
            PreciseFramePacing = PreciseFramePacing,
            RepulsionGate = RepulsionGateEnabled,
            RepulsionDistance = RepulsionDistance,
            AnimBlockLod = AnimBlockLodEnabled,
            AnimBlockLodFrameBudget = AnimBlockLodFrameBudget,
            ShadowFarVegetation = ShadowFarVegetation,
            WeatherWindThrottle = WeatherWindThrottleEnabled,
            ParticleDistanceGate = ParticleDistanceGateEnabled,
            ChiselLod = ChiselLodEnabled,
            ChiselLodDistance = ChiselLodDistance,
            OcclusionCullingScale = OcclusionCullingScaleEnabled,
            DynamicLightCache = DynamicLightCacheEnabled,
            EntityLightBatch = EntityLightBatchEnabled,
            EntityShaderStateCache = EntityShaderStateCacheEnabled,
            EntityTesselationFrameBudget = EntityTesselationFrameBudget,
            EntityOutfitShapeCache = EntityOutfitShapeCacheEnabled,
            EntityOutfitAnimatorCache = EntityOutfitAnimatorCacheEnabled,
            EntityOutfitTexturePrewarm = EntityOutfitTexturePrewarmEnabled,
            GreedyMeshEnabled = GreedyMeshEnabled,
            GreedyMeshMaxMergeWidth = GreedyMeshMaxMergeWidth,
            GreedyMeshMaxMergeHeight = GreedyMeshMaxMergeHeight,
            GreedyMeshLightTolerance = GreedyMeshLightTolerance,
            GreedyMeshFarDistance = GreedyMeshFarDistance,
            GreedyMeshTextureGrad = GreedyMeshTextureGrad,
            RenderScale = RenderScale,
            MapPageCache = MapPageCacheEnabled,
            MapPageCacheMaxLayers = MapPageCacheMaxLayers,
            MapPageCacheBc7 = MapPageCacheBc7,
            RandomTickSlice = RandomTickSliceEnabled,
            WorldgenWorkStealing = WorldgenWorkStealingEnabled,
            ChunkReadPoolEnabled = ChunkReadPoolEnabled,
            ChunkReadPoolWorkers = ChunkReadPoolWorkers,
            AdaptiveRadius = AdaptiveRadiusEnabled,
            AdaptiveRadiusFloor = AdaptiveRadiusFloor,
            AdaptiveRadiusHighThreshold = AdaptiveRadiusHighThreshold,
            AdaptiveRadiusLowThreshold = AdaptiveRadiusLowThreshold,
        };

        try
        {
            string json = JsonSerializer.Serialize(data, _jsonOpts);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception)
        {
            // Disk full or permissions: silently skip.
        }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

internal sealed class OptimumConfigData
{
    public bool EntityShadowCull { get; set; } = true;
    public int ShadowCullDistance { get; set; } = 80;
    public bool DynamicLightScale { get; set; } = true;
    public bool BackgroundFpsLimit { get; set; } = true;
    public bool PreciseFramePacing { get; set; } = true;
    public bool RepulsionGate { get; set; } = true;
    public int RepulsionDistance { get; set; } = 64;
    public bool AnimBlockLod { get; set; } = true;
    public int AnimBlockLodFrameBudget { get; set; } = 256;
    public bool ShadowFarVegetation { get; set; } = true;
    public bool WeatherWindThrottle { get; set; } = true;
    public bool ParticleDistanceGate { get; set; } = true;
    public bool ChiselLod { get; set; } = true;
    public int ChiselLodDistance { get; set; } = 48;
    public bool OcclusionCullingScale { get; set; } = true;
    public bool DynamicLightCache { get; set; } = true;
    public bool EntityLightBatch { get; set; } = true;
    public bool EntityShaderStateCache { get; set; } = true;
    public int EntityTesselationFrameBudget { get; set; } = 2;
    public bool EntityOutfitShapeCache { get; set; } = false;
    public bool EntityOutfitAnimatorCache { get; set; } = false;
    public bool EntityOutfitTexturePrewarm { get; set; } = false;
    public bool GreedyMeshEnabled { get; set; } = false;
    public int GreedyMeshMaxMergeWidth { get; set; } = 8;
    public int GreedyMeshMaxMergeHeight { get; set; } = 8;
    public int GreedyMeshLightTolerance { get; set; } = 0;
    public int GreedyMeshFarDistance { get; set; } = 0;
    public bool GreedyMeshTextureGrad { get; set; } = true;
    public float RenderScale { get; set; } = 1.0f;
    public bool MapPageCache { get; set; } = true;
    public int MapPageCacheMaxLayers { get; set; } = 128;
    public bool MapPageCacheBc7 { get; set; } = true;
    public bool RandomTickSlice { get; set; } = true;
    public bool WorldgenWorkStealing { get; set; } = true;
    public bool ChunkReadPoolEnabled { get; set; } = true;
    public int ChunkReadPoolWorkers { get; set; } = 4;
    public bool AdaptiveRadius { get; set; } = true;
    public int AdaptiveRadiusFloor { get; set; } = 4;
    public int AdaptiveRadiusHighThreshold { get; set; } = 60;
    public int AdaptiveRadiusLowThreshold { get; set; } = 20;
}

public static class OptimumDiagnostics
{
    // Stratum-ported optimization counters (server-side ports)
    private static long _serverTickCount;
    private static long _collisionFastPathHits;
    private static long _collisionFastPathSkips;
    private static long _pathNodePoolRents;
    private static long _pathNodePoolOverflows;
    private static long _collectEntitiesStridedSkips;
    private static long _mechPowerTickCount;

    public static long ServerTickCount => Interlocked.Read(ref _serverTickCount);
    public static long CollisionFastPathHits => Interlocked.Read(ref _collisionFastPathHits);
    public static long CollisionFastPathSkips => Interlocked.Read(ref _collisionFastPathSkips);
    public static long PathNodePoolRents => Interlocked.Read(ref _pathNodePoolRents);
    public static long PathNodePoolOverflows => Interlocked.Read(ref _pathNodePoolOverflows);
    public static long CollectEntitiesStridedSkips => Interlocked.Read(ref _collectEntitiesStridedSkips);
    public static long MechPowerTickCount => Interlocked.Read(ref _mechPowerTickCount);

    public static void RecordServerTick() => Interlocked.Increment(ref _serverTickCount);
    public static void RecordCollisionFastPathHit() => Interlocked.Increment(ref _collisionFastPathHits);
    public static void RecordCollisionFastPathSkip() => Interlocked.Increment(ref _collisionFastPathSkips);
    public static void RecordPathNodePoolRent() => Interlocked.Increment(ref _pathNodePoolRents);
    public static void RecordPathNodePoolOverflow() => Interlocked.Increment(ref _pathNodePoolOverflows);
    public static void RecordCollectEntitiesStridedSkip() => Interlocked.Increment(ref _collectEntitiesStridedSkips);
    public static void RecordMechPowerTick() => Interlocked.Increment(ref _mechPowerTickCount);

    public static void ResetStratumCounters()
    {
        Interlocked.Exchange(ref _serverTickCount, 0);
        Interlocked.Exchange(ref _collisionFastPathHits, 0);
        Interlocked.Exchange(ref _collisionFastPathSkips, 0);
        Interlocked.Exchange(ref _pathNodePoolRents, 0);
        Interlocked.Exchange(ref _pathNodePoolOverflows, 0);
        Interlocked.Exchange(ref _collectEntitiesStridedSkips, 0);
        Interlocked.Exchange(ref _mechPowerTickCount, 0);
    }

    public static string GetStratumSummary()
    {
        return $"[Optimum Stratum ports] ticks={ServerTickCount} collFP={CollisionFastPathHits}/{CollisionFastPathSkips} " +
               $"pathPool={PathNodePoolRents}/{PathNodePoolOverflows} collectSkips={CollectEntitiesStridedSkips} " +
               $"mechTicks={MechPowerTickCount}";
    }

    // Optimum-native optimization counters (client-side)
    private static long _chiselLodBlocks;
    private static long _chiselLodFullMeshContributions;
    private static long _chiselLodProxyMeshContributions;
    private static long _chiselLodFallbackMeshContributions;
    private static long _chiselLodFullTriangles;
    private static long _chiselLodProxyTriangles;
    private static long _chiselLodTesselationTicks;

    private static long _animBlockRuns;
    private static long _animBlockTicks;

    private static long _greedyMeshChunks;
    private static long _greedyMeshQuads;
    private static long _greedyMeshBlocksConsumed;

    private static long _entityLightBatchFrames;
    private static long _entityLightSamples;
    private static long _entityLightPreparedSamples;
    private static long _entityLightChunkGroups;
    private static long _entityLightFailedChunkGroups;
    private static long _entityLightCoordinateMismatches;
    private static long _entityLightChunkInvalidations;
    private static long _entityLightLockBatches;
    private static long _entityLightMaxBatchSize;
    private static long _entityLightTimedFrames;
    private static long _entityLightBatchTicks;

    private static long _entityShaderSegments;
    private static long _entityShaderUses;
    private static long _entityShaderUniformUploadsAvoided;
    private static long _entityShaderUboLookupsAvoided;

    public static void RecordEntityLightBatch(int samples, int preparedSamples, int chunkGroups, int failedChunkGroups, int lockBatches = 0, int maxBatchSize = 0, long elapsedTicks = 0)
    {
        Interlocked.Increment(ref _entityLightBatchFrames);
        Interlocked.Add(ref _entityLightSamples, samples);
        Interlocked.Add(ref _entityLightPreparedSamples, preparedSamples);
        Interlocked.Add(ref _entityLightChunkGroups, chunkGroups);
        Interlocked.Add(ref _entityLightFailedChunkGroups, failedChunkGroups);
        Interlocked.Add(ref _entityLightLockBatches, lockBatches);
        Interlocked.Add(ref _entityLightBatchTicks, elapsedTicks);
        if (elapsedTicks > 0)
        {
            Interlocked.Increment(ref _entityLightTimedFrames);
        }
        long observed = Volatile.Read(ref _entityLightMaxBatchSize);
        while (maxBatchSize > observed)
        {
            long previous = Interlocked.CompareExchange(ref _entityLightMaxBatchSize, maxBatchSize, observed);
            if (previous == observed)
            {
                break;
            }
            observed = previous;
        }
    }

    public static void RecordEntityLightCoordinateMismatch()
    {
        Interlocked.Increment(ref _entityLightCoordinateMismatches);
    }

    public static void RecordEntityLightChunkInvalidation()
    {
        Interlocked.Increment(ref _entityLightChunkInvalidations);
    }

    public static void RecordEntityShaderSegment(int useCount)
    {
        Interlocked.Increment(ref _entityShaderSegments);
        Interlocked.Add(ref _entityShaderUses, useCount);
        int sharedCallsAvoided = Math.Max(0, useCount - 1);
        Interlocked.Add(ref _entityShaderUniformUploadsAvoided, sharedCallsAvoided * 2L);
        Interlocked.Add(ref _entityShaderUboLookupsAvoided, sharedCallsAvoided);
    }

    public static void ResetEntityRenderP0()
    {
        Interlocked.Exchange(ref _entityLightBatchFrames, 0);
        Interlocked.Exchange(ref _entityLightSamples, 0);
        Interlocked.Exchange(ref _entityLightPreparedSamples, 0);
        Interlocked.Exchange(ref _entityLightChunkGroups, 0);
        Interlocked.Exchange(ref _entityLightFailedChunkGroups, 0);
        Interlocked.Exchange(ref _entityLightCoordinateMismatches, 0);
        Interlocked.Exchange(ref _entityLightChunkInvalidations, 0);
        Interlocked.Exchange(ref _entityLightLockBatches, 0);
        Interlocked.Exchange(ref _entityLightMaxBatchSize, 0);
        Interlocked.Exchange(ref _entityLightTimedFrames, 0);
        Interlocked.Exchange(ref _entityLightBatchTicks, 0);
        Interlocked.Exchange(ref _entityShaderSegments, 0);
        Interlocked.Exchange(ref _entityShaderUses, 0);
        Interlocked.Exchange(ref _entityShaderUniformUploadsAvoided, 0);
        Interlocked.Exchange(ref _entityShaderUboLookupsAvoided, 0);
    }

    /// <summary>
    /// One call per BuildBlockPolygons invocation from OptimumGreedyMeshEmitter
    /// (bug B7). quads/blocksConsumed are 0 when the chunk had no eligible
    /// interior faces this pass.
    /// </summary>
    public static void RecordGreedyMeshChunk(int quads, int blocksConsumed)
    {
        Interlocked.Increment(ref _greedyMeshChunks);
        Interlocked.Add(ref _greedyMeshQuads, quads);
        Interlocked.Add(ref _greedyMeshBlocksConsumed, blocksConsumed);
    }

    public static void ResetGreedyMesh()
    {
        Interlocked.Exchange(ref _greedyMeshChunks, 0);
        Interlocked.Exchange(ref _greedyMeshQuads, 0);
        Interlocked.Exchange(ref _greedyMeshBlocksConsumed, 0);
    }

    public static string GetGreedyMeshSummary()
    {
        long chunks = Interlocked.Read(ref _greedyMeshChunks);
        long quads = Interlocked.Read(ref _greedyMeshQuads);
        long blocksConsumed = Interlocked.Read(ref _greedyMeshBlocksConsumed);
        double blocksPerQuad = quads == 0 ? 0 : (double)blocksConsumed / quads;

        // The memory the merge actually removed from the chunk pools:
        // each vanilla quad a merge absorbed would have been one FaceData
        // struct (64 bytes, std430: 3x vec3 padded + uv + uvSize + ivec4
        // flags + colormapData) plus 6 ints of indices (24 bytes) on the
        // SSBO path. The GL 3.3 vertex path is in the same ballpark
        // (4 verts x ~32 bytes + indices), and the emitter only merges on
        // the SSBO path anyway, so one number is honest enough here.
        long quadsSaved = blocksConsumed - quads;
        double poolMBSaved = quadsSaved * 88.0 / (1024.0 * 1024.0);

        return $"Optimum greedy mesh: enabled={OptimumConfig.GreedyMeshEnabled}, maxMergeWidth={OptimumConfig.GreedyMeshMaxMergeWidth}, maxMergeHeight={OptimumConfig.GreedyMeshMaxMergeHeight}, lightTolerance={OptimumConfig.GreedyMeshLightTolerance}, farDistance={OptimumConfig.GreedyMeshFarDistance}, chunks={chunks}, quads={quads}, blocksConsumed={blocksConsumed}, blocksPerQuad={blocksPerQuad:0.00}, quadsSaved={quadsSaved}, estPoolMBSaved={poolMBSaved:0.00}";
    }

    public static void RecordChiselLod(int fullTriangles, int proxyTriangles, bool fallback, long elapsedTicks)
    {
        Interlocked.Increment(ref _chiselLodBlocks);
        Interlocked.Increment(ref _chiselLodFullMeshContributions);
        Interlocked.Add(ref _chiselLodFullTriangles, fullTriangles);
        Interlocked.Add(ref _chiselLodTesselationTicks, elapsedTicks);

        if (fallback)
        {
            Interlocked.Increment(ref _chiselLodFallbackMeshContributions);
        }
        else
        {
            Interlocked.Increment(ref _chiselLodProxyMeshContributions);
            Interlocked.Add(ref _chiselLodProxyTriangles, proxyTriangles);
        }
    }

    public static void ResetChiselLod()
    {
        Interlocked.Exchange(ref _chiselLodBlocks, 0);
        Interlocked.Exchange(ref _chiselLodFullMeshContributions, 0);
        Interlocked.Exchange(ref _chiselLodProxyMeshContributions, 0);
        Interlocked.Exchange(ref _chiselLodFallbackMeshContributions, 0);
        Interlocked.Exchange(ref _chiselLodFullTriangles, 0);
        Interlocked.Exchange(ref _chiselLodProxyTriangles, 0);
        Interlocked.Exchange(ref _chiselLodTesselationTicks, 0);
    }

    public static string GetChiselLodSummary()
    {
        long blocks = Interlocked.Read(ref _chiselLodBlocks);
        long fullMeshes = Interlocked.Read(ref _chiselLodFullMeshContributions);
        long proxyMeshes = Interlocked.Read(ref _chiselLodProxyMeshContributions);
        long fallbackMeshes = Interlocked.Read(ref _chiselLodFallbackMeshContributions);
        long fullTriangles = Interlocked.Read(ref _chiselLodFullTriangles);
        long proxyTriangles = Interlocked.Read(ref _chiselLodProxyTriangles);
        long ticks = Interlocked.Read(ref _chiselLodTesselationTicks);

        double proxyRate = blocks == 0 ? 0 : (double)proxyMeshes * 100.0 / blocks;
        double elapsedMs = ticks * 1000.0 / Stopwatch.Frequency;

        return $"Optimum chisel LOD: blocks={blocks}, fullMeshes={fullMeshes}, proxyMeshes={proxyMeshes}, proxyRate={proxyRate:0.0}%, fallbackMeshes={fallbackMeshes}, fullTriangles={fullTriangles}, proxyTriangles={proxyTriangles}, microblockTesselationMs={elapsedMs:0.###}";
    }

    // Chisel LOD shadow-pass cull diagnostics (OptimumApiBridge.InFrustumShadowPass, added
    // 92a4c72). Reset once per frame by the stutter watch below, so the summary always
    // reflects only the current frame's cost rather than a lifetime total.
    private static long _chiselShadowCullCalls;
    private static long _chiselShadowCullTicks;

    public static void RecordChiselShadowCull(long elapsedTicks)
    {
        Interlocked.Increment(ref _chiselShadowCullCalls);
        Interlocked.Add(ref _chiselShadowCullTicks, elapsedTicks);
    }

    public static void ResetChiselShadowCull()
    {
        Interlocked.Exchange(ref _chiselShadowCullCalls, 0);
        Interlocked.Exchange(ref _chiselShadowCullTicks, 0);
    }

    public static string GetChiselShadowCullSummary()
    {
        long calls = Interlocked.Read(ref _chiselShadowCullCalls);
        long ticks = Interlocked.Read(ref _chiselShadowCullTicks);
        double elapsedMs = ticks * 1000.0 / Stopwatch.Frequency;
        return $"Optimum chisel shadow cull: calls={calls}, elapsedMs={elapsedMs:0.###}";
    }

    /// <summary>
    /// Opt-in per-frame stutter diagnostic. When enabled, ClientPlatformWindows logs
    /// <see cref="BuildStutterReport"/> to the client log for every frame at or above
    /// <see cref="StutterWatchThresholdMs"/>, attributing the frame to Optimum's own
    /// per-frame subsystems so a slow frame can be traced without an external profiler.
    /// Session-only: not persisted, toggled via the ".optimum stutterwatch" command.
    /// </summary>
    public static volatile bool StutterWatchEnabled;
    public static volatile int StutterWatchThresholdMs = 25;

    /// <summary>
    /// Attributes ONLY the Optimum-owned per-frame subsystems below (each counter is reset
    /// every frame by <see cref="ResetPerFrameStutterCounters"/>, so every number here is
    /// this frame's cost, not a lifetime total). It cannot see vanilla's own baseline render
    /// cost, GC pauses, disk I/O, or GPU/driver stalls - a stutter with none of these
    /// subsystems standing out did NOT come from Optimum's own code, and needs vanilla's
    /// own frame profiler (".debug logticks &lt;ms&gt;", logs the full vanilla+Optimum stage
    /// breakdown to the client log) or an external trace to localize.
    /// </summary>
    public static string BuildStutterReport(double frameMs)
    {
        var sb = new StringBuilder();
        sb.Append($"[Optimum stutter-watch] frame took {frameMs:0.##} ms (threshold {StutterWatchThresholdMs} ms) - Optimum-owned subsystems only, see \".debug logticks\" for the full vanilla+Optimum breakdown:");
        sb.Append("\n  ").Append(GetChiselShadowCullSummary());
        sb.Append("\n  ").Append(GetChiselLodSummary());
        sb.Append("\n  ").Append(GetAnimBlockSummary());
        sb.Append("\n  ").Append(GetGreedyMeshSummary());
        sb.Append("\n  ").Append(GetChunkRenderSummary());
        sb.Append("\n  ").Append(GetChunkUploadSummary());
        {
            var (hits, skips) = EntityTesselationBudget.Snapshot();
            sb.Append($"\n  Optimum entity tesselation budget: tesselated={hits}, deferred={skips}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resets every counter fed into <see cref="BuildStutterReport"/>. Called once per frame
    /// by ClientPlatformWindows when stutter-watch is enabled, so each counter always
    /// reflects only the frame that just ended rather than an accumulating lifetime total.
    /// </summary>
    public static void ResetPerFrameStutterCounters()
    {
        ResetChiselShadowCull();
        ResetChiselLod();
        ResetAnimBlock();
        ResetGreedyMesh();
        ResetChunkRender();
        ResetChunkUpload();
        EntityTesselationBudget.Reset();
    }

    // Entity re-tesselation frame budget (EntityTesselationFrameBudget). Reset once per frame
    // from SystemRenderEntities.OnBeforeRender; consumed by EntityShapeRenderer.BeforeRender
    // before calling TesselateShape() on an entity that already has a mesh (first tesselation
    // is never gated). Interlocked since entity rendering only runs on the render thread today,
    // but the check-and-decrement must still be atomic against itself for correctness.
    private static int _entityTesselationBudgetRemaining;

    public static void ResetEntityTesselationBudget()
    {
        Volatile.Write(ref _entityTesselationBudgetRemaining, OptimumConfig.EntityTesselationFrameBudget);
    }

    /// <summary>
    /// Returns true if a re-tesselation is allowed this frame (and consumes one unit of
    /// budget), false if the frame's budget is already spent. A budget of 0 disables the cap
    /// entirely (always returns true).
    /// </summary>
    public static bool TryConsumeEntityTesselationBudget()
    {
        if (OptimumConfig.EntityTesselationFrameBudget <= 0) return true;

        while (true)
        {
            int current = Volatile.Read(ref _entityTesselationBudgetRemaining);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref _entityTesselationBudgetRemaining, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Accumulates the animator.OnFrame cost for animated blocks that actually
    /// ran this frame (near tier, or mid tier on a due frame). Two timestamp
    /// reads per call is noise next to the OnFrame work itself.
    /// </summary>
    public static void RecordAnimBlockTicks(long elapsedTicks)
    {
        Interlocked.Increment(ref _animBlockRuns);
        Interlocked.Add(ref _animBlockTicks, elapsedTicks);
    }

    public static void ResetAnimBlock()
    {
        Interlocked.Exchange(ref _animBlockRuns, 0);
        Interlocked.Exchange(ref _animBlockTicks, 0);
    }

    public static string GetAnimBlockSummary()
    {
        long runs = Interlocked.Read(ref _animBlockRuns);
        long ticks = Interlocked.Read(ref _animBlockTicks);
        double elapsedMs = ticks * 1000.0 / Stopwatch.Frequency;

        return $"Optimum anim block LOD: runs={runs}, animatorMs={elapsedMs:0.###}";
    }

    /// <summary>
    /// Lock-free hit/skip pair for one optimization. Hit means the full
    /// (vanilla-equivalent) path ran; skip means the optimization's fast
    /// path fired instead. A single Interlocked.Increment per call, no
    /// allocation, safe to call from a per-frame or per-entity hot path.
    /// </summary>
    public sealed class HitSkipCounter
    {
        private long _hits;
        private long _skips;

        public void Hit() => Interlocked.Increment(ref _hits);
        public void Skip() => Interlocked.Increment(ref _skips);

        public void Reset()
        {
            Interlocked.Exchange(ref _hits, 0);
            Interlocked.Exchange(ref _skips, 0);
        }

        public (long Hits, long Skips) Snapshot() => (Interlocked.Read(ref _hits), Interlocked.Read(ref _skips));
    }

    public static readonly HitSkipCounter EntityShadowCull = new();
    public static readonly HitSkipCounter EntityRenderCull = new();
    public static readonly HitSkipCounter DynamicLightRadius = new();
    public static readonly HitSkipCounter BackgroundFpsLimiter = new();
    public static readonly HitSkipCounter PreciseFramePacing = new();
    public static readonly HitSkipCounter HudEntityNameTags = new();
    public static readonly HitSkipCounter ShadowFarVegetation = new();
    public static readonly HitSkipCounter RepulseAgents = new();
    public static readonly HitSkipCounter WeatherWindThrottle = new();
    public static readonly HitSkipCounter AnimBlockLodNear = new();
    public static readonly HitSkipCounter AnimBlockLodMid = new();
    public static readonly HitSkipCounter AnimBlockLodFar = new();
    public static readonly HitSkipCounter AnimBlockLodDeferred = new();
    public static readonly HitSkipCounter ParticleDistanceGate = new();
    public static readonly HitSkipCounter OcclusionCullingScale = new();
    public static readonly HitSkipCounter DynamicLightCache = new();
    public static readonly HitSkipCounter ChunkUploadSort = new();
    public static readonly HitSkipCounter EntityLightBatch = new();
    public static readonly HitSkipCounter EntityShaderStateCache = new();
    public static readonly HitSkipCounter EntityTesselationBudget = new();
    public static readonly HitSkipCounter EntityOutfitShapeCache = new();
    public static readonly HitSkipCounter EntityOutfitAnimatorCache = new();

    /// <summary>
    /// Every hit/skip counter above, keyed by name, for .optimum status and
    /// the coverage test that keeps this list honest. Declared after the
    /// individual fields so their static initializers have already run.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, HitSkipCounter> Counters = new Dictionary<string, HitSkipCounter>
    {
        [nameof(EntityShadowCull)] = EntityShadowCull,
        [nameof(EntityRenderCull)] = EntityRenderCull,
        [nameof(DynamicLightRadius)] = DynamicLightRadius,
        [nameof(BackgroundFpsLimiter)] = BackgroundFpsLimiter,
        [nameof(PreciseFramePacing)] = PreciseFramePacing,
        [nameof(HudEntityNameTags)] = HudEntityNameTags,
        [nameof(ShadowFarVegetation)] = ShadowFarVegetation,
        [nameof(RepulseAgents)] = RepulseAgents,
        [nameof(WeatherWindThrottle)] = WeatherWindThrottle,
        [nameof(AnimBlockLodNear)] = AnimBlockLodNear,
        [nameof(AnimBlockLodMid)] = AnimBlockLodMid,
        [nameof(AnimBlockLodFar)] = AnimBlockLodFar,
        [nameof(AnimBlockLodDeferred)] = AnimBlockLodDeferred,
        [nameof(ParticleDistanceGate)] = ParticleDistanceGate,
        [nameof(OcclusionCullingScale)] = OcclusionCullingScale,
        [nameof(DynamicLightCache)] = DynamicLightCache,
        [nameof(ChunkUploadSort)] = ChunkUploadSort,
        [nameof(EntityLightBatch)] = EntityLightBatch,
        [nameof(EntityShaderStateCache)] = EntityShaderStateCache,
        [nameof(EntityTesselationBudget)] = EntityTesselationBudget,
        [nameof(EntityOutfitShapeCache)] = EntityOutfitShapeCache,
        [nameof(EntityOutfitAnimatorCache)] = EntityOutfitAnimatorCache,
    };

    public static void ResetAllCounters()
    {
        foreach (var counter in Counters.Values)
        {
            counter.Reset();
        }
        ResetChiselLod();
        ResetAnimBlock();
        ResetGreedyMesh();
        ResetEntityRenderP0();
    }

    // Chunk render diagnostics (Phase 1 for rank 2 command batching evaluation)
    private static long _chunkRenderFrames;
    private static long _chunkDrawCalls;
    private static long _chunkPoolsRendered;
    private static long _chunkVisibleGroups;
    private static long _chunkFrustumCullTicks;

    /// <summary>
    /// Called once per MeshDataPool.RenderMesh invocation (one MultiDrawElements call).
    /// </summary>
    public static void RecordChunkDrawCall(int groupCount)
    {
        Interlocked.Increment(ref _chunkDrawCalls);
        Interlocked.Add(ref _chunkVisibleGroups, groupCount);
    }

    /// <summary>
    /// Called once per MeshDataPoolManager.Render (one per pass+atlas combination).
    /// poolsRendered = pools with groupCount > 0.
    /// </summary>
    public static void RecordChunkRenderPass(int poolsRendered)
    {
        Interlocked.Increment(ref _chunkRenderFrames);
        Interlocked.Add(ref _chunkPoolsRendered, poolsRendered);
    }

    /// <summary>
    /// Accumulates frustum cull time across all pools in one frame.
    /// </summary>
    public static void RecordChunkFrustumCullTicks(long ticks)
    {
        Interlocked.Add(ref _chunkFrustumCullTicks, ticks);
    }

    public static void ResetChunkRender()
    {
        Interlocked.Exchange(ref _chunkRenderFrames, 0);
        Interlocked.Exchange(ref _chunkDrawCalls, 0);
        Interlocked.Exchange(ref _chunkPoolsRendered, 0);
        Interlocked.Exchange(ref _chunkVisibleGroups, 0);
        Interlocked.Exchange(ref _chunkFrustumCullTicks, 0);
    }

    // Chunk upload diagnostics (Phase 1 for rank 3 persistent mapped upload)
    private static long _chunkUploadFrames;
    private static long _chunkUploadBytes;
    private static long _chunkUploadCalls;
    private static long _chunkUploadTicks;

    /// <summary>
    /// Called per updateVAO invocation on the persistent path.
    /// </summary>
    public static void RecordChunkUpload(int bytes, long ticks)
    {
        Interlocked.Increment(ref _chunkUploadCalls);
        Interlocked.Add(ref _chunkUploadBytes, bytes);
        Interlocked.Add(ref _chunkUploadTicks, ticks);
    }

    /// <summary>
    /// Called once per frame from the upload limiter to mark frame boundaries.
    /// </summary>
    public static void RecordChunkUploadFrame()
    {
        Interlocked.Increment(ref _chunkUploadFrames);
    }

    public static void ResetChunkUpload()
    {
        Interlocked.Exchange(ref _chunkUploadFrames, 0);
        Interlocked.Exchange(ref _chunkUploadBytes, 0);
        Interlocked.Exchange(ref _chunkUploadCalls, 0);
        Interlocked.Exchange(ref _chunkUploadTicks, 0);
    }

    public static string GetChunkUploadSummary()
    {
        long frames = Interlocked.Read(ref _chunkRenderFrames); // reuse render frame counter as proxy
        long bytes = Interlocked.Read(ref _chunkUploadBytes);
        long calls = Interlocked.Read(ref _chunkUploadCalls);
        long ticks = Interlocked.Read(ref _chunkUploadTicks);
        double ms = ticks * 1000.0 / Stopwatch.Frequency;

        double bytesPerFrame = frames == 0 ? 0 : (double)bytes / frames;
        double callsPerFrame = frames == 0 ? 0 : (double)calls / frames;
        double msPerFrame = frames == 0 ? 0 : ms / frames;
        double mbTotal = bytes / (1024.0 * 1024.0);

        return $"Optimum chunk upload: frames={frames}, calls/frame={callsPerFrame:0.0}, KB/frame={bytesPerFrame / 1024:0.0}, uploadMs/frame={msPerFrame:0.###}, totalMB={mbTotal:0.0}, totalMs={ms:0.###}";
    }

    public static string GetChunkRenderSummary()
    {
        long frames = Interlocked.Read(ref _chunkRenderFrames);
        long draws = Interlocked.Read(ref _chunkDrawCalls);
        long pools = Interlocked.Read(ref _chunkPoolsRendered);
        long groups = Interlocked.Read(ref _chunkVisibleGroups);
        long cullTicks = Interlocked.Read(ref _chunkFrustumCullTicks);
        double cullMs = cullTicks * 1000.0 / Stopwatch.Frequency;

        double drawsPerFrame = frames == 0 ? 0 : (double)draws / frames;
        double poolsPerFrame = frames == 0 ? 0 : (double)pools / frames;
        double groupsPerFrame = frames == 0 ? 0 : (double)groups / frames;
        double cullMsPerFrame = frames == 0 ? 0 : cullMs / frames;

        return $"Optimum chunk render: frames={frames}, drawCalls/frame={drawsPerFrame:0.0}, poolsRendered/frame={poolsPerFrame:0.0}, visibleGroups/frame={groupsPerFrame:0.0}, frustumCullMs/frame={cullMsPerFrame:0.###}, totalCullMs={cullMs:0.###}";
    }

    public static string GetCountersSummary()
    {
        var sb = new StringBuilder("Optimum counters (hit=ran full path, skip=fast-pathed):");
        foreach (var (name, counter) in Counters)
        {
            var (hits, skips) = counter.Snapshot();
            long total = hits + skips;
            double skipRate = total == 0 ? 0 : skips * 100.0 / total;
            sb.Append($"\n  {name}: hits={hits}, skips={skips}, skipRate={skipRate:0.0}%");
        }
        double entityLightBatchMs = Interlocked.Read(ref _entityLightBatchTicks) * 1000.0 / Stopwatch.Frequency;
        sb.Append($"\n  EntityLightBatchTotals: frames={Interlocked.Read(ref _entityLightBatchFrames)}, samples={Interlocked.Read(ref _entityLightSamples)}, prepared={Interlocked.Read(ref _entityLightPreparedSamples)}, chunkGroups={Interlocked.Read(ref _entityLightChunkGroups)}, failedChunkGroups={Interlocked.Read(ref _entityLightFailedChunkGroups)}, coordinateMismatches={Interlocked.Read(ref _entityLightCoordinateMismatches)}, chunkInvalidations={Interlocked.Read(ref _entityLightChunkInvalidations)}, lockBatches={Interlocked.Read(ref _entityLightLockBatches)}, maxBatchSize={Interlocked.Read(ref _entityLightMaxBatchSize)}, timedFrames={Interlocked.Read(ref _entityLightTimedFrames)}, sampledBatchMs={entityLightBatchMs:0.###}");
        sb.Append($"\n  EntityShaderStateCacheTotals: segments={Interlocked.Read(ref _entityShaderSegments)}, uses={Interlocked.Read(ref _entityShaderUses)}, uniformUploadsAvoided={Interlocked.Read(ref _entityShaderUniformUploadsAvoided)}, uboLookupsAvoided={Interlocked.Read(ref _entityShaderUboLookupsAvoided)}");
        return sb.ToString();
    }
}
