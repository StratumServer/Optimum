using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class EntityPartitioningTests
{
    [Fact]
    public void EntityPartitioningPatch_ReusesChunksAndLists()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VSEssentials/Systems/EntityPartitioning.cs.patch");
        Assert.True(File.Exists(patchPath), "EntityPartitioning.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        // Verify EntityPartitionChunk Clear method
        Assert.Contains("public void Clear()", patched);
        Assert.Contains("Entities[i]?.Clear();", patched);
        Assert.Contains("InanimateEntities[i]?.Clear();", patched);

        // Verify chunk pooling
        Assert.Contains("private readonly Stack<EntityPartitionChunk> chunkPool = new Stack<EntityPartitionChunk>();", patched);
        Assert.Contains("chunkPool.Push(partition);", patched);
        Assert.Contains("partition = chunkPool.Count > 0 ? chunkPool.Pop() : new EntityPartitionChunk();", patched);

        // Verify PartitionSingleEntity and non-destructive RePartitionPlayer
        Assert.Contains("private void PartitionSingleEntity(Entity entity)", patched);
        Assert.Contains("PartitionSingleEntity(entity);", patched);
        Assert.DoesNotContain("PartitionEntities(new Entity[] { entity });", patched);
    }

    [Fact]
    public void EntityPartitioningPatch_EliminatesClosureAllocationsInNearestEntityQueries()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VSEssentials/Systems/EntityPartitioning.cs.patch");
        Assert.True(File.Exists(patchPath), "EntityPartitioning.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        // Verify FindNearestEntityInternal implementation
        Assert.Contains("private Entity FindNearestEntityInternal(Vec3d position, double radius, ActionConsumable<Entity> matches, bool requireInteractable, EnumEntitySearchType searchType)", patched);
        Assert.Contains("return FindNearestEntityInternal(position, radius, matches, true, EnumEntitySearchType.Creatures);", patched);
        Assert.Contains("return FindNearestEntityInternal(position, radius, matches, false, searchType);", patched);

        // Verify that closure lambdas are eliminated in GetNearestInteractableEntity and GetNearestEntity
        Assert.DoesNotContain("(e) => e.IsInteractable", patched);
        Assert.DoesNotContain("(e) => matches(e) && e.IsInteractable", patched);
        Assert.DoesNotContain("WalkEntities(position.X, position.Y, position.Z, radius, (e) =>", patched);

        // Verify zero-allocation loop and distance pruning
        Assert.Contains("if (requireInteractable && !entity.IsInteractable) continue;", patched);
        Assert.Contains("double distSq = entity.Pos.SquareDistanceTo(position);", patched);
        Assert.Contains("if (distSq < nearestDistanceSq && (matches == null || matches(entity)))", patched);
    }
}
