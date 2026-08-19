using System;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Tests for OptimumTerrainSampler's static helpers: color classification,
/// brightness multiplication, desaturation, and hash noise distribution.
/// The SampleChunk method requires a live ICoreClientAPI (untestable without
/// a game instance); these tests verify the deterministic math layer beneath.
/// </summary>
public class TerrainSamplerTests
{
    // ---- ClassifyColor ----

    [Fact]
    public void ClassifyColor_HighOcean_ReturnsOceanColor()
    {
        // Ocean takes priority regardless of other params.
        int result = OptimumTerrainSampler.ClassifyColor(
            temperature: 128, rainfall: 128, ocean: 0.8f, forest: 50);
        Assert.Equal(unchecked((int)0xFF_CCC890), result);
    }

    [Fact]
    public void ClassifyColor_LowTemperature_ReturnsGlacierColor()
    {
        // Below glacier threshold (60), ocean < 0.5.
        int result = OptimumTerrainSampler.ClassifyColor(
            temperature: 30, rainfall: 128, ocean: 0.1f, forest: 50);
        Assert.Equal(unchecked((int)0xFF_E0E0C0), result);
    }

    [Fact]
    public void ClassifyColor_HighTempLowRain_ReturnsDesertColor()
    {
        // Above 200 temp + below 80 rain = desert.
        int result = OptimumTerrainSampler.ClassifyColor(
            temperature: 220, rainfall: 40, ocean: 0.0f, forest: 50);
        Assert.Equal(unchecked((int)0xFF_C4A468), result);
    }

    [Fact]
    public void ClassifyColor_HighForest_ReturnsForestColor()
    {
        // Forest threshold > 140.
        int result = OptimumTerrainSampler.ClassifyColor(
            temperature: 128, rainfall: 128, ocean: 0.0f, forest: 180);
        Assert.Equal(unchecked((int)0xFF_98844C), result);
    }

    [Fact]
    public void ClassifyColor_MidValues_ReturnsLandColor()
    {
        // None of the above: default land.
        int result = OptimumTerrainSampler.ClassifyColor(
            temperature: 128, rainfall: 128, ocean: 0.0f, forest: 50);
        Assert.Equal(unchecked((int)0xFF_AC8858), result);
    }

    [Fact]
    public void ClassifyColor_OceanPrioritizedOverGlacier()
    {
        // Even at freezing temp, ocean > 0.5 wins.
        int result = OptimumTerrainSampler.ClassifyColor(
            temperature: 10, rainfall: 128, ocean: 0.9f, forest: 50);
        Assert.Equal(unchecked((int)0xFF_CCC890), result);
    }

    // ---- MultiplyBrightness ----

    [Fact]
    public void MultiplyBrightness_Factor1_Unchanged()
    {
        int color = unchecked((int)0xFF_808080);
        int result = OptimumTerrainSampler.MultiplyBrightness(color, 1.0f);
        Assert.Equal(color, result);
    }

    [Fact]
    public void MultiplyBrightness_Factor2_Clamped()
    {
        int color = unchecked((int)0xFF_808080);
        int result = OptimumTerrainSampler.MultiplyBrightness(color, 2.0f);
        // 0x80 * 2 = 0x100, clamped to 0xFF.
        Assert.Equal(unchecked((int)0xFF_FFFFFF), result);
    }

    [Fact]
    public void MultiplyBrightness_Factor0_Black()
    {
        int color = unchecked((int)0xFF_AC8858);
        int result = OptimumTerrainSampler.MultiplyBrightness(color, 0.0f);
        Assert.Equal(unchecked((int)0xFF_000000), result);
    }

    [Fact]
    public void MultiplyBrightness_PreservesAlpha()
    {
        int color = unchecked((int)0xAB_804020);
        int result = OptimumTerrainSampler.MultiplyBrightness(color, 1.5f);
        Assert.Equal(0xAB, (result >> 24) & 0xFF);
    }

    // ---- Desaturate ----

    [Fact]
    public void Desaturate_Factor0_Unchanged()
    {
        int color = unchecked((int)0xFF_AC8858);
        int result = OptimumTerrainSampler.Desaturate(color, 0.0f);
        Assert.Equal(color, result);
    }

    [Fact]
    public void Desaturate_Factor1_FullGray()
    {
        int color = unchecked((int)0xFF_FF0000); // pure red
        int result = OptimumTerrainSampler.Desaturate(color, 1.0f);
        int r = (result >> 16) & 0xFF;
        int g = (result >> 8) & 0xFF;
        int b = result & 0xFF;
        // Full desaturation: R, G, B all equal to luminance.
        // Luminance of pure red (255,0,0) = 255*0.299 = 76.
        Assert.Equal(r, g);
        Assert.Equal(g, b);
        Assert.InRange(r, 75, 77); // rounding tolerance
    }

    [Fact]
    public void Desaturate_HalfFactor_MovesTowardGray()
    {
        int color = unchecked((int)0xFF_FF0000); // pure red
        int result = OptimumTerrainSampler.Desaturate(color, 0.5f);
        int r = (result >> 16) & 0xFF;
        int g = (result >> 8) & 0xFF;
        // R should be between original (255) and lum (76): about 165
        Assert.InRange(r, 160, 170);
        // G should be between 0 and lum (76): about 38
        Assert.InRange(g, 35, 42);
    }

    [Fact]
    public void Desaturate_PreservesAlpha()
    {
        int color = unchecked((int)0xCD_AC8858);
        int result = OptimumTerrainSampler.Desaturate(color, 0.5f);
        Assert.Equal(0xCD, (result >> 24) & 0xFF);
    }

    // ---- HashNoise ----

    [Fact]
    public void HashNoise_InRange01()
    {
        // Sample 1000 positions, all must be in [0, 1).
        for (int i = 0; i < 1000; i++)
        {
            float v = OptimumTerrainSampler.HashNoise(i * 17, i * 31, 12345);
            Assert.InRange(v, 0.0f, 1.0f);
        }
    }

    [Fact]
    public void HashNoise_Deterministic()
    {
        float a = OptimumTerrainSampler.HashNoise(100, 200, 42);
        float b = OptimumTerrainSampler.HashNoise(100, 200, 42);
        Assert.Equal(a, b);
    }

    [Fact]
    public void HashNoise_DifferentSeed_DifferentValue()
    {
        float a = OptimumTerrainSampler.HashNoise(100, 200, 42);
        float b = OptimumTerrainSampler.HashNoise(100, 200, 43);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashNoise_Distribution_NotDegenerate()
    {
        // Verify the noise covers the range, not clustered in one bucket.
        int belowHalf = 0;
        int total = 10000;
        for (int i = 0; i < total; i++)
        {
            float v = OptimumTerrainSampler.HashNoise(i * 7, i * 13, 999);
            if (v < 0.5f) belowHalf++;
        }
        // Expect roughly 50% below 0.5. Allow 5% tolerance.
        double ratio = (double)belowHalf / total;
        Assert.InRange(ratio, 0.45, 0.55);
    }

    // ---- ComputeSlope ----

    [Fact]
    public void ComputeSlope_FlatTerrain_Returns1()
    {
        // All heights equal: zero gradient, brightness = 1.0.
        float[] grid = new float[9]; // 3x3
        Array.Fill(grid, 0.5f);
        float slope = OptimumTerrainSampler.ComputeSlope(grid, 1, 1, 3);
        Assert.Equal(1.0f, slope, precision: 4);
    }

    [Fact]
    public void ComputeSlope_RisingToSE_DarkerThan1()
    {
        // Terrain rises toward SE: current pixel is in shadow (NW light).
        float[] grid = new float[9]; // 3x3
        // Center at (1,1). Left=0.5, right=0.7, up=0.5, down=0.7
        grid[0] = 0.5f; grid[1] = 0.5f; grid[2] = 0.5f;
        grid[3] = 0.5f; grid[4] = 0.6f; grid[5] = 0.7f;
        grid[6] = 0.5f; grid[7] = 0.7f; grid[8] = 0.7f;
        float slope = OptimumTerrainSampler.ComputeSlope(grid, 1, 1, 3);
        Assert.True(slope < 1.0f, $"Expected shadow (< 1.0), got {slope}");
    }

    [Fact]
    public void ComputeSlope_RisingToNW_BrighterThan1()
    {
        // Terrain rises toward NW: current pixel faces the light.
        float[] grid = new float[9]; // 3x3
        // Center at (1,1). Left=0.7, right=0.5, up=0.7, down=0.5
        grid[0] = 0.7f; grid[1] = 0.7f; grid[2] = 0.7f;
        grid[3] = 0.7f; grid[4] = 0.6f; grid[5] = 0.5f;
        grid[6] = 0.7f; grid[7] = 0.5f; grid[8] = 0.5f;
        float slope = OptimumTerrainSampler.ComputeSlope(grid, 1, 1, 3);
        Assert.True(slope > 1.0f, $"Expected lit (> 1.0), got {slope}");
    }

    [Fact]
    public void ComputeSlope_SymmetricOpposites()
    {
        // A slope facing NW and its mirror facing SE should produce
        // symmetric brightness around 1.0.
        float[] gridNW = new float[9];
        gridNW[0] = 0.8f; gridNW[1] = 0.8f; gridNW[2] = 0.6f;
        gridNW[3] = 0.8f; gridNW[4] = 0.7f; gridNW[5] = 0.6f;
        gridNW[6] = 0.6f; gridNW[7] = 0.6f; gridNW[8] = 0.6f;

        float[] gridSE = new float[9];
        gridSE[0] = 0.6f; gridSE[1] = 0.6f; gridSE[2] = 0.6f;
        gridSE[3] = 0.6f; gridSE[4] = 0.7f; gridSE[5] = 0.8f;
        gridSE[6] = 0.6f; gridSE[7] = 0.8f; gridSE[8] = 0.8f;

        float slopeNW = OptimumTerrainSampler.ComputeSlope(gridNW, 1, 1, 3);
        float slopeSE = OptimumTerrainSampler.ComputeSlope(gridSE, 1, 1, 3);

        // Both deviations from 1.0 should be equal in magnitude.
        float devNW = slopeNW - 1.0f;
        float devSE = slopeSE - 1.0f;
        Assert.Equal(devNW, -devSE, precision: 4);
    }
}
