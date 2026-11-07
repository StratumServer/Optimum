using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class BlockEntityBarrelTests
{
    [Fact]
    public void BEBarrelPatch_GuardsPeriodicRecipeSearchWithDirtyFlag()
    {
        string patchPath = PatchReader.FindRepositoryFile(
            "patches/VSSurvivalMod/Systems/Barrel/BEBarrel.cs.patch");
        Assert.True(File.Exists(patchPath), "BEBarrel.cs.patch must exist");
        string patched = PatchReader.ReadPatchedContent(patchPath);

        Assert.Contains("protected bool recipeDirty = true;", patched);
        Assert.Contains("recipeDirty = false;", patched);
        Assert.Contains("recipeDirty = true;", patched);
        Assert.Contains("if (recipeDirty && !inventory[0].Empty && CurrentRecipe == null)", patched);
        Assert.DoesNotContain("if (!inventory[0].Empty && CurrentRecipe == null)", patched);
    }
}
