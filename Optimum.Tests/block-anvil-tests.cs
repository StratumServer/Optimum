using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class BlockAnvilTests
{
    [Fact]
    public void BlockAnvilPatch_ProvidesSinglePassPrewarming()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VSSurvivalMod/Block/BlockAnvil.cs.patch");
        Assert.True(File.Exists(patchPath), "BlockAnvil.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("PrewarmAnvilInteractions", patched);
        Assert.Contains("BuildInteractions", patched);
        Assert.Contains("workableByTier[t]", patched);
        Assert.Contains("capi.ObjectCache[\"anvilBlockInteractions\" + t]", patched);
    }

    [Fact]
    public void BlockAnvilPatch_DefersPrewarmingToLevelFinalizeAndGuardsExceptions()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VSSurvivalMod/Block/BlockAnvil.cs.patch");
        Assert.True(File.Exists(patchPath), "BlockAnvil.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("capi.Event.LevelFinalize += () => PrewarmAnvilInteractions(capi);", patched);
        Assert.Contains("capi.Event.LeaveWorld += () => { s_prewarmed = false; };", patched);
        Assert.Contains("workable.GetRequiredAnvilTier(stack)", patched);
        Assert.Contains("catch", patched);
    }
}
