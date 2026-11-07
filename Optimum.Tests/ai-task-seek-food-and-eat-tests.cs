using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class AiTaskSeekFoodAndEatTests
{
    [Fact]
    public void AiTaskSeekFoodAndEatPatch_ContainsPhaseDispersionAndJitter()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VSEssentials/Entity/AI/Tasks/AiTaskSeekFoodAndEat.cs.patch");
        Assert.True(File.Exists(patchPath), "AiTaskSeekFoodAndEat.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("lastPOISearchTotalMs = entity.World.ElapsedMilliseconds - entity.World.Rand.Next(15000);", patched);
        Assert.Contains("lastPOISearchTotalMs = entity.World.ElapsedMilliseconds + entity.World.Rand.Next(-1500, 1501);", patched);
    }

    [Fact]
    public void AiTaskSeekFoodAndEatRefactoredPatch_ContainsPhaseDispersionAndJitter()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VSEssentials/Entity/AI/TasksRefactored/AiTaskSeekFoodAndEat.cs.patch");
        Assert.True(File.Exists(patchPath), "Refactored AiTaskSeekFoodAndEat.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("lastPOISearchTotalMs = entity.World.ElapsedMilliseconds - entity.World.Rand.Next(cooldown);", patched);
        Assert.Contains("lastPOISearchTotalMs = entity.World.ElapsedMilliseconds + entity.World.Rand.Next(-jitter, jitter + 1);", patched);
    }
}
