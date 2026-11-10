using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class ChatCommandResilienceTests
{
    [Fact]
    public void ChatCommandImpl_ValidateRecursive_DefaultsMissingDescriptionToName()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VintagestoryLib/Vintagestory.Common/ChatCommandImpl.cs.patch");
        Assert.True(File.Exists(patchPath), "ChatCommandImpl.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("description = name ?? string.Empty;", patched);

        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Common/ChatCommandImpl.cs.patch", cecilList);

        string patcherProgram = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("new(\"Vintagestory.Common.ChatCommandImpl\", \"ValidateRecursive\", 0)", patcherProgram);
    }

    [Fact]
    public void ModSystemVillagerDebug_SubcommandsHaveDescriptions()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VSSurvivalMod/Lore/Village/ModSystemVillagerDebug.cs.patch");
        Assert.True(File.Exists(patchPath), "ModSystemVillagerDebug.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains(".WithDesc(\"Toggle A* pathfinding debug for entity\")", patched);
        Assert.Contains(".WithDesc(\"Villager profile commands\")", patched);
        Assert.Contains(".WithDesc(\"Freeze villager activity\")", patched);
        Assert.Contains(".WithDesc(\"Unfreeze villager activity\")", patched);
        Assert.Contains(".WithDesc(\"Show current villager outfit slots\")", patched);
    }

    [Fact]
    public void CollectibleBehaviorBuffable_SubcommandsHaveDescriptions()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VSEssentials/CollectibleBehavior/CollectibleBehaviorBuffable.cs.patch");
        Assert.True(File.Exists(patchPath), "CollectibleBehaviorBuffable.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains(".WithDesc(\"Collectible buff commands\")", patched);
        Assert.Contains(".WithDesc(\"Add buff to collectible\")", patched);
        Assert.Contains(".WithDesc(\"Clear buffs from collectible\")", patched);
    }
}
