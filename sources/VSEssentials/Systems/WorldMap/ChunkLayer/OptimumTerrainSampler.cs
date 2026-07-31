using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Vintagestory.GameContent;

/// <summary>
/// Generates approximate biome-colored 32x32 map tiles for chunks the player
/// has not explored. Uses world seed, climate map, ocean map, and forest map
/// (all available client-side from Packet_MapRegion) to classify each pixel
/// into a terrain category and produce a plausible antique-map color.
///
/// Height shading uses a 2D evaluation of the terrain noise (same seed,
/// octaves, frequency, and persistence as GenTerra) as a relative height
/// proxy. Slope brightness from adjacent pixel height differences mimics the
/// vanilla map's shadow pass. The noise captures large-scale terrain shape
/// (hills, valleys, mountain ridges) without the full 3D density evaluation.
///
/// The output matches the same int[1024] ARGB format that
/// ChunkMapLayer.GenerateChunkImage returns. A desaturation pass marks pregen
/// pixels as visually distinct from explored terrain.
/// </summary>
public sealed class OptimumTerrainSampler
{
    // Vanilla antique-map palette (from ChunkMapLayer.hexColorsByCode).
    // Stored as ARGB with alpha=255.
    private static readonly int ColorLand    = unchecked((int)0xFF_AC8858);
    private static readonly int ColorDesert  = unchecked((int)0xFF_C4A468);
    private static readonly int ColorForest  = unchecked((int)0xFF_98844C);
    private static readonly int ColorLake    = unchecked((int)0xFF_CCC890);
    private static readonly int ColorOcean   = unchecked((int)0xFF_CCC890);
    private static readonly int ColorGlacier = unchecked((int)0xFF_E0E0C0);

    // Climate band thresholds (temperature in the 0-255 packed range,
    // where 0 = coldest and 255 = hottest at sea level before altitude).
    private const int TempGlacier = 60;       // Below this: glacier/snow
    private const int TempDesertMin = 200;    // Above this + low rain: desert
    private const int RainDesertMax = 80;     // Below this + high temp: desert
    private const int ForestThreshold = 140;  // ForestMap value above which: forest tint

    // Pregen desaturation: lerp toward gray by this factor (0.0 = full color, 1.0 = full gray).
    private const float DesatFactor = 0.35f;

    // Noise variation amplitude (brightness +-).
    private const float NoiseBrightRange = 0.06f;

    // Height shading: slope contrast multiplier. Higher = more pronounced
    // hills and valleys. Vanilla shadow uses ~5.0 for the height gradient.
    private const float SlopeContrast = 4.0f;

    // GenTerra terrain noise parameters (from vssurvivalmod GenTerra.cs).
    // 9 octaves, base freq 1/3267, persistence 0.9, world seed.
    private const int TerrainOctaves = 9;
    private const double TerrainBaseFrequency = 0.00030618621784789723; // ≈ 1/3267
    private const double TerrainPersistence = 0.9;

    private readonly ICoreClientAPI _capi;
    private readonly int _seed;
    private readonly NormalizedSimplexNoise _heightNoise;

    public OptimumTerrainSampler(ICoreClientAPI capi)
    {
        _capi = capi;
        _seed = capi.World.Seed;

        // Create the terrain noise with the same params GenTerra uses.
        // NormalizedSimplexNoise.FromDefaultOctaves produces amplitudes = persistence^i
        // and frequencies = baseFrequency * 2^i, matching GenTerra's construction.
        _heightNoise = NormalizedSimplexNoise.FromDefaultOctaves(
            TerrainOctaves, TerrainBaseFrequency, TerrainPersistence, _seed);
    }

    /// <summary>
    /// Generate an approximate 32x32 ARGB map tile for an unexplored chunk.
    /// Returns null if the region containing this chunk has not been received
    /// from the server (no climate data available).
    /// </summary>
    public int[]? SampleChunk(int chunkX, int chunkZ)
    {
        int regionSize = _capi.World.BlockAccessor.RegionSize;
        int regionX = (chunkX * 32) / regionSize;
        int regionZ = (chunkZ * 32) / regionSize;

        IMapRegion? region = _capi.World.BlockAccessor.GetMapRegion(regionX, regionZ);
        if (region == null) return null;

        int[] pixels = new int[1024];

        int baseX = chunkX * 32;
        int baseZ = chunkZ * 32;

        // Region-local fractional coordinates for bilinear sampling.
        int regionBlockX = baseX - regionX * regionSize;
        int regionBlockZ = baseZ - regionZ * regionSize;

        // Evaluate terrain height noise on a 34x34 grid (one pixel border on
        // each side) so slope at edge pixels can reference their neighbors.
        float[] heights = SampleHeightGrid(baseX - 1, baseZ - 1, 34);

        for (int lz = 0; lz < 32; lz++)
        {
            for (int lx = 0; lx < 32; lx++)
            {
                int worldX = baseX + lx;
                int worldZ = baseZ + lz;

                int climate = SampleClimate(region, regionBlockX + lx, regionBlockZ + lz, regionSize);
                int temperature = (climate >> 16) & 0xFF;
                int rainfall = (climate >> 8) & 0xFF;

                float ocean = SampleOcean(region, regionBlockX + lx, regionBlockZ + lz, regionSize);
                float forest = SampleForest(region, regionBlockX + lx, regionBlockZ + lz, regionSize);

                int baseColor = ClassifyColor(temperature, rainfall, ocean, forest);

                // Height shading: compute slope from the 34x34 height grid.
                // Index into the padded grid: offset by +1 for the border.
                int hx = lx + 1;
                int hz = lz + 1;
                float slope = ComputeSlope(heights, hx, hz, 34);

                // Combine slope shading with per-pixel hash noise.
                float noise = HashNoise(worldX, worldZ, _seed);
                float brightness = slope + (noise * 2.0f - 1.0f) * NoiseBrightRange;

                int pixel = MultiplyBrightness(baseColor, brightness);
                pixel = Desaturate(pixel, DesatFactor);

                pixels[lz * 32 + lx] = pixel;
            }
        }

        return pixels;
    }

    /// <summary>
    /// Evaluate terrain noise on a square grid and return normalized height
    /// values in [0, 1]. The noise uses the same seed and octave config as
    /// GenTerra's terrain pass.
    /// </summary>
    private float[] SampleHeightGrid(int startX, int startZ, int size)
    {
        float[] grid = new float[size * size];
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                double wx = startX + x;
                double wz = startZ + z;
                // NormalizedSimplexNoise.Noise returns a value in roughly [0, 1].
                double h = _heightNoise.Noise(wx, wz);
                grid[z * size + x] = (float)h;
            }
        }
        return grid;
    }

    /// <summary>
    /// Compute a brightness multiplier from the slope at (hx, hz) in the
    /// padded height grid. Uses the same northwest-light convention as the
    /// vanilla map shadow pass: bright = sloping toward the light (NW),
    /// dark = sloping away from the light (SE).
    /// </summary>
    public static float ComputeSlope(float[] heights, int hx, int hz, int stride)
    {
        // Height differences: NW light direction means left (west) and up (north)
        // pixels that are higher produce shadows on the current pixel.
        float left  = heights[hz * stride + (hx - 1)];
        float right = heights[hz * stride + (hx + 1)];
        float up    = heights[(hz - 1) * stride + hx];
        float down  = heights[(hz + 1) * stride + hx];

        // Gradient: positive = terrain rises toward SE (current pixel is in shadow)
        float dx = right - left;
        float dz = down - up;

        // NW illumination: project gradient onto light direction (-1, -1) normalized.
        // Positive projection = facing away from light (darker).
        float shade = -(dx + dz) * 0.5f;

        // Scale to a brightness multiplier centered at 1.0.
        return 1.0f + shade * SlopeContrast;
    }

    /// <summary>
    /// Classify a world position into a terrain color category based on
    /// climate temperature, rainfall, ocean strength, and forest density.
    /// </summary>
    public static int ClassifyColor(int temperature, int rainfall, float ocean, float forest)
    {
        // Ocean/lake: highest priority (ocean map > 0.5).
        if (ocean > 0.5f) return ColorOcean;

        // Glacier: cold biomes.
        if (temperature < TempGlacier) return ColorGlacier;

        // Desert: hot + dry.
        if (temperature > TempDesertMin && rainfall < RainDesertMax) return ColorDesert;

        // Forest: high forest density.
        if (forest > ForestThreshold) return ColorForest;

        // Default: land.
        return ColorLand;
    }

    /// <summary>
    /// Bilinear sample of the region's climate map at block-resolution coords.
    /// Returns the packed climate int: bits 16-23 = temp, 8-15 = rain, 0-7 = geologic.
    /// </summary>
    public static int SampleClimate(IMapRegion region, int localX, int localZ, int regionSize)
    {
        IntDataMap2D map = region.ClimateMap;
        if (map == null) return 128 << 16 | 128 << 8; // mid values fallback

        return map.GetUnpaddedColorLerped(
            (float)localX / regionSize * map.InnerSize,
            (float)localZ / regionSize * map.InnerSize);
    }

    /// <summary>
    /// Bilinear sample of the ocean map. Returns 0.0 (land) to 1.0 (deep ocean).
    /// </summary>
    public static float SampleOcean(IMapRegion region, int localX, int localZ, int regionSize)
    {
        IntDataMap2D map = region.OceanMap;
        if (map == null) return 0f;

        int raw = map.GetUnpaddedColorLerped(
            (float)localX / regionSize * map.InnerSize,
            (float)localZ / regionSize * map.InnerSize);

        return (raw & 0xFF) / 255f;
    }

    /// <summary>
    /// Bilinear sample of the forest map. Returns 0-255 forest density.
    /// </summary>
    public static float SampleForest(IMapRegion region, int localX, int localZ, int regionSize)
    {
        IntDataMap2D map = region.ForestMap;
        if (map == null) return 0f;

        int raw = map.GetUnpaddedColorLerped(
            (float)localX / regionSize * map.InnerSize,
            (float)localZ / regionSize * map.InnerSize);

        return raw & 0xFF;
    }

    /// <summary>
    /// Deterministic hash-based noise in [0, 1] for per-pixel variation.
    /// </summary>
    public static float HashNoise(int x, int z, int seed)
    {
        int h = (x * 73856093) ^ (z * 19349663) ^ (seed * 83492791);
        h = (h ^ (h >> 13)) * 1540483477;
        h = h ^ (h >> 15);
        return (float)((h & 0x7FFFFFFF) % 10000) / 10000f;
    }

    /// <summary>
    /// Multiply RGB channels by a brightness factor, clamp to [0, 255].
    /// Preserves alpha.
    /// </summary>
    public static int MultiplyBrightness(int argb, float factor)
    {
        int a = (argb >> 24) & 0xFF;
        int r = Math.Clamp((int)(((argb >> 16) & 0xFF) * factor), 0, 255);
        int g = Math.Clamp((int)(((argb >> 8) & 0xFF) * factor), 0, 255);
        int b = Math.Clamp((int)((argb & 0xFF) * factor), 0, 255);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    /// <summary>
    /// Desaturate an ARGB pixel toward its luminance gray by the given factor.
    /// factor=0 returns the original, factor=1 returns full grayscale.
    /// </summary>
    public static int Desaturate(int argb, float factor)
    {
        int a = (argb >> 24) & 0xFF;
        int r = (argb >> 16) & 0xFF;
        int g = (argb >> 8) & 0xFF;
        int b = argb & 0xFF;

        // ITU-R BT.601 luminance.
        int lum = (int)(r * 0.299f + g * 0.587f + b * 0.114f);

        r = r + (int)((lum - r) * factor);
        g = g + (int)((lum - g) * factor);
        b = b + (int)((lum - b) * factor);

        return (a << 24) | (r << 16) | (g << 8) | b;
    }
}
