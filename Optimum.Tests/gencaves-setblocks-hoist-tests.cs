using System.IO;
using Xunit;

namespace Optimum.Tests;

public class GenCavesSetBlocksHoistTests
{
    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    [Fact]
    public void SourceAndRuntimePatches_HoistColumnInvariantsAndPreserveTwoPassStructure()
    {
        string sourcePatch = ReadRepoFile(
            "patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/3.GenCaves/GenCaves.cs.patch");
        string runtimePatch = ReadRepoFile(
            "patches/runtime/VSEssentials/Vintagestory/ServerMods/GenCaves.cs.patch");

        Assert.Contains("if (xdistRel + zdistRel > 1.0) continue;", sourcePatch);
        Assert.Contains("double heightrndSq = heightrnd * heightrnd;", sourcePatch);
        Assert.Contains("int maxWaterY = Math.Min(maxdy + 10, worldheight - 1);", sourcePatch);
        Assert.Contains("int maxCarveY = Math.Min(maxdy + 10, worldheight - 1);", sourcePatch);
        Assert.Contains("lightUpdatePos ??= new BlockPos(0);", sourcePatch);

        Assert.Contains("if (num10 + num11 > 1.0)", runtimePatch);
        Assert.Contains("double heightrndSq = num12 * num12;", runtimePatch);
        Assert.Contains("int maxWaterY = Math.Min(num4 + 10, worldheight - 1);", runtimePatch);
        Assert.Contains("int maxCarveY = Math.Min(num4 + 10, worldheight - 1);", runtimePatch);
        Assert.Contains("lightUpdatePos ??= new BlockPos(0);", runtimePatch);
    }

    [Fact]
    public void ModPatcher_RegistersGenCavesSetBlocksTarget()
    {
        string modPatcher = ReadRepoFile("Optimum.Patcher/mod-patcher.cs");
        Assert.Contains("new(\"Vintagestory.ServerMods.GenCaves\", \"SetBlocks\", 11)", modPatcher);
    }
}
