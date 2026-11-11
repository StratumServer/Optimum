using System;
using Vintagestory.API.Config;
using Xunit;
using OptimumInterop = Vintagestory.API.Config.VsModInterop;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class ChunkUploadBoundsTests
{
    [Theory]
    [InlineData(512, 8, 264)]
    [InlineData(256, 8, 136)]
    [InlineData(128, 8, 72)]
    [InlineData(1024, 8, 520)]
    [InlineData(512, 0, 264)]
    [InlineData(512, 12, 396)]
    public void CapacityHoldsOneStripOfNewTerrainAtTheViewDistance(int viewDistance, int sizeY, int expected)
    {
        int previous = OptimumConfig.TesselationUploadQueueCapacity;
        try
        {
            OptimumConfig.TesselationUploadQueueCapacity = 0;
            Assert.Equal(expected, OptimumConfig.GetTesselationUploadCapacity(viewDistance, sizeY));
        }
        finally
        {
            OptimumConfig.TesselationUploadQueueCapacity = previous;
        }
    }

    [Fact]
    public void CapacityGrowsWithViewDistanceAndStaysBounded()
    {
        int previousSetting = OptimumConfig.TesselationUploadQueueCapacity;
        try
        {
            OptimumConfig.TesselationUploadQueueCapacity = 0;
            int last = 0;
            for (int view = 32; view <= 4096; view += 32)
            {
                int capacity = OptimumConfig.GetTesselationUploadCapacity(view, 8);
                Assert.InRange(capacity, 64, 2048);
                Assert.True(capacity >= last, $"capacity shrank at view distance {view}");
                last = capacity;
            }

            Assert.Equal(64, OptimumConfig.GetTesselationUploadCapacity(0, 8));
            Assert.Equal(2048, OptimumConfig.GetTesselationUploadCapacity(1_000_000, 8));
        }
        finally
        {
            OptimumConfig.TesselationUploadQueueCapacity = previousSetting;
        }
    }

    [Fact]
    public void ExplicitCapacityOverridesTheDerivedValue()
    {
        int previous = OptimumConfig.TesselationUploadQueueCapacity;
        try
        {
            OptimumConfig.TesselationUploadQueueCapacity = 96;
            Assert.Equal(96, OptimumConfig.GetTesselationUploadCapacity(512, 8));
            Assert.Equal(96, OptimumConfig.GetTesselationUploadCapacity(2048, 8));
        }
        finally
        {
            OptimumConfig.TesselationUploadQueueCapacity = previous;
        }
    }

    [Fact]
    public void SendRadiusNeverExceedsTheViewDistanceRadius()
    {
        var random = new Random(20261111);
        for (int i = 0; i < 20_000; i++)
        {
            int maxRadius = random.Next(1, 64);
            int adaptive = random.Next(0, 2) == 0 ? int.MaxValue : random.Next(1, 64);
            int viewBlocks = random.Next(32, 2048);
            int viewRadius = (int)Math.Ceiling(viewBlocks / 32f);

            int radius = OptimumConfig.EffectiveSendRadius(maxRadius, adaptive, viewRadius);

            Assert.InRange(radius, 0, Math.Min(maxRadius, viewRadius));
            Assert.True(radius <= adaptive);
        }
    }

    [Fact]
    public void AdaptiveRadiusStaysBetweenTheFloorAndTheConfiguredMaximum()
    {
        OptimumConfig.AdaptiveRadiusEnabled = true;
        OptimumConfig.AdaptiveRadiusFloor = 4;
        OptimumConfig.AdaptiveRadiusHighThreshold = 60;
        OptimumConfig.AdaptiveRadiusLowThreshold = 20;
        OptimumConfig.KometDetected = false;
        OptimumConfig.KometAdaptiveChunkInflowEnabled = false;
        OptimumInterop.ResetYieldsForTests();
        OptimumConfig.AdaptiveRadiusEffective = int.MaxValue;

        var random = new Random(7);
        var controller = new OptimumAdaptiveRadiusController(16);
        try
        {
            for (int i = 0; i < 50_000; i++)
            {
                int maxRadius = random.Next(1, 40);
                int queue = random.Next(0, 200);
                float tickMs = random.Next(0, 3) == 0 ? -1f : random.Next(0, 80);

                controller.Tick(queue, maxRadius, tickMs);

                int floor = Math.Min(OptimumConfig.AdaptiveRadiusFloor, maxRadius);
                Assert.InRange(controller.EffectiveRadius, floor, maxRadius);
                Assert.Equal(controller.EffectiveRadius, OptimumConfig.AdaptiveRadiusEffective);
            }
        }
        finally
        {
            OptimumAdaptiveRadiusController.ReleaseActive();
        }
    }
}
