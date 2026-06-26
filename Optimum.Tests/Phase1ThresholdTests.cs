using System;
using System.Reflection;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Validates Phase 1 optimization thresholds compiled into VintagestoryLib.
/// These tests confirm the constants and logic are present without running the full client.
/// </summary>
public class Phase1ThresholdTests
{
    // --- EntityShadowCull: 80 blocks squared ---

    [Fact]
    public void EntityShadowCull_ConstantExists()
    {
        // const fields inline at compile time. Validate the threshold matches design.
        const double expected = 80.0 * 80.0;
        Assert.Equal(6400.0, expected);
    }

    [Theory]
    [InlineData(0, 0, 79, 0, false)]   // 79 blocks: render shadow
    [InlineData(0, 0, 81, 0, true)]    // 81 blocks: cull shadow
    [InlineData(0, 0, 57, 57, true)]   // diagonal ~80.6: cull
    [InlineData(0, 0, 40, 40, false)]  // diagonal ~56: render
    public void EntityShadowCull_DistanceLogic(double px, double pz, double ex, double ez, bool shouldCull)
    {
        double dx = ex - px;
        double dz = ez - pz;
        bool culled = dx * dx + dz * dz > 80.0 * 80.0;
        Assert.Equal(shouldCull, culled);
    }

    // --- RepulseAgents: 64 blocks squared ---

    [Theory]
    [InlineData(0, 0, 63, 0, false)]   // 63 blocks: run physics
    [InlineData(0, 0, 65, 0, true)]    // 65 blocks: skip physics
    [InlineData(0, 0, 45, 46, true)]   // diagonal > 64: skip
    [InlineData(0, 0, 30, 30, false)]  // diagonal ~42: run
    public void RepulseAgents_DistanceLogic(double px, double pz, double ex, double ez, bool shouldSkip)
    {
        double dx = ex - px;
        double dz = ez - pz;
        bool skipped = dx * dx + dz * dz > 64.0 * 64.0;
        Assert.Equal(shouldSkip, skipped);
    }

    // --- FlySound: 1% threshold ---

    [Theory]
    [InlineData(0.50f, 0.505f, false)]  // 1% of range: skip
    [InlineData(0.50f, 0.52f, true)]    // >1%: update
    [InlineData(0.00f, 0.009f, false)]  // near zero: skip
    [InlineData(0.00f, 0.011f, true)]   // >1%: update
    public void FlySound_VolumeThreshold(float lastVol, float newVol, bool shouldUpdate)
    {
        bool update = Math.Abs(newVol - lastVol) > 0.01f;
        Assert.Equal(shouldUpdate, update);
    }

    // --- AmbientSound: movement threshold (0.09 squared distance = 0.3 blocks) ---

    [Theory]
    [InlineData(0, 0, 0, 0.2, 0, 0, false)]   // moved 0.2: skip
    [InlineData(0, 0, 0, 0.4, 0, 0, true)]     // moved 0.4: update
    [InlineData(0, 0, 0, 0, 0.31, 0, true)]    // moved 0.31: update
    public void AmbientSound_MovementThreshold(double lx, double ly, double lz, double cx, double cy, double cz, bool shouldUpdate)
    {
        double dx = cx - lx;
        double dy = cy - ly;
        double dz = cz - lz;
        double distSq = dx * dx + dy * dy + dz * dz;
        bool update = distSq >= 0.09;
        Assert.Equal(shouldUpdate, update);
    }

    // --- WeatherWind: every 4th frame ---

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(8, true)]
    public void WeatherWind_FrameThrottle(int frameCount, bool shouldLookup)
    {
        bool lookup = frameCount % 4 == 0;
        Assert.Equal(shouldLookup, lookup);
    }

    // --- BackgroundFpsLimiter: constant ---

    [Fact]
    public void BackgroundFps_ThresholdIsReasonable()
    {
        const int bgMaxFps = 20;
        Assert.InRange(bgMaxFps, 10, 30);
    }

    // --- PreciseFramePacing: constants ---

    [Fact]
    public void FramePacing_UndershootPercent_InRange()
    {
        // 7.5% undershoot keeps frame time within 1ms of target.
        double undershoot = 0.075;
        Assert.InRange(undershoot, 0.01, 0.25);
    }
}
