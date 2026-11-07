using System;
using System.IO;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;
using Xunit;

namespace Optimum.Tests;

public sealed class PathNodeSetTests
{
    [Fact]
    public void PathNodeSetPatch_VerifiesIndexedPriorityQueue()
    {
        string patchPath = PatchReader.FindRepositoryFile("patches/VSEssentials/Entity/Pathfinding/Astar/PathNodeSet.cs.patch");
        Assert.True(File.Exists(patchPath), "PathNodeSet.cs.patch must exist");
        string patchedContent = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("private PathNode[] heap = new PathNode[64];", patchedContent);
        Assert.Contains("private readonly Dictionary<long, PathNode> nodeMap", patchedContent);
        Assert.Contains("private void SiftUp(int i)", patchedContent);
        Assert.Contains("private void SiftDown(int i)", patchedContent);
        Assert.DoesNotContain("buckets", patchedContent);
    }

    [Fact]
    public void Add_And_RemoveNearest_ExtractsInPriorityOrder()
    {
        var set = new PathNodeSet();

        var node1 = new PathNode(new BlockPos(10, 10, 10)) { gCost = 5f, hCost = 5f }; // f = 10
        var node2 = new PathNode(new BlockPos(20, 10, 20)) { gCost = 2f, hCost = 3f }; // f = 5
        var node3 = new PathNode(new BlockPos(30, 10, 30)) { gCost = 4f, hCost = 4f }; // f = 8

        Assert.True(set.Add(node1));
        Assert.True(set.Add(node2));
        Assert.True(set.Add(node3));
        Assert.Equal(3, set.Count);

        // Duplicate coordinates rejected
        var dup = new PathNode(new BlockPos(10, 10, 10)) { gCost = 1f, hCost = 1f };
        Assert.False(set.Add(dup));
        Assert.Equal(3, set.Count);

        // Nearest should be node2 (f = 5), then node3 (f = 8), then node1 (f = 10)
        Assert.Same(node2, set.RemoveNearest());
        Assert.Equal(2, set.Count);

        Assert.Same(node3, set.RemoveNearest());
        Assert.Equal(1, set.Count);

        Assert.Same(node1, set.RemoveNearest());
        Assert.Equal(0, set.Count);
        Assert.Null(set.RemoveNearest());
    }

    [Fact]
    public void RemoveNearest_BreaksTiesUsingHCostAndInsertionRecency()
    {
        var set = new PathNodeSet();

        var node1 = new PathNode(new BlockPos(1, 1, 1)) { gCost = 5f, hCost = 5f }; // f = 10, h = 5
        var node2 = new PathNode(new BlockPos(2, 2, 2)) { gCost = 6f, hCost = 4f }; // f = 10, h = 4 (lower h)
        var node3 = new PathNode(new BlockPos(3, 3, 3)) { gCost = 6f, hCost = 4f }; // f = 10, h = 4 (same h, newer)

        set.Add(node1);
        set.Add(node2);
        set.Add(node3);

        // node3 is newer than node2 with equal fCost and hCost
        Assert.Same(node3, set.RemoveNearest());
        Assert.Same(node2, set.RemoveNearest());
        Assert.Same(node1, set.RemoveNearest());
    }

    [Fact]
    public void TryFindValue_And_Remove_OperatesCorrectly()
    {
        var set = new PathNodeSet();
        var node = new PathNode(new BlockPos(100, 50, 200)) { gCost = 10f, hCost = 2f };
        set.Add(node);

        var query = new PathNode(new BlockPos(100, 50, 200));
        var found = set.TryFindValue(query);
        Assert.Same(node, found);

        set.Remove(query);
        Assert.Equal(0, set.Count);
        Assert.Null(set.TryFindValue(query));
    }
}
