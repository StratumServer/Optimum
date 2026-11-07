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
}
