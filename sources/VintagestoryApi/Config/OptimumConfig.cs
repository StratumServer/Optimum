using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vintagestory.API.Config;

/// <summary>
/// Runtime config for Optimum optimizations. Persists to ModConfig/optimum.json.
/// VintagestoryLib syncs these from ClientSettings at startup; forks read the static fields.
/// </summary>
public static class OptimumConfig
{
    public static bool RepulsionGateEnabled = true;
    public static int RepulsionDistance = 64;
    public static double RepulsionDistanceSq = 64.0 * 64.0;

    public static bool AnimBlockLodEnabled = true;
    public static bool WeatherWindThrottleEnabled = true;
    public static bool ParticleDistanceGateEnabled = true;
    public static bool ChiselLodEnabled = true;
    public static int ChiselLodDistance = 48;
    public static double ChiselLodDistanceSq = 48.0 * 48.0;

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
}
