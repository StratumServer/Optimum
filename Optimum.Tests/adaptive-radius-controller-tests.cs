using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class AdaptiveRadiusControllerTests
{
    private static void SetAdaptiveRadiusConfig(bool enabled = true, int floor = 4, int high = 60, int low = 20)
    {
        OptimumConfig.AdaptiveRadiusEnabled = enabled;
        OptimumConfig.AdaptiveRadiusFloor = floor;
        OptimumConfig.AdaptiveRadiusHighThreshold = high;
        OptimumConfig.AdaptiveRadiusLowThreshold = low;
        OptimumConfig.AdaptiveRadiusEffective = int.MaxValue;
    }

    [Fact]
    public void ColdStartUsesMaxRadius()
    {
        SetAdaptiveRadiusConfig();
        var ctrl = new OptimumAdaptiveRadiusController(12);
        Assert.Equal(12, ctrl.EffectiveRadius);
    }

    [Fact]
    public void ShrinksRadiusWhenQueueExceedsHighThreshold()
    {
        SetAdaptiveRadiusConfig(high: 50, low: 10);
        var ctrl = new OptimumAdaptiveRadiusController(12);

        // Sustained high queue: EWMA alpha=0.15, need several ticks to cross 50.
        // First tick seeds the EWMA directly at the sample value.
        ctrl.Tick(80, 12);
        // EWMA = 80 (seeded). 80 > 50 threshold: should shrink.
        Assert.Equal(11, ctrl.EffectiveRadius);

        ctrl.Tick(80, 12);
        // EWMA = 0.15*80 + 0.85*80 = 80. Still above threshold.
        Assert.Equal(10, ctrl.EffectiveRadius);
    }

    [Fact]
    public void RecoversRadiusWhenQueueBelowLowThreshold()
    {
        SetAdaptiveRadiusConfig(high: 60, low: 20);
        var ctrl = new OptimumAdaptiveRadiusController(12);

        // Force radius down first
        ctrl.Tick(100, 12); // Seeds EWMA at 100, shrinks to 11
        ctrl.Tick(100, 12); // Stays high, shrinks to 10
        Assert.Equal(10, ctrl.EffectiveRadius);

        // Now queue empties. EWMA decays slowly (alpha=0.15).
        // Need several ticks at 0 to drop EWMA below 20.
        // EWMA after tick at 0: 0.15*0 + 0.85*100 = 85 → still above low=20
        for (int i = 0; i < 20; i++)
        {
            ctrl.Tick(0, 12);
        }

        // After ~20 ticks at 0: EWMA = 100 * 0.85^20 ≈ 3.9 → below 20
        Assert.True(ctrl.EffectiveRadius > 10,
            $"Expected radius to recover above 10, got {ctrl.EffectiveRadius} (EWMA: {ctrl.SmoothedQueueDepth:F1})");
    }

    [Fact]
    public void NeverDropsBelowFloor()
    {
        SetAdaptiveRadiusConfig(floor: 4, high: 30);
        var ctrl = new OptimumAdaptiveRadiusController(12);

        // Hammer it with high queue depth for many ticks
        for (int i = 0; i < 50; i++)
        {
            ctrl.Tick(200, 12);
        }

        Assert.Equal(4, ctrl.EffectiveRadius);
    }

    [Fact]
    public void NeverExceedsMaxRadius()
    {
        SetAdaptiveRadiusConfig(low: 50);
        var ctrl = new OptimumAdaptiveRadiusController(12);

        // Queue always empty: recovery should cap at max
        for (int i = 0; i < 30; i++)
        {
            ctrl.Tick(0, 12);
        }

        Assert.Equal(12, ctrl.EffectiveRadius);
    }

    [Fact]
    public void DisabledModePinsToMax()
    {
        SetAdaptiveRadiusConfig(enabled: false);
        var ctrl = new OptimumAdaptiveRadiusController(12);

        // Even with massive queue, disabled mode ignores it
        ctrl.Tick(500, 12);
        Assert.Equal(12, ctrl.EffectiveRadius);
        Assert.Equal(12, OptimumConfig.AdaptiveRadiusEffective);
    }

    [Fact]
    public void TracksMaxRadiusChanges()
    {
        SetAdaptiveRadiusConfig(low: 50);
        var ctrl = new OptimumAdaptiveRadiusController(12);

        // Radius is at max (12), queue empty
        ctrl.Tick(0, 12);
        Assert.Equal(12, ctrl.EffectiveRadius);

        // Singleplayer raises view distance: MaxChunkRadius goes to 16
        ctrl.Tick(0, 16);

        // Should grow toward new max
        Assert.True(ctrl.EffectiveRadius > 12,
            $"Expected radius > 12 after max raised to 16, got {ctrl.EffectiveRadius}");
    }

    [Fact]
    public void TracksMaxRadiusDecrease()
    {
        SetAdaptiveRadiusConfig(low: 50);
        var ctrl = new OptimumAdaptiveRadiusController(12);

        ctrl.Tick(0, 12);
        Assert.Equal(12, ctrl.EffectiveRadius);

        // MaxChunkRadius drops to 8 (e.g. multiplayer server cap)
        ctrl.Tick(0, 8);

        // Effective should clamp to new max
        Assert.Equal(8, ctrl.EffectiveRadius);
    }

    [Fact]
    public void PublishesToStaticVolatile()
    {
        SetAdaptiveRadiusConfig();
        OptimumConfig.AdaptiveRadiusEffective = int.MaxValue;

        var ctrl = new OptimumAdaptiveRadiusController(10);
        ctrl.Tick(100, 10); // High queue, seeds EWMA at 100, shrinks

        Assert.Equal(ctrl.EffectiveRadius, OptimumConfig.AdaptiveRadiusEffective);
        Assert.True(OptimumConfig.AdaptiveRadiusEffective < 10);
    }

    [Fact]
    public void HysteresisPreventsThrashing()
    {
        SetAdaptiveRadiusConfig(high: 60, low: 20);
        var ctrl = new OptimumAdaptiveRadiusController(12);

        // Queue in the dead zone (between low=20 and high=60): no movement
        ctrl.Tick(40, 12); // Seeds EWMA at 40
        Assert.Equal(12, ctrl.EffectiveRadius); // 40 < 60, no shrink

        ctrl.Tick(40, 12);
        Assert.Equal(12, ctrl.EffectiveRadius); // Still in dead zone

        ctrl.Tick(35, 12);
        Assert.Equal(12, ctrl.EffectiveRadius); // 35 > 20, no recovery either (already at max)
    }

    [Fact]
    public void EwmaSmoothsSingleSpike()
    {
        SetAdaptiveRadiusConfig(high: 60, low: 20);
        var ctrl = new OptimumAdaptiveRadiusController(12);

        // Steady low queue, then one spike
        ctrl.Tick(10, 12); // Seeds at 10
        Assert.Equal(12, ctrl.EffectiveRadius);

        // Single spike to 200
        ctrl.Tick(200, 12);
        // EWMA: 0.15*200 + 0.85*10 = 38.5 → below 60 threshold
        Assert.Equal(12, ctrl.EffectiveRadius); // Spike smoothed, no shrink
    }

    [Fact]
    public void ResetRestoresInitialState()
    {
        SetAdaptiveRadiusConfig();
        var ctrl = new OptimumAdaptiveRadiusController(12);

        // Drive it down
        for (int i = 0; i < 10; i++) ctrl.Tick(200, 12);
        Assert.True(ctrl.EffectiveRadius < 12);

        ctrl.Reset(14);
        Assert.Equal(14, ctrl.EffectiveRadius);
        Assert.Equal(0, ctrl.SmoothedQueueDepth);
    }

    [Fact]
    public void FullCycle_ExplorationSpikeThenRecovery()
    {
        // Realistic scenario: player starts exploring (queue spikes), radius
        // contracts, then player stops (queue drains), radius recovers.
        SetAdaptiveRadiusConfig(high: 60, low: 20, floor: 4);
        var ctrl = new OptimumAdaptiveRadiusController(12);

        // Phase 1: player sprinting into unexplored terrain (queue spikes to 120+)
        for (int i = 0; i < 15; i++)
        {
            ctrl.Tick(120, 12);
        }

        int contractedRadius = ctrl.EffectiveRadius;
        Assert.True(contractedRadius < 12,
            $"Expected radius < 12 during exploration spike, got {contractedRadius}");
        Assert.True(contractedRadius >= 4,
            $"Expected radius >= floor (4) during spike, got {contractedRadius}");

        // Phase 2: player stops, queue drains over many ticks
        for (int i = 0; i < 40; i++)
        {
            ctrl.Tick(0, 12);
        }

        int recoveredRadius = ctrl.EffectiveRadius;
        Assert.True(recoveredRadius > contractedRadius,
            $"Expected radius to recover above {contractedRadius}, got {recoveredRadius}");
    }
}
