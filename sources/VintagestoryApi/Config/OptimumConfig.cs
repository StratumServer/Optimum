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
    public const string Version = "0.3.14";

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
    /// Hysteresis factor for entity/chisel render distance. Entities that were
    /// visible last frame stay visible until they exceed innerRadius * 1.1.
    /// (1.1)^2 = 1.21, precomputed to avoid sqrt in the render loop.
    /// </summary>
    public static double HysteresisFactorSq = 1.21;

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
    /// Lets diagnostic runs share worldgen passes across worker threads.
    /// Optimum suspends automatic worker scheduling until worldgen R1 isolates
    /// mutable generator state. The environment override controls experiments.
    /// </summary>
    public static bool WorldgenWorkStealingEnabled = false;

    /// <summary>
    /// Runtime cap for client tessellation workers. The startup scan lowers it
    /// to one when a foreign texture source can race on singleton state.
    /// </summary>
    public static volatile int TesselationWorkerCap = int.MaxValue;

    /// <summary>
    /// Maximum number of completed meshes waiting for render-thread upload.
    /// </summary>
    public static int TesselationUploadQueueCapacity = 128;

    /// <summary>
    /// Allows parallel worldgen with assemblies outside the audited set.
    /// This override can produce unsafe worldgen state and stays off by default.
    /// </summary>
    public static volatile bool WorldgenConcurrencyForce;

    /// <summary>
    /// Worldgen worker policy. Controls how many additional worldgen threads
    /// run alongside the main chunk thread.
    /// Values: "auto" (detect hardware), "serial" (0 workers), "1", "2", "3".
    /// Default "auto": serial on systems with 8 or fewer logical processors,
    /// 1 worker on systems with more than 8.
    /// Environment variables OPTIMUM_WORLDGEN_MT and OPTIMUM_WORLDGEN_WORKERS
    /// override this setting for benchmark automation.
    /// </summary>
    public static string WorldgenWorkerPolicy = "auto";

    /// <summary>
    /// Replace the vanilla ChunkMapLayer upload pipeline with a page-cached
    /// renderer. Pages (8x8 chunks, 256x256 pixels) persist to disk as
    /// zstd-compressed RGBA and upload to a GL_TEXTURE_2D_ARRAY for
    /// single-draw-call rendering. Explored chunks never re-generate on map
    /// open: the cache serves them in sub-100ms. Default true.
    /// </summary>
    public static bool MapPageCacheEnabled = true;

    public static bool EffectiveMapPageCache => MapPageCacheEnabled &&
        !IsShaderFeatureDisabled("MapPageCache");

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
    /// When true, ExecuteMainThreadTasks drains multiple launch tasks per frame
    /// within LaunchTaskBudgetMs. When false, vanilla one-per-frame behavior.
    /// Default false (opt-in experiment).
    /// </summary>
    public static volatile bool LaunchTaskBudgetEnabled;

    /// <summary>
    /// Per-frame time budget in milliseconds for draining launch tasks.
    /// Clamped [1, 500]. Only effective when LaunchTaskBudgetEnabled is true.
    /// </summary>
    public static volatile int LaunchTaskBudgetMs = 100;

    /// <summary>
    /// Parallelize ServerChunk.FromBytes deserialization after the read pool
    /// fetches raw bytes. Safe because each FromBytes call creates a new
    /// ServerChunk instance with no shared mutable state.
    /// </summary>
    public static volatile bool ChunkDeserializeParallel = true;

    /// <summary>
    /// Minimum chunkMapSizeY required before parallel deserialization kicks in.
    /// Below this threshold the overhead of Parallel.For exceeds serial.
    /// Clamped [2, 64].
    /// </summary>
    public static volatile int ChunkDeserializeParallelMinY = 4;

    /// <summary>
    /// Move shader asset I/O, ToText decode, and LoadShaderProgram to a
    /// background thread during loadRegisteredShaderPrograms. GL calls
    /// (Compile, SetCustomSampler) stay on the GL thread.
    /// </summary>
    public static volatile bool ShaderPreprocessParallel = true;

    /// <summary>
    /// FSR render scale: 1.0 = native (off), 0.85 = quality, 0.77 = balanced, 0.67 = performance.
    /// Multiplies ssaaLevel in SetupDefaultFrameBuffers. Disables FXAA when < 1.0.
    /// </summary>
    public static float RenderScale = 1.0f;

    /// <summary>
    /// R4: cap the god-rays post-process at 100 texture samples when enabled.
    /// The disabled path sends the vanilla 180-sample limit. This option can
    /// change the post-process image, so it stays off by default.
    /// </summary>
    public const int VanillaGodRaysSampleLimit = 180;
    public const int OptimumGodRaysSampleLimit = 100;
    public static bool GodRaysSampleCapEnabled = false;
    public static int GodRaysSampleLimit => EffectiveGodRaysSampleCap
        ? OptimumGodRaysSampleLimit
        : VanillaGodRaysSampleLimit;

    // Compatibility state comes from the launcher's metadata scan. It stays
    // outside OptimumConfigData so a mod cannot make a runtime fallback
    // persistent by changing its shader files.
    private static readonly HashSet<string> _shaderCompatibilityDisabledFeatures = new(StringComparer.OrdinalIgnoreCase);
    private static bool _shaderCompatibilityScanFailed;
    private static bool _greedyMeshVertexShaderReady;
    private static bool _greedyMeshFragmentShaderReady;
    private static string? _shaderCompatibilityFingerprint;
    private static string? _dataPath;

    public static bool EffectiveGreedyMesh => GreedyMeshEnabled &&
        !IsShaderFeatureDisabled("GreedyMesh") &&
        _greedyMeshVertexShaderReady && _greedyMeshFragmentShaderReady;

    public static float EffectiveRenderScale => IsShaderFeatureDisabled("RenderScale") ? 1.0f : RenderScale;

    public static bool EffectiveGodRaysSampleCap => GodRaysSampleCapEnabled &&
        !IsShaderFeatureDisabled("GodRaysSampleCap");

    public static bool EffectiveEntityLightBatch => EntityLightBatchEnabled &&
        !IsShaderFeatureDisabled("EntityLightBatch");

    public static bool EffectiveEntityShaderStateCache => EntityShaderStateCacheEnabled &&
        !IsShaderFeatureDisabled("EntityShaderStateCache");

    public static bool EffectiveOit => !IsShaderFeatureDisabled("Oit");

    public static bool EffectiveShaderPreprocessParallel => ShaderPreprocessParallel &&
        !IsShaderFeatureDisabled("ShaderPreprocessParallel");

    public static bool IsShaderFeatureDisabled(string feature) =>
        _shaderCompatibilityScanFailed || _shaderCompatibilityDisabledFeatures.Contains(feature);

    public static void SetGreedyMeshShaderAbi(bool vertexShaderReady, bool fragmentShaderReady)
    {
        _greedyMeshVertexShaderReady = vertexShaderReady;
        _greedyMeshFragmentShaderReady = fragmentShaderReady;
        GreedyMeshShadersCompiledOn = vertexShaderReady && fragmentShaderReady;
    }

    public static void ResetShaderCompatibilityAfterReload()
    {
        SetGreedyMeshShaderAbi(false, false);
    }

    private static void LoadShaderCompatibilityReport()
    {
        _shaderCompatibilityDisabledFeatures.Clear();
        _shaderCompatibilityScanFailed = true;
        _shaderCompatibilityFingerprint = null;
        ResetShaderCompatibilityAfterReload();

        if (_dataPath == null) return;

        string reportPath = Path.Combine(_dataPath, ".optimum", "shader-compatibility.json");
        try
        {
            if (!File.Exists(reportPath)) return;

            string json = File.ReadAllText(reportPath);
            var report = JsonSerializer.Deserialize<ShaderCompatibilityState>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (report == null) return;

            if (report.DisabledFeatures != null)
            {
                foreach (string? feature in report.DisabledFeatures)
                {
                    if (!string.IsNullOrWhiteSpace(feature)) _shaderCompatibilityDisabledFeatures.Add(feature);
                }
            }

            _shaderCompatibilityScanFailed = report.ScanFailed;
            _shaderCompatibilityFingerprint = report.Fingerprint;
        }
        catch (Exception)
        {
            _shaderCompatibilityDisabledFeatures.Clear();
            _shaderCompatibilityScanFailed = true;
            _shaderCompatibilityFingerprint = null;
        }
    }

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
    /// Resolves the worker count from the persisted policy ("auto", "serial", "1"-"3").
    /// Serial on systems with 8 or fewer logical processors (APU/iGPU saturation).
    /// 1 worker on 8+ core systems with headroom for parallel terrain generation.
    /// </summary>
    public static int GetWorldgenWorkerCount(int logicalProcessors, bool reducedServerThreads)
    {
        if (reducedServerThreads) return 0;

        string policy = WorldgenWorkerPolicy ?? "auto";
        return policy switch
        {
            "serial" or "0" => 0,
            "1" => 1,
            "2" => Math.Min(2, Math.Max(0, logicalProcessors / 4 - 1)),
            "3" => Math.Min(3, Math.Max(0, logicalProcessors / 4 - 1)),
            _ => logicalProcessors > 8 ? 1 : 0, // "auto"
        };
    }

    /// <summary>
    /// Worker ceiling for adaptive scaling. Returns 0 when serial, otherwise
    /// the configured or auto-detected ceiling.
    /// </summary>
    public static int GetWorldgenWorkerCeiling(int logicalProcessors, bool reducedServerThreads)
    {
        if (reducedServerThreads) return 0;

        string policy = WorldgenWorkerPolicy ?? "auto";
        return policy switch
        {
            "serial" or "0" => 0,
            "1" => 1,
            "2" => 2,
            "3" => 3,
            _ => logicalProcessors > 8 ? 2 : 0, // "auto" ceiling
        };
    }

    /// <summary>
    /// Resolves the exact worker count for a diagnostic environment override.
    /// Priority: env var > config policy > auto default.
    /// Reduced-thread mode forces serial regardless.
    /// </summary>
    public static int ResolveWorldgenWorkerCount(
        int logicalProcessors,
        bool reducedServerThreads,
        string? mtOverride,
        string? workerCountOverride)
    {
        if (reducedServerThreads) return 0;

        // Env var explicit override (highest priority, for benchmarks)
        if (mtOverride == "1" && workerCountOverride != null)
        {
            return workerCountOverride switch
            {
                "1" => 1,
                "2" => 2,
                "3" => 3,
                _ => 0,
            };
        }

        // Env var explicitly disables
        if (mtOverride == "0") return 0;

        // Fall through to config-based policy
        return GetWorldgenWorkerCount(logicalProcessors, reducedServerThreads);
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
        (nameof(OptimumConfigData.GodRaysSampleCap), GodRaysSampleCapEnabled.ToString()),
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
        (nameof(OptimumConfigData.LaunchTaskBudgetEnabled), LaunchTaskBudgetEnabled.ToString()),
        (nameof(OptimumConfigData.LaunchTaskBudgetMs), LaunchTaskBudgetMs.ToString()),
        (nameof(OptimumConfigData.WorldgenWorkerPolicy), WorldgenWorkerPolicy),
        (nameof(OptimumConfigData.ChunkDeserializeParallel), ChunkDeserializeParallel.ToString()),
        (nameof(OptimumConfigData.ChunkDeserializeParallelMinY), ChunkDeserializeParallelMinY.ToString()),
        (nameof(OptimumConfigData.ShaderPreprocessParallel), ShaderPreprocessParallel.ToString()),
    };

    /// <summary>
    /// Set the data path root (e.g. GamePaths.DataPath). Call once at startup.
    /// </summary>
    public static void SetDataPath(string dataPath)
    {
        string dir = Path.Combine(dataPath, "ModConfig");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "optimum.json");
        _dataPath = dataPath;
        LoadShaderCompatibilityReport();
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
            GodRaysSampleCapEnabled = data.GodRaysSampleCap;
            MapPageCacheEnabled = data.MapPageCache;
            MapPageCacheMaxLayers = Math.Clamp(data.MapPageCacheMaxLayers, 16, 512);
            MapPageCacheBc7 = data.MapPageCacheBc7;
            RandomTickSliceEnabled = data.RandomTickSlice;
            WorldgenWorkStealingEnabled = data.WorldgenWorkStealing;
            ChunkReadPoolEnabled = data.ChunkReadPoolEnabled;
            ChunkReadPoolWorkers = Math.Clamp(data.ChunkReadPoolWorkers, 1, 8);
            ChunkDeserializeParallel = data.ChunkDeserializeParallel;
            ChunkDeserializeParallelMinY = Math.Clamp(data.ChunkDeserializeParallelMinY, 2, 64);
            ShaderPreprocessParallel = data.ShaderPreprocessParallel;
            AdaptiveRadiusEnabled = data.AdaptiveRadius;
            AdaptiveRadiusFloor = Math.Clamp(data.AdaptiveRadiusFloor, 1, 12);
            AdaptiveRadiusHighThreshold = Math.Max(1, data.AdaptiveRadiusHighThreshold);
            AdaptiveRadiusLowThreshold = Math.Max(1, data.AdaptiveRadiusLowThreshold);
            LaunchTaskBudgetEnabled = data.LaunchTaskBudgetEnabled;
            LaunchTaskBudgetMs = Math.Clamp(data.LaunchTaskBudgetMs, 1, 500);
            WorldgenWorkerPolicy = data.WorldgenWorkerPolicy ?? "auto";
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
            GodRaysSampleCap = GodRaysSampleCapEnabled,
            MapPageCache = MapPageCacheEnabled,
            MapPageCacheMaxLayers = MapPageCacheMaxLayers,
            MapPageCacheBc7 = MapPageCacheBc7,
            RandomTickSlice = RandomTickSliceEnabled,
            WorldgenWorkStealing = WorldgenWorkStealingEnabled,
            ChunkReadPoolEnabled = ChunkReadPoolEnabled,
            ChunkReadPoolWorkers = ChunkReadPoolWorkers,
            ChunkDeserializeParallel = ChunkDeserializeParallel,
            ChunkDeserializeParallelMinY = ChunkDeserializeParallelMinY,
            ShaderPreprocessParallel = ShaderPreprocessParallel,
            AdaptiveRadius = AdaptiveRadiusEnabled,
            AdaptiveRadiusFloor = AdaptiveRadiusFloor,
            AdaptiveRadiusHighThreshold = AdaptiveRadiusHighThreshold,
            AdaptiveRadiusLowThreshold = AdaptiveRadiusLowThreshold,
            LaunchTaskBudgetEnabled = LaunchTaskBudgetEnabled,
            LaunchTaskBudgetMs = LaunchTaskBudgetMs,
            WorldgenWorkerPolicy = WorldgenWorkerPolicy,
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

    private sealed class ShaderCompatibilityState
    {
        public bool ScanFailed { get; set; }
        public string? Fingerprint { get; set; }
        public List<string>? DisabledFeatures { get; set; }
    }
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
    public bool GodRaysSampleCap { get; set; } = false;
    public bool MapPageCache { get; set; } = true;
    public int MapPageCacheMaxLayers { get; set; } = 128;
    public bool MapPageCacheBc7 { get; set; } = true;
    public bool RandomTickSlice { get; set; } = true;
    public bool WorldgenWorkStealing { get; set; } = false;
    public bool ChunkReadPoolEnabled { get; set; } = true;
    public int ChunkReadPoolWorkers { get; set; } = 4;
    public bool ChunkDeserializeParallel { get; set; } = true;
    public int ChunkDeserializeParallelMinY { get; set; } = 4;
    public bool ShaderPreprocessParallel { get; set; } = true;
    public bool AdaptiveRadius { get; set; } = true;
    public int AdaptiveRadiusFloor { get; set; } = 4;
    public int AdaptiveRadiusHighThreshold { get; set; } = 60;
    public int AdaptiveRadiusLowThreshold { get; set; } = 20;
    public bool LaunchTaskBudgetEnabled { get; set; } = false;
    public int LaunchTaskBudgetMs { get; set; } = 100;
    public string WorldgenWorkerPolicy { get; set; } = "auto";
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
        sb.Append("\n  ").Append(GetEntityAnimationSummary());
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
        ResetChunkRenderFrame();
        ResetChunkUpload();
        ResetEntityAnimation();
        EntityTesselationBudget.Reset();
    }

    private const int EntityAnimationBandCount = 5;
    private const int EntityAnimationUnknownBand = 4;
    private static readonly string[] EntityAnimationBandNames = { "player", "near", "mid", "far", "unknown" };
    private static readonly long[] _entityAnimationManagerCalls = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationManagerSkips = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationHeadUpdates = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationPoseUpdates = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationPoseSkips = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationMatrixBuilds = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationMatrixSkips = new long[EntityAnimationBandCount];
    private static readonly long[] _entityAnimationLoopingSoundPasses = new long[EntityAnimationBandCount];
    private static long _entityAnimationMatrixTicks;

    [ThreadStatic]
    private static bool _entityAnimationContextActive;

    [ThreadStatic]
    private static int _entityAnimationBand;

    /// <summary>
    /// Sets the distance band for the entity whose animation manager runs next.
    /// Stutter-watch owns this context, so normal frames perform no distance work.
    /// </summary>
    public static void BeginEntityAnimationContext(bool isPlayer, double distanceSq)
    {
        if (!StutterWatchEnabled) return;

        _entityAnimationBand = isPlayer ? 0 : distanceSq <= 24.0 * 24.0 ? 1 : distanceSq <= 48.0 * 48.0 ? 2 : 3;
        _entityAnimationContextActive = true;
    }

    public static void EndEntityAnimationContext()
    {
        _entityAnimationContextActive = false;
    }

    public static void RecordEntityAnimationManagerCall() => AddEntityAnimationCount(_entityAnimationManagerCalls);
    public static void RecordEntityAnimationManagerSkip() => AddEntityAnimationCount(_entityAnimationManagerSkips);
    public static void RecordEntityAnimationHeadUpdate() => AddEntityAnimationCount(_entityAnimationHeadUpdates);
    public static void RecordEntityAnimationPoseUpdate() => AddEntityAnimationCount(_entityAnimationPoseUpdates);
    public static void RecordEntityAnimationPoseSkip() => AddEntityAnimationCount(_entityAnimationPoseSkips);
    public static void RecordEntityAnimationMatrixBuild() => AddEntityAnimationCount(_entityAnimationMatrixBuilds);

    public static void RecordEntityAnimationMatrixTicks(long elapsedTicks)
    {
        Interlocked.Add(ref _entityAnimationMatrixTicks, elapsedTicks);
    }

    public static void RecordEntityAnimationMatrixSkip() => AddEntityAnimationCount(_entityAnimationMatrixSkips);
    public static void RecordEntityAnimationLoopingSoundPass() => AddEntityAnimationCount(_entityAnimationLoopingSoundPasses);

    private static void AddEntityAnimationCount(long[] counts)
    {
        if (!StutterWatchEnabled) return;
        int band = _entityAnimationContextActive ? _entityAnimationBand : EntityAnimationUnknownBand;
        Interlocked.Increment(ref counts[band]);
    }

    public static void ResetEntityAnimation()
    {
        Array.Clear(_entityAnimationManagerCalls);
        Array.Clear(_entityAnimationManagerSkips);
        Array.Clear(_entityAnimationHeadUpdates);
        Array.Clear(_entityAnimationPoseUpdates);
        Array.Clear(_entityAnimationPoseSkips);
        Array.Clear(_entityAnimationMatrixBuilds);
        Array.Clear(_entityAnimationMatrixSkips);
        Array.Clear(_entityAnimationLoopingSoundPasses);
        Interlocked.Exchange(ref _entityAnimationMatrixTicks, 0);
    }

    public static string GetEntityAnimationSummary()
    {
        long managerCalls = SumEntityAnimationCounts(_entityAnimationManagerCalls);
        long managerSkips = SumEntityAnimationCounts(_entityAnimationManagerSkips);
        long headUpdates = SumEntityAnimationCounts(_entityAnimationHeadUpdates);
        long poseUpdates = SumEntityAnimationCounts(_entityAnimationPoseUpdates);
        long poseSkips = SumEntityAnimationCounts(_entityAnimationPoseSkips);
        long matrixBuilds = SumEntityAnimationCounts(_entityAnimationMatrixBuilds);
        long matrixSkips = SumEntityAnimationCounts(_entityAnimationMatrixSkips);
        long loopingSoundPasses = SumEntityAnimationCounts(_entityAnimationLoopingSoundPasses);
        double matrixMs = Interlocked.Read(ref _entityAnimationMatrixTicks) * 1000.0 / Stopwatch.Frequency;

        var sb = new StringBuilder();
        sb.Append($"Optimum entity animation: managerCalls={managerCalls}, managerSkips={managerSkips}, headUpdates={headUpdates}, poseUpdates={poseUpdates}, poseSkips={poseSkips}, matrixBuilds={matrixBuilds}, matrixSkips={matrixSkips}, loopingSoundPasses={loopingSoundPasses}, matrixMs={matrixMs:0.###}, bands=");
        for (int i = 0; i < EntityAnimationBandCount; i++)
        {
            if (i > 0) sb.Append(';');
            sb.Append(EntityAnimationBandNames[i]).Append(':')
                .Append(Interlocked.Read(ref _entityAnimationManagerCalls[i])).Append('/')
                .Append(Interlocked.Read(ref _entityAnimationPoseUpdates[i])).Append('/')
                .Append(Interlocked.Read(ref _entityAnimationPoseSkips[i])).Append('/')
                .Append(Interlocked.Read(ref _entityAnimationMatrixBuilds[i])).Append('/')
                .Append(Interlocked.Read(ref _entityAnimationMatrixSkips[i]));
        }

        return sb.ToString();
    }

    private static long SumEntityAnimationCounts(long[] counts)
    {
        long total = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            total += Interlocked.Read(ref counts[i]);
        }

        return total;
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
        ResetTessellation();
        ResetEntityAnimation();
        ResetGameLaunchTasks();
        ResetWorldgenPassTiming();
    }

    // Game launch task diagnostics. The client intentionally runs at most one
    // launch task per frame. These counters measure the current policy before
    // any pacing change considers queue depth or task duration.
    private static long _gameLaunchTaskFrames;
    private static long _gameLaunchTaskCount;
    private static long _gameLaunchTaskTicks;
    private static long _gameLaunchTaskMaxTicks;
    private static long _gameLaunchTaskPeakDepth;

    public static void RecordGameLaunchTask(long elapsedTicks, int queueDepth)
    {
        Interlocked.Increment(ref _gameLaunchTaskCount);
        Interlocked.Add(ref _gameLaunchTaskTicks, elapsedTicks);
        UpdatePeak(ref _gameLaunchTaskMaxTicks, elapsedTicks);
        UpdatePeak(ref _gameLaunchTaskPeakDepth, queueDepth);
    }

    /// <summary>
    /// Called once per frame that processes at least one launch task.
    /// Separated from RecordGameLaunchTask so multi-task frames count
    /// as one frame but multiple tasks.
    /// </summary>
    public static void RecordGameLaunchTaskFrame()
    {
        Interlocked.Increment(ref _gameLaunchTaskFrames);
    }

    public static void ResetGameLaunchTasks()
    {
        Interlocked.Exchange(ref _gameLaunchTaskFrames, 0);
        Interlocked.Exchange(ref _gameLaunchTaskCount, 0);
        Interlocked.Exchange(ref _gameLaunchTaskTicks, 0);
        Interlocked.Exchange(ref _gameLaunchTaskMaxTicks, 0);
        Interlocked.Exchange(ref _gameLaunchTaskPeakDepth, 0);
    }

    public static string GetGameLaunchTaskSummary()
    {
        long frames = Interlocked.Read(ref _gameLaunchTaskFrames);
        long count = Interlocked.Read(ref _gameLaunchTaskCount);
        long totalTicks = Interlocked.Read(ref _gameLaunchTaskTicks);
        long maxTicks = Interlocked.Read(ref _gameLaunchTaskMaxTicks);
        double totalMs = totalTicks * 1000.0 / Stopwatch.Frequency;
        double averageMs = count == 0 ? 0 : totalMs / count;
        double maxMs = maxTicks * 1000.0 / Stopwatch.Frequency;
        double tasksPerFrame = frames == 0 ? 0 : (double)count / frames;

        return $"Optimum game launch tasks: frames={frames}, tasks={count}, tasks/frame={tasksPerFrame:0.00}, averageMs={averageMs:0.###}, maxMs={maxMs:0.###}, totalMs={totalMs:0.###}, peakQueueDepth={Interlocked.Read(ref _gameLaunchTaskPeakDepth)}";
    }

    private static void UpdatePeak(ref long target, long value)
    {
        long current = Interlocked.Read(ref target);
        while (value > current)
        {
            long previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }
            current = previous;
        }
    }

    // Chunk render diagnostics (Phase 1 for rank 2 command batching evaluation)
    private static long _chunkRenderFrames;
    private static long _chunkDrawCalls;
    private static long _chunkPoolsRendered;
    private static long _chunkPoolsCulled;
    private static long _chunkVisibleGroups;
    private static long _chunkFrustumCullTicks;
    private const int ChunkRenderWindowSize = 120;
    private static readonly long[] _chunkWindowDrawCalls = new long[ChunkRenderWindowSize];
    private static readonly long[] _chunkWindowPoolsRendered = new long[ChunkRenderWindowSize];
    private static readonly long[] _chunkWindowPoolsCulled = new long[ChunkRenderWindowSize];
    private static readonly long[] _chunkWindowVisibleGroups = new long[ChunkRenderWindowSize];
    private static readonly long[] _chunkWindowFrustumCullTicks = new long[ChunkRenderWindowSize];
    private static long _chunkWindowSampleCount;
    private static long _chunkWindowDrawCallsTotal;
    private static long _chunkWindowPoolsRenderedTotal;
    private static long _chunkWindowPoolsCulledTotal;
    private static long _chunkWindowVisibleGroupsTotal;
    private static long _chunkWindowFrustumCullTicksTotal;
    private static long _chunkWindowLastDrawCalls;
    private static long _chunkWindowLastPoolsRendered;
    private static long _chunkWindowLastPoolsCulled;
    private static long _chunkWindowLastVisibleGroups;
    private static long _chunkWindowLastFrustumCullTicks;
    private static long _chunkWindowNextSample;
    private static int _chunkWindowBaselineReady;

    /// <summary>
    /// Called once per MeshDataPool.RenderMesh invocation (one MultiDrawElements call).
    /// </summary>
    public static void RecordChunkDrawCall(int groupCount)
    {
        Interlocked.Increment(ref _chunkDrawCalls);
        Interlocked.Add(ref _chunkVisibleGroups, groupCount);
    }

    /// <summary>
    /// Called once per MeshDataPoolManager.Render.
    /// poolsRendered counts pools with visible groups in this render pass.
    /// poolsCulled counts normal-dimension pools with no visible groups after frustum culling.
    /// </summary>
    public static void RecordChunkRenderPass(int poolsRendered)
    {
        RecordChunkRenderPass(poolsRendered, 0);
    }

    public static void RecordChunkRenderPass(int poolsRendered, int poolsCulled)
    {
        Interlocked.Add(ref _chunkPoolsRendered, poolsRendered);
        Interlocked.Add(ref _chunkPoolsCulled, poolsCulled);
    }

    /// <summary>
    /// Called once per client frame from MeshDataPoolMasterManager.OnFrame.
    /// </summary>
    public static void RecordChunkRenderFrame()
    {
        Interlocked.Increment(ref _chunkRenderFrames);
        if (Interlocked.Exchange(ref _chunkWindowBaselineReady, 1) == 0)
        {
            SetChunkWindowBaseline();
            return;
        }

        RecordChunkWindowSample();
    }

    private static void SetChunkWindowBaseline()
    {
        Interlocked.Exchange(ref _chunkWindowLastDrawCalls, Interlocked.Read(ref _chunkDrawCalls));
        Interlocked.Exchange(ref _chunkWindowLastPoolsRendered, Interlocked.Read(ref _chunkPoolsRendered));
        Interlocked.Exchange(ref _chunkWindowLastPoolsCulled, Interlocked.Read(ref _chunkPoolsCulled));
        Interlocked.Exchange(ref _chunkWindowLastVisibleGroups, Interlocked.Read(ref _chunkVisibleGroups));
        Interlocked.Exchange(ref _chunkWindowLastFrustumCullTicks, Interlocked.Read(ref _chunkFrustumCullTicks));
    }

    private static void RecordChunkWindowSample()
    {
        long draws = Interlocked.Read(ref _chunkDrawCalls);
        long pools = Interlocked.Read(ref _chunkPoolsRendered);
        long culled = Interlocked.Read(ref _chunkPoolsCulled);
        long groups = Interlocked.Read(ref _chunkVisibleGroups);
        long cullTicks = Interlocked.Read(ref _chunkFrustumCullTicks);
        long sampleNumber = Interlocked.Increment(ref _chunkWindowNextSample);
        long drawDelta = draws - Interlocked.Exchange(ref _chunkWindowLastDrawCalls, draws);
        long poolDelta = pools - Interlocked.Exchange(ref _chunkWindowLastPoolsRendered, pools);
        long culledDelta = culled - Interlocked.Exchange(ref _chunkWindowLastPoolsCulled, culled);
        long groupDelta = groups - Interlocked.Exchange(ref _chunkWindowLastVisibleGroups, groups);
        long cullTickDelta = cullTicks - Interlocked.Exchange(ref _chunkWindowLastFrustumCullTicks, cullTicks);
        int slot = (int)((sampleNumber - 1) % ChunkRenderWindowSize);

        Interlocked.Add(ref _chunkWindowDrawCallsTotal, drawDelta - _chunkWindowDrawCalls[slot]);
        Interlocked.Add(ref _chunkWindowPoolsRenderedTotal, poolDelta - _chunkWindowPoolsRendered[slot]);
        Interlocked.Add(ref _chunkWindowPoolsCulledTotal, culledDelta - _chunkWindowPoolsCulled[slot]);
        Interlocked.Add(ref _chunkWindowVisibleGroupsTotal, groupDelta - _chunkWindowVisibleGroups[slot]);
        Interlocked.Add(ref _chunkWindowFrustumCullTicksTotal, cullTickDelta - _chunkWindowFrustumCullTicks[slot]);
        _chunkWindowDrawCalls[slot] = drawDelta;
        _chunkWindowPoolsRendered[slot] = poolDelta;
        _chunkWindowPoolsCulled[slot] = culledDelta;
        _chunkWindowVisibleGroups[slot] = groupDelta;
        _chunkWindowFrustumCullTicks[slot] = cullTickDelta;
        Interlocked.Exchange(ref _chunkWindowSampleCount, Math.Min(sampleNumber, ChunkRenderWindowSize));
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
        Interlocked.Exchange(ref _chunkPoolsCulled, 0);
        Interlocked.Exchange(ref _chunkVisibleGroups, 0);
        Interlocked.Exchange(ref _chunkFrustumCullTicks, 0);
        Interlocked.Exchange(ref _chunkWindowSampleCount, 0);
        Interlocked.Exchange(ref _chunkWindowDrawCallsTotal, 0);
        Interlocked.Exchange(ref _chunkWindowPoolsRenderedTotal, 0);
        Interlocked.Exchange(ref _chunkWindowPoolsCulledTotal, 0);
        Interlocked.Exchange(ref _chunkWindowVisibleGroupsTotal, 0);
        Interlocked.Exchange(ref _chunkWindowFrustumCullTicksTotal, 0);
        Interlocked.Exchange(ref _chunkWindowLastDrawCalls, 0);
        Interlocked.Exchange(ref _chunkWindowLastPoolsRendered, 0);
        Interlocked.Exchange(ref _chunkWindowLastPoolsCulled, 0);
        Interlocked.Exchange(ref _chunkWindowLastVisibleGroups, 0);
        Interlocked.Exchange(ref _chunkWindowLastFrustumCullTicks, 0);
        Interlocked.Exchange(ref _chunkWindowNextSample, 0);
        Interlocked.Exchange(ref _chunkWindowBaselineReady, 0);
        Array.Clear(_chunkWindowDrawCalls);
        Array.Clear(_chunkWindowPoolsRendered);
        Array.Clear(_chunkWindowPoolsCulled);
        Array.Clear(_chunkWindowVisibleGroups);
        Array.Clear(_chunkWindowFrustumCullTicks);
    }

    public static void ResetChunkRenderFrame()
    {
        if (Volatile.Read(ref _chunkWindowBaselineReady) != 0)
        {
            RecordChunkWindowSample();
        }

        Interlocked.Exchange(ref _chunkRenderFrames, 0);
        Interlocked.Exchange(ref _chunkDrawCalls, 0);
        Interlocked.Exchange(ref _chunkPoolsRendered, 0);
        Interlocked.Exchange(ref _chunkPoolsCulled, 0);
        Interlocked.Exchange(ref _chunkVisibleGroups, 0);
        Interlocked.Exchange(ref _chunkFrustumCullTicks, 0);
        Interlocked.Exchange(ref _chunkWindowLastDrawCalls, 0);
        Interlocked.Exchange(ref _chunkWindowLastPoolsRendered, 0);
        Interlocked.Exchange(ref _chunkWindowLastPoolsCulled, 0);
        Interlocked.Exchange(ref _chunkWindowLastVisibleGroups, 0);
        Interlocked.Exchange(ref _chunkWindowLastFrustumCullTicks, 0);
        Interlocked.Exchange(ref _chunkWindowBaselineReady, 0);
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
        long culled = Interlocked.Read(ref _chunkPoolsCulled);
        long groups = Interlocked.Read(ref _chunkVisibleGroups);
        long cullTicks = Interlocked.Read(ref _chunkFrustumCullTicks);
        long windowFrames = Interlocked.Read(ref _chunkWindowSampleCount);
        long windowDraws = Interlocked.Read(ref _chunkWindowDrawCallsTotal);
        long windowPools = Interlocked.Read(ref _chunkWindowPoolsRenderedTotal);
        long windowCulled = Interlocked.Read(ref _chunkWindowPoolsCulledTotal);
        long windowGroups = Interlocked.Read(ref _chunkWindowVisibleGroupsTotal);
        long windowCullTicks = Interlocked.Read(ref _chunkWindowFrustumCullTicksTotal);
        double cullMs = cullTicks * 1000.0 / Stopwatch.Frequency;
        double windowCullMs = windowCullTicks * 1000.0 / Stopwatch.Frequency;

        double drawsPerFrame = frames == 0 ? 0 : (double)draws / frames;
        double poolsPerFrame = frames == 0 ? 0 : (double)pools / frames;
        double culledPerFrame = frames == 0 ? 0 : (double)culled / frames;
        double groupsPerFrame = frames == 0 ? 0 : (double)groups / frames;
        double cullMsPerFrame = frames == 0 ? 0 : cullMs / frames;
        double windowDrawsPerFrame = windowFrames == 0 ? 0 : (double)windowDraws / windowFrames;
        double windowPoolsPerFrame = windowFrames == 0 ? 0 : (double)windowPools / windowFrames;
        double windowCulledPerFrame = windowFrames == 0 ? 0 : (double)windowCulled / windowFrames;
        double windowGroupsPerFrame = windowFrames == 0 ? 0 : (double)windowGroups / windowFrames;
        double windowCullMsPerFrame = windowFrames == 0 ? 0 : windowCullMs / windowFrames;

        return $"Optimum chunk render: frames={frames}, drawCalls/frame={drawsPerFrame:0.0}, poolsRendered/frame={poolsPerFrame:0.0}, poolsCulled/frame={culledPerFrame:0.0}, visibleGroups/frame={groupsPerFrame:0.0}, frustumCullMs/frame={cullMsPerFrame:0.###}, totalCullMs={cullMs:0.###}, windowFrames={windowFrames}, windowDrawCalls/frame={windowDrawsPerFrame:0.0}, windowPoolsRendered/frame={windowPoolsPerFrame:0.0}, windowPoolsCulled/frame={windowCulledPerFrame:0.0}, windowVisibleGroups/frame={windowGroupsPerFrame:0.0}, windowFrustumCullMs/frame={windowCullMsPerFrame:0.###}";
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
        sb.Append($"\n  {GetGameLaunchTaskSummary()}");
        return sb.ToString();
    }

    // Tessellation pipeline instrumentation (Phase 2, Steps 9-10)
    private static long _tessChunksProcessed;
    private static long _tessTotalTicks;
    private static long _tessPeakQueueDepth;
    private static long _tessCurrentQueueDepth;
    private static long _tessReadyToUploadTicks; // wall-clock from "chunk data ready" to "mesh uploaded"
    private static long _tessReadyToUploadCount;
    private static long _tessRetryRequeueTotal;
    private static long _tessRetryRequeueWorst; // worst per-chunk requeue count
    private static long _tessBackpressureCount;
    private static long _tessHandoffCapacity;
    private static long _tessHandoffPeak;
    private static readonly object _tessWorkerGate = new();
    private static readonly List<int> _tessWorkerIds = new();

    /// <summary>Called after each chunk tessellation completes (success or retry).</summary>
    public static void RecordTessellation(long elapsedTicks, int queueDepth)
    {
        Interlocked.Increment(ref _tessChunksProcessed);
        Interlocked.Add(ref _tessTotalTicks, elapsedTicks);

        // Update peak (lock-free CAS loop)
        long current = Interlocked.Read(ref _tessPeakQueueDepth);
        while (queueDepth > current)
        {
            long prev = Interlocked.CompareExchange(ref _tessPeakQueueDepth, queueDepth, current);
            if (prev == current) break;
            current = prev;
        }
        Volatile.Write(ref _tessCurrentQueueDepth, queueDepth);
    }

    /// <summary>Called when a tessellated chunk is uploaded to the render thread.</summary>
    public static void RecordTessUpload(long readyToUploadTicks)
    {
        Interlocked.Increment(ref _tessReadyToUploadCount);
        Interlocked.Add(ref _tessReadyToUploadTicks, readyToUploadTicks);
    }

    /// <summary>Called on each RetryTesselationException requeue.</summary>
    public static void RecordTessRetry(int perChunkRetryCount)
    {
        Interlocked.Increment(ref _tessRetryRequeueTotal);

        // Update worst per-chunk (lock-free CAS loop)
        long current = Interlocked.Read(ref _tessRetryRequeueWorst);
        while (perChunkRetryCount > current)
        {
            long prev = Interlocked.CompareExchange(ref _tessRetryRequeueWorst, perChunkRetryCount, current);
            if (prev == current) break;
            current = prev;
        }
    }

    /// <summary>Called when a completed mesh returns to the dirty queue because the handoff is full.</summary>
    public static void RecordTessBackpressure()
    {
        Interlocked.Increment(ref _tessBackpressureCount);
    }

    /// <summary>Called once from OptimumBoundedHandoff's constructor with its actual capacity.</summary>
    public static void RecordTessHandoffCapacity(int capacity)
    {
        Interlocked.Exchange(ref _tessHandoffCapacity, capacity);
    }

    /// <summary>Called on each successful OptimumBoundedHandoff.TryReserve with the new reserved count.</summary>
    public static void RecordTessHandoffReserved(int reserved)
    {
        long current = Interlocked.Read(ref _tessHandoffPeak);
        while (reserved > current)
        {
            long prev = Interlocked.CompareExchange(ref _tessHandoffPeak, reserved, current);
            if (prev == current) break;
            current = prev;
        }
    }

    /// <summary>Called from OptimumTesselationWorkerRegistry.Register on first registration of a thread id.</summary>
    public static void RecordTessWorkerRegistered(int threadId)
    {
        lock (_tessWorkerGate)
        {
            if (!_tessWorkerIds.Contains(threadId))
            {
                _tessWorkerIds.Add(threadId);
            }
        }
    }

    /// <summary>Check if a chunk has exceeded the retry threshold (50) and should log a warning.</summary>
    public static bool ShouldWarnTessRetry(int perChunkRetryCount) => perChunkRetryCount == 50;

    public static void ResetTessellation()
    {
        Interlocked.Exchange(ref _tessChunksProcessed, 0);
        Interlocked.Exchange(ref _tessTotalTicks, 0);
        Interlocked.Exchange(ref _tessPeakQueueDepth, 0);
        Interlocked.Exchange(ref _tessCurrentQueueDepth, 0);
        Interlocked.Exchange(ref _tessReadyToUploadTicks, 0);
        Interlocked.Exchange(ref _tessReadyToUploadCount, 0);
        Interlocked.Exchange(ref _tessRetryRequeueTotal, 0);
        Interlocked.Exchange(ref _tessRetryRequeueWorst, 0);
        Interlocked.Exchange(ref _tessBackpressureCount, 0);
        Interlocked.Exchange(ref _tessHandoffPeak, 0);
        lock (_tessWorkerGate)
        {
            _tessWorkerIds.Clear();
        }
    }

    public static string GetTessellationSummary()
    {
        long chunks = Interlocked.Read(ref _tessChunksProcessed);
        long ticks = Interlocked.Read(ref _tessTotalTicks);
        long peak = Interlocked.Read(ref _tessPeakQueueDepth);
        long currentQ = Volatile.Read(ref _tessCurrentQueueDepth);
        long uploadCount = Interlocked.Read(ref _tessReadyToUploadCount);
        long uploadTicks = Interlocked.Read(ref _tessReadyToUploadTicks);
        long retries = Interlocked.Read(ref _tessRetryRequeueTotal);
        long worstRetry = Interlocked.Read(ref _tessRetryRequeueWorst);
        long backpressure = Interlocked.Read(ref _tessBackpressureCount);
        long handoffCapacity = Interlocked.Read(ref _tessHandoffCapacity);
        long handoffPeak = Interlocked.Read(ref _tessHandoffPeak);

        double totalMs = ticks * 1000.0 / Stopwatch.Frequency;
        double meanMs = chunks == 0 ? 0 : totalMs / chunks;
        double uploadMs = uploadCount == 0 ? 0 : uploadTicks * 1000.0 / Stopwatch.Frequency / uploadCount;

        string workerIds;
        int workerCount;
        lock (_tessWorkerGate)
        {
            workerCount = _tessWorkerIds.Count;
            workerIds = string.Join(",", _tessWorkerIds);
        }

        return $"Optimum tessellation: chunks={chunks}, meanMs/chunk={meanMs:0.###}, queuePeak={peak}, queueNow={currentQ}, ready-to-upload meanMs={uploadMs:0.###}, retries={retries}, worstPerChunk={worstRetry}, backpressure={backpressure}, workers={workerCount} [ids={workerIds}], handoffPeak={handoffPeak}/{handoffCapacity}";
    }

    // Worldgen per-pass timing diagnostics (Step 33)
    private const int WorldgenPassCount = 6; // None(0), Terrain(1), TerrainFeatures(2), Vegetation(3), NeighbourSunLightFlood(4), PreDone(5)
    private static readonly long[] _worldgenPassTicks = new long[WorldgenPassCount];
    private static readonly long[] _worldgenPassColumns = new long[WorldgenPassCount];
    internal static long _worldgenTotalColumns;

    public static void RecordWorldgenPassTiming(int pass, long elapsedTicks)
    {
        if ((uint)pass >= WorldgenPassCount) return;
        Interlocked.Add(ref _worldgenPassTicks[pass], elapsedTicks);
        Interlocked.Increment(ref _worldgenPassColumns[pass]);
        Interlocked.Increment(ref _worldgenTotalColumns);
    }

    public static void ResetWorldgenPassTiming()
    {
        Array.Clear(_worldgenPassTicks);
        Array.Clear(_worldgenPassColumns);
        Interlocked.Exchange(ref _worldgenTotalColumns, 0);
    }

    private static readonly string[] WorldgenPassNames = { "None", "Terrain", "TerrainFeatures", "Vegetation", "SunLightFlood", "PreDone" };

    public static string GetWorldgenPassTimingSummary()
    {
        long totalColumns = Interlocked.Read(ref _worldgenTotalColumns);
        var sb = new StringBuilder();
        sb.Append($"Optimum worldgen pass timing: totalColumns={totalColumns}");
        for (int i = 1; i < WorldgenPassCount; i++)
        {
            long ticks = Interlocked.Read(ref _worldgenPassTicks[i]);
            long cols = Interlocked.Read(ref _worldgenPassColumns[i]);
            double totalMs = ticks * 1000.0 / Stopwatch.Frequency;
            double meanMs = cols == 0 ? 0 : totalMs / cols;
            sb.Append($", {WorldgenPassNames[i]}={meanMs:0.###}ms/col({cols}cols,{totalMs:0.#}ms)");
        }
        return sb.ToString();
    }

    // Chunk deserialization parallelism diagnostics
    private static long _chunkDeserializeParallelColumns;
    private static long _chunkDeserializeParallelChunks;

    public static void RecordChunkDeserializeParallel(int chunksInColumn)
    {
        Interlocked.Increment(ref _chunkDeserializeParallelColumns);
        Interlocked.Add(ref _chunkDeserializeParallelChunks, chunksInColumn);
    }

    public static void ResetChunkDeserializeParallel()
    {
        Interlocked.Exchange(ref _chunkDeserializeParallelColumns, 0);
        Interlocked.Exchange(ref _chunkDeserializeParallelChunks, 0);
    }

    public static string GetChunkDeserializeParallelSummary()
    {
        long columns = Interlocked.Read(ref _chunkDeserializeParallelColumns);
        long chunks = Interlocked.Read(ref _chunkDeserializeParallelChunks);
        return $"Optimum chunk deserialize parallel: columns={columns}, chunks={chunks}";
    }
}
