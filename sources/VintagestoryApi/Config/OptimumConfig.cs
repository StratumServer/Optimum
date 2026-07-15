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
    public const string Version = "0.2.11";

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
        (nameof(OptimumConfigData.GreedyMeshEnabled), GreedyMeshEnabled.ToString()),
        (nameof(OptimumConfigData.GreedyMeshMaxMergeWidth), GreedyMeshMaxMergeWidth.ToString()),
        (nameof(OptimumConfigData.GreedyMeshMaxMergeHeight), GreedyMeshMaxMergeHeight.ToString()),
        (nameof(OptimumConfigData.GreedyMeshLightTolerance), GreedyMeshLightTolerance.ToString()),
        (nameof(OptimumConfigData.GreedyMeshFarDistance), GreedyMeshFarDistance.ToString()),
        (nameof(OptimumConfigData.GreedyMeshTextureGrad), GreedyMeshTextureGrad.ToString()),
        (nameof(OptimumConfigData.RenderScale), RenderScale.ToString("F2")),
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
            GreedyMeshEnabled = GreedyMeshEnabled,
            GreedyMeshMaxMergeWidth = GreedyMeshMaxMergeWidth,
            GreedyMeshMaxMergeHeight = GreedyMeshMaxMergeHeight,
            GreedyMeshLightTolerance = GreedyMeshLightTolerance,
            GreedyMeshFarDistance = GreedyMeshFarDistance,
            GreedyMeshTextureGrad = GreedyMeshTextureGrad,
            RenderScale = RenderScale,
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
    public bool GreedyMeshEnabled { get; set; } = false;
    public int GreedyMeshMaxMergeWidth { get; set; } = 8;
    public int GreedyMeshMaxMergeHeight { get; set; } = 8;
    public int GreedyMeshLightTolerance { get; set; } = 0;
    public int GreedyMeshFarDistance { get; set; } = 0;
    public bool GreedyMeshTextureGrad { get; set; } = true;
    public float RenderScale { get; set; } = 1.0f;
}

public static class OptimumDiagnostics
{
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
