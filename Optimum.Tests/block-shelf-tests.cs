using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class BlockShelfTests
{
    [Fact]
    public void BlockShelfPatch_EliminatesPerFrameLinqAndArrayAllocations()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VSSurvivalMod/Block/BlockShelf.cs.patch");
        Assert.True(File.Exists(patchPath), "BlockShelf.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("var mealContainers = mealContainerList.ToArray();", patched);
        Assert.Contains("var crockSealers = crockSealerList.ToArray();", patched);
        Assert.Contains("sampleMealContainer != null && beshelf.CanUse(sampleMealContainer, bs)", patched);
        Assert.Contains("sampleCrockSealer != null && beshelf.CanUse(sampleCrockSealer, bs)", patched);
        Assert.DoesNotContain("usableItemStacklist.Where", patched);
    }
}
