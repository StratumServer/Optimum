using System;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Essentials;
using Xunit;

namespace Optimum.Tests;

public sealed class PathfindingAsyncTests
{
    private sealed class TestablePathfindingAsync : PathfindingAsync
    {
        public PathfinderTask CallNext() => Next();
    }

    [Fact]
    public void ProcessQueue_WhenMaxCountIsZero_DoesNotDropTasksFromQueue()
    {
        var pathfinding = new TestablePathfindingAsync();
        var box = new Cuboidf(0, 0, 0, 1, 1, 1);
        var task1 = new PathfinderTask(new BlockPos(0, 0, 0), new BlockPos(1, 0, 0), 0, 1, 1, box, 10);
        var task2 = new PathfinderTask(new BlockPos(0, 0, 0), new BlockPos(2, 0, 0), 0, 1, 1, box, 10);

        pathfinding.PathfinderTasks.Enqueue(task1);
        pathfinding.PathfinderTasks.Enqueue(task2);

        // ProcessQueue with maxCount = 0 should NOT dequeue any tasks when quota is exhausted
        pathfinding.ProcessQueue(null, 0);

        Assert.Equal(2, pathfinding.PathfinderTasks.Count);
        Assert.Same(task1, pathfinding.CallNext());
        Assert.Same(task2, pathfinding.CallNext());
    }

    [Fact]
    public void PathfindingAsyncPatch_VerifiesQuotaCheckedBeforeNextCall()
    {
        string patchPath = PatchReader.FindRepositoryFile("patches/VSEssentials/Entity/Pathfinding/PathfindingAsync.cs.patch");
        Assert.True(File.Exists(patchPath), "PathfindingAsync.cs.patch must exist");
        string patchedContent = PatchReader.ReadPatchedContent(patchPath);
        Assert.Contains("while (maxCount > 0 && (task = Next()) != null)", patchedContent);
        Assert.DoesNotContain("while ((task = Next()) != null && maxCount-- > 0)", patchedContent);
    }
}
