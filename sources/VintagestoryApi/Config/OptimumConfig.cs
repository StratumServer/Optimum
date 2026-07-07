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
    /// Mirrors VintagestoryLib.Optimum.OptimumInfo.Version. Forks (VSEssentials,
    /// VSSurvivalMod) reference this assembly, not the Lib donor, so the version
    /// string can't come from one shared constant; update both on release.
    /// </summary>
    public const string Version = "0.2.4";

    public static bool RepulsionGateEnabled = true;
    public static int RepulsionDistance = 64;
    public static double RepulsionDistanceSq = 64.0 * 64.0;

    public static bool AnimBlockLodEnabled = true;
    public static bool WeatherWindThrottleEnabled = true;
    public static bool ParticleDistanceGateEnabled = true;
    public static bool ChiselLodEnabled = true;
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
        (nameof(OptimumConfigData.ShadowFarVegetation), ShadowFarVegetation.ToString()),
        (nameof(OptimumConfigData.WeatherWindThrottle), WeatherWindThrottleEnabled.ToString()),
        (nameof(OptimumConfigData.ParticleDistanceGate), ParticleDistanceGateEnabled.ToString()),
        (nameof(OptimumConfigData.ChiselLod), ChiselLodEnabled.ToString()),
        (nameof(OptimumConfigData.ChiselLodDistance), ChiselLodDistance.ToString()),
        (nameof(OptimumConfigData.OcclusionCullingScale), OcclusionCullingScaleEnabled.ToString()),
        (nameof(OptimumConfigData.DynamicLightCache), DynamicLightCacheEnabled.ToString()),
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
    /// Load config from optimum.json. Missing keys keep their compiled defaults.
    /// </summary>
    public static void Load()
    {
        if (_configPath == null || !File.Exists(_configPath)) return;

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
            ShadowFarVegetation = data.ShadowFarVegetation;
            WeatherWindThrottleEnabled = data.WeatherWindThrottle;
            ParticleDistanceGateEnabled = data.ParticleDistanceGate;
            ChiselLodEnabled = data.ChiselLod;
            ChiselLodDistance = data.ChiselLodDistance;
            ChiselLodDistanceSq = (double)data.ChiselLodDistance * data.ChiselLodDistance;
            OcclusionCullingScaleEnabled = data.OcclusionCullingScale;
            DynamicLightCacheEnabled = data.DynamicLightCache;
        }
        catch (Exception)
        {
            // Corrupt file: ignore, use defaults.
        }
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
            ShadowFarVegetation = ShadowFarVegetation,
            WeatherWindThrottle = WeatherWindThrottleEnabled,
            ParticleDistanceGate = ParticleDistanceGateEnabled,
            ChiselLod = ChiselLodEnabled,
            ChiselLodDistance = ChiselLodDistance,
            OcclusionCullingScale = OcclusionCullingScaleEnabled,
            DynamicLightCache = DynamicLightCacheEnabled,
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
    public bool ShadowFarVegetation { get; set; } = true;
    public bool WeatherWindThrottle { get; set; } = true;
    public bool ParticleDistanceGate { get; set; } = true;
    public bool ChiselLod { get; set; } = true;
    public int ChiselLodDistance { get; set; } = 48;
    public bool OcclusionCullingScale { get; set; } = true;
    public bool DynamicLightCache { get; set; } = true;
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
    public static readonly HitSkipCounter AnimBlockLod = new();
    public static readonly HitSkipCounter ParticleDistanceGate = new();
    public static readonly HitSkipCounter OcclusionCullingScale = new();
    public static readonly HitSkipCounter DynamicLightCache = new();
    public static readonly HitSkipCounter ChunkUploadSort = new();

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
        [nameof(AnimBlockLod)] = AnimBlockLod,
        [nameof(ParticleDistanceGate)] = ParticleDistanceGate,
        [nameof(OcclusionCullingScale)] = OcclusionCullingScale,
        [nameof(DynamicLightCache)] = DynamicLightCache,
        [nameof(ChunkUploadSort)] = ChunkUploadSort,
    };

    public static void ResetAllCounters()
    {
        foreach (var counter in Counters.Values)
        {
            counter.Reset();
        }
        ResetChiselLod();
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
        return sb.ToString();
    }
}
