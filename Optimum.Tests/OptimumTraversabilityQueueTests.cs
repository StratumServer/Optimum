using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

// Issue #566: Nearest-first priority ordering, 2 ms batch budget, and torn-read-free
// traversability publishing for SystemChunkVisibilityCalc.
public sealed class OptimumTraversabilityQueueTests
{
    [Fact]
    public void PushAndPop_ReturnsNearestChunkFirstBySquaredDistance()
    {
        var queue = new OptimumTraversabilityQueue();
        int camX = 100, camY = 4, camZ = 100;

        queue.Push(110, 4, 100, 0, camX, camY, camZ); // distSq = 100
        queue.Push(102, 4, 101, 0, camX, camY, camZ); // distSq = 5
        queue.Push(100, 4, 100, 0, camX, camY, camZ); // distSq = 0
        queue.Push(101, 4, 101, 0, camX, camY, camZ); // distSq = 2
        queue.Push(100, 7, 100, 0, camX, camY, camZ); // distSq = 9

        long prev = -1;
        int popped = 0;
        while (queue.TryPop(out var entry))
        {
            Assert.True(entry.DistSq >= prev, $"Out-of-order pop: {entry.DistSq} < {prev}");
            prev = entry.DistSq;
            popped++;
        }

        Assert.Equal(5, popped);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void ReheapIfCameraMoved_RespectsThresholdAndReprioritizes()
    {
        var queue = new OptimumTraversabilityQueue();
        queue.Push(10, 0, 0, 0, 0, 0, 0);
        queue.Push(0, 0, 0, 0, 0, 0, 0);

        Assert.False(queue.ReheapIfCameraMoved(1, 0, 0));
        Assert.True(queue.ReheapIfCameraMoved(10, 0, 0));

        Assert.True(queue.TryPop(out var first));
        Assert.Equal(10, first.X);
        Assert.True(queue.TryPop(out var second));
        Assert.Equal(0, second.X);
    }

    [Fact]
    public void BulkReset_GrowsCapacityAndMaintainsMinHeapInvariant()
    {
        var queue = new OptimumTraversabilityQueue();
        const int count = 600;
        queue.BeginBulkReset(count, 50, 4, 50);
        for (int i = 0; i < count; i++)
        {
            queue.PushUnordered(50 + ((i * 37) % 41) - 20, 4 + (i % 7), 50 + ((i * 53) % 43) - 21, 0);
        }
        queue.FinishBulkReset();
        Assert.Equal(count, queue.Count);

        long prev = -1;
        int popped = 0;
        while (queue.TryPop(out var entry))
        {
            Assert.True(entry.DistSq >= prev);
            prev = entry.DistSq;
            popped++;
        }
        Assert.Equal(count, popped);
    }

    [Fact]
    public void SteadyStatePushPop_IsZeroAllocationAfterWarmup()
    {
        var queue = new OptimumTraversabilityQueue();
        for (int i = 0; i < 64; i++)
        {
            queue.Push(i, 4, i, 0, 0, 4, 0);
        }
        while (queue.TryPop(out _)) { }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < 64; i++)
            {
                queue.Push(i, 4, i, 0, round, 4, 0);
            }
            while (queue.TryPop(out _)) { }
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Constants_AreWithinExpectedOperationalBounds()
    {
        Assert.Equal(2.0, OptimumTraversabilityQueue.BatchBudgetMs);
        Assert.InRange(OptimumTraversabilityQueue.PopBatchSize, 1, 16);
        Assert.InRange(OptimumTraversabilityQueue.ReheapMoveThresholdChunks, 1, 8);
    }

    [Fact]
    public void ProgramAndPatches_WireSystemChunkVisibilityCalcWithoutPopFirst()
    {
        string program = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("Vintagestory.Client.NoObf.SystemChunkVisibilityCalc", program);
        Assert.Contains("\"OnSeperateThreadGameTick\"", program);
        Assert.Contains("\"RegenTraversabilityGraph\"", program);
        Assert.Contains("\"ConnectFacesToMask\"", program);

        string sysPatchPath = PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemChunkVisibilityCalc.cs.patch");
        string sysPatch = File.ReadAllText(sysPatchPath);
        Assert.Contains("OptimumTraversabilityQueue", sysPatch);
        Assert.Contains("Volatile.Write(ref clientChunk.traversabilityFresh, true)", sysPatch);
        Assert.DoesNotContain("+			chunkpos = game.chunkPositionsForRegenTrav.PopFirst();", sysPatch);

        string cullerPatch = File.ReadAllText(PatchReader.FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkCuller.cs.patch"));
        Assert.Contains("Volatile.Read(ref chunk.traversabilityFresh)", cullerPatch);
    }
}
