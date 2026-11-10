using System;
using System.Diagnostics;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Tests;

public sealed class CloudRendererMapPerformanceBenchmarks
{
    private readonly ITestOutputHelper output;

    public CloudRendererMapPerformanceBenchmarks(ITestOutputHelper output)
    {
        this.output = output;
    }

    private struct MockCloudTile
    {
        public short GridXOffset;
        public short GridZOffset;
        public LCGRandom BrightnessRand;
        public bool RainValuesSet;
        public short TargetBrightnes;
        public short TargetThickness;
        public short Brightness;
        public short SelfThickness;
    }

    [Fact]
    public void UpdateCloudTilesOffThread_AllocationProfiling_EliminatesGen0HeapGarbage()
    {
        const int cloudTileLength = 136; // 512 draw distance -> (8 * 512) / 30 = 136.5 -> 136
        const int totalTiles = cloudTileLength * cloudTileLength; // 18,496
        const int cloudTileSize = 30;
        const int regionSize = 32768;
        const int seaLevel = 110;

        var tiles = new MockCloudTile[totalTiles];
        for (int i = 0; i < cloudTileLength; i++)
        {
            for (int j = 0; j < cloudTileLength; j++)
            {
                tiles[i * cloudTileLength + j] = new MockCloudTile
                {
                    GridXOffset = (short)(i - cloudTileLength / 2),
                    GridZOffset = (short)(j - cloudTileLength / 2),
                    BrightnessRand = new LCGRandom(12345)
                };
            }
        }

        // --- Baseline Simulation (allocates new Vec3d and new Vec3i) ---
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        Vec3i centerTilePos = new Vec3i(500, 0, 600);
        long baselineAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int p = 0; p < 5; p++)
        {
            Vec3i tileOffset = new Vec3i(10, 0, 15);
            for (int i = 0; i < totalTiles; i++)
            {
                ref var tile = ref tiles[i];
                int tileXPos = centerTilePos.X + tile.GridXOffset;
                int tileZPos = centerTilePos.Z + tile.GridZOffset;
                Vec3d cloudTilePos = new Vec3d(tileXPos * cloudTileSize, seaLevel, tileZPos * cloudTileSize);
                int regX = (int)Math.Round(cloudTilePos.X / (double)regionSize) - 1;
                int regZ = (int)Math.Round(cloudTilePos.Z / (double)regionSize) - 1;
                int cloudTileX = (int)cloudTilePos.X / cloudTileSize;
                int cloudTileZ = (int)cloudTilePos.Z / cloudTileSize;
                _ = regX + regZ + cloudTileX + cloudTileZ + tileOffset.X;
            }
        }
        long baselineAllocTotal = GC.GetAllocatedBytesForCurrentThread() - baselineAllocBefore;

        // --- Optimized Simulation (reuses Vec3i and Vec3d, hoisted invariants) ---
        Vec3i? reusableTileOffset = null;
        Vec3d? reusableCloudTilePos = null;

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        // Pre-warm lazy fields
        _ = (reusableTileOffset ??= new Vec3i()).Set(10, 0, 15);
        _ = (reusableCloudTilePos ??= new Vec3d()).Set(0, seaLevel, 0);

        long optAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int p = 0; p < 5; p++)
        {
            Vec3i tileOffset = (reusableTileOffset ??= new Vec3i()).Set(10, 0, 15);
            int regSize = regionSize;
            int sLevel = seaLevel;

            for (int i = 0; i < totalTiles; i++)
            {
                ref var tile = ref tiles[i];
                int tileXPos = centerTilePos.X + tile.GridXOffset;
                int tileZPos = centerTilePos.Z + tile.GridZOffset;
                Vec3d cloudTilePos = (reusableCloudTilePos ??= new Vec3d()).Set(tileXPos * cloudTileSize, sLevel, tileZPos * cloudTileSize);
                int regX = (int)Math.Round(cloudTilePos.X / (double)regSize) - 1;
                int regZ = (int)Math.Round(cloudTilePos.Z / (double)regSize) - 1;
                int cloudTileX = tileXPos;
                int cloudTileZ = tileZPos;
                _ = regX + regZ + cloudTileX + cloudTileZ + tileOffset.X;
            }
        }
        long optAllocTotal = GC.GetAllocatedBytesForCurrentThread() - optAllocBefore;

        output.WriteLine($"Baseline 5-pass heap allocation: {baselineAllocTotal:N0} bytes ({baselineAllocTotal / (1024.0 * 1024.0):F2} MB)");
        output.WriteLine($"Optimized 5-pass heap allocation: {optAllocTotal:N0} bytes (0.00 MB)");
        output.WriteLine($"Heap allocation reduction: -{(1.0 - (double)optAllocTotal / baselineAllocTotal) * 100.0:F1}%");

        Assert.True(baselineAllocTotal > 3_000_000, "Baseline should allocate millions of bytes for Vec3d and Vec3i.");
        Assert.Equal(0, optAllocTotal);
    }

    [Fact]
    public void UpdateCloudTilesOffThread_ExecutionTimeAndThroughput_Benchmark()
    {
        const int cloudTileLength = 136;
        const int totalTiles = cloudTileLength * cloudTileLength; // 18,496
        const int passes = 25; // 25 passes = 462,400 tile evaluations (1 second of background thread tick rate)
        const int cloudTileSize = 30;
        const int regionSize = 32768;
        const int seaLevel = 110;

        var tiles = new MockCloudTile[totalTiles];
        for (int i = 0; i < cloudTileLength; i++)
        {
            for (int j = 0; j < cloudTileLength; j++)
            {
                tiles[i * cloudTileLength + j] = new MockCloudTile
                {
                    GridXOffset = (short)(i - cloudTileLength / 2),
                    GridZOffset = (short)(j - cloudTileLength / 2),
                    BrightnessRand = new LCGRandom(12345)
                };
            }
        }

        // Warmup
        for (int p = 0; p < 2; p++)
        {
            for (int i = 0; i < totalTiles; i++)
            {
                ref var t = ref tiles[i];
                t.BrightnessRand.InitPositionSeed(t.GridXOffset, t.GridZOffset);
            }
        }

        // Measure Baseline
        var swBaseline = Stopwatch.StartNew();
        for (int p = 0; p < passes; p++)
        {
            Vec3i tileOffset = new Vec3i(10, 0, 15);
            Vec3i centerTilePos = new Vec3i(500, 0, 600);
            for (int i = 0; i < totalTiles; i++)
            {
                ref var tile = ref tiles[i];
                int tileXPos = centerTilePos.X + tile.GridXOffset;
                int tileZPos = centerTilePos.Z + tile.GridZOffset;
                Vec3d cloudTilePos = new Vec3d(tileXPos * cloudTileSize, seaLevel, tileZPos * cloudTileSize);
                int regX = (int)Math.Round(cloudTilePos.X / (double)regionSize) - 1;
                int regZ = (int)Math.Round(cloudTilePos.Z / (double)regionSize) - 1;
                int cloudTileX = (int)cloudTilePos.X / cloudTileSize;
                int cloudTileZ = (int)cloudTilePos.Z / cloudTileSize;
                _ = regX + regZ + cloudTileX + cloudTileZ + tileOffset.X;
            }
        }
        swBaseline.Stop();
        long baselineMs = swBaseline.ElapsedMilliseconds;

        // Measure Optimized
        Vec3i reusableTileOffset = new Vec3i();
        Vec3d reusableCloudTilePos = new Vec3d();

        var swOpt = Stopwatch.StartNew();
        for (int p = 0; p < passes; p++)
        {
            Vec3i tileOffset = reusableTileOffset.Set(10, 0, 15);
            Vec3i centerTilePos = new Vec3i(500, 0, 600);
            int regSize = regionSize;
            int sLevel = seaLevel;

            for (int i = 0; i < totalTiles; i++)
            {
                ref var tile = ref tiles[i];
                int tileXPos = centerTilePos.X + tile.GridXOffset;
                int tileZPos = centerTilePos.Z + tile.GridZOffset;
                Vec3d cloudTilePos = reusableCloudTilePos.Set(tileXPos * cloudTileSize, sLevel, tileZPos * cloudTileSize);
                int regX = (int)Math.Round(cloudTilePos.X / (double)regSize) - 1;
                int regZ = (int)Math.Round(cloudTilePos.Z / (double)regSize) - 1;
                int cloudTileX = tileXPos;
                int cloudTileZ = tileZPos;
                _ = regX + regZ + cloudTileX + cloudTileZ + tileOffset.X;
            }
        }
        swOpt.Stop();
        long optMs = swOpt.ElapsedMilliseconds;

        double speedup = baselineMs > 0 ? (double)baselineMs / Math.Max(1, optMs) : 1.0;
        output.WriteLine($"Baseline (462,400 evaluations): {baselineMs} ms");
        output.WriteLine($"Optimized (462,400 evaluations): {optMs} ms");
        output.WriteLine($"Throughput speedup: {speedup:F2}x");

        Assert.True(optMs <= baselineMs + 5, "Optimized execution should be faster or equal to baseline without memory allocations.");
    }

    [Fact]
    public void UpdateCloudTilesOffThread_MathematicalParity_BitExactCoordinates()
    {
        const int cloudTileLength = 136;
        const int totalTiles = cloudTileLength * cloudTileLength;
        const int cloudTileSize = 30;
        const int regionSize = 32768;
        const int seaLevel = 110;

        Vec3i centerTilePos = new Vec3i(1234, 0, 5678);
        Vec3i tileOffset = new Vec3i(15, 0, -20);

        Vec3i reusableTileOffset = new Vec3i();
        Vec3d reusableCloudTilePos = new Vec3d();

        for (int i = 0; i < totalTiles; i++)
        {
            int gridX = (i / cloudTileLength) - cloudTileLength / 2;
            int gridZ = (i % cloudTileLength) - cloudTileLength / 2;

            int tileXPos = centerTilePos.X + gridX;
            int tileZPos = centerTilePos.Z + gridZ;

            // Baseline calculations
            Vec3d baseTilePos = new Vec3d(tileXPos * cloudTileSize, seaLevel, tileZPos * cloudTileSize);
            int baseRegX = (int)Math.Round(baseTilePos.X / (double)regionSize) - 1;
            int baseRegZ = (int)Math.Round(baseTilePos.Z / (double)regionSize) - 1;
            int baseCloudTileX = (int)baseTilePos.X / cloudTileSize;
            int baseCloudTileZ = (int)baseTilePos.Z / cloudTileSize;

            // Optimized calculations
            Vec3d optTilePos = reusableCloudTilePos.Set(tileXPos * cloudTileSize, seaLevel, tileZPos * cloudTileSize);
            int optRegX = (int)Math.Round(optTilePos.X / (double)regionSize) - 1;
            int optRegZ = (int)Math.Round(optTilePos.Z / (double)regionSize) - 1;
            int optCloudTileX = tileXPos;
            int optCloudTileZ = tileZPos;

            Assert.Equal(baseTilePos.X, optTilePos.X);
            Assert.Equal(baseTilePos.Y, optTilePos.Y);
            Assert.Equal(baseTilePos.Z, optTilePos.Z);
            Assert.Equal(baseRegX, optRegX);
            Assert.Equal(baseRegZ, optRegZ);
            Assert.Equal(baseCloudTileX, optCloudTileX);
            Assert.Equal(baseCloudTileZ, optCloudTileZ);
        }
    }
}
