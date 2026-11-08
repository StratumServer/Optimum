using System.IO;
using Xunit;

namespace Optimum.Tests;

public class ChunkDecompressMeshingAllocsTests
{
    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    [Fact]
    public void CompressionPatch_EliminatesTrailingBlockSliceCopyAndZeroFill()
    {
        string patch = ReadRepoFile("patches/VintagestoryLib/Vintagestory.Common/Compression.cs.patch");

        Assert.Contains("Compression1.DecompressAndSize(blocksCompressed, num + 4, blocksCompressed.Length - (num + 4), out var buffer2)", patch);
        Assert.Contains("GC.AllocateUninitializedArray<int>(roundedSize)", patch);
    }

    [Fact]
    public void TesselatorPatches_ReuseScratchBlockPosInsteadOfAllocatingPerBlock()
    {
        string chunkTesselatorPatch = ReadRepoFile(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch");
        string surfaceLayerPatch = ReadRepoFile(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/SurfaceLayerTesselator.cs.patch");
        string bleedingJsonPatch = ReadRepoFile(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/BleedingJsonTesselator.cs.patch");

        Assert.Contains("drawYAdjustable.AdjustYPosition(tmpPos.Set(tCTCache.posX, tCTCache.posY, tCTCache.posZ)", chunkTesselatorPatch);
        Assert.Contains("drawYAdjustable.AdjustYPosition(tmpPos.Set(vars.posX, vars.posY, vars.posZ)", chunkTesselatorPatch);
        Assert.Contains("BlockPos blockPos = tmpPos ?? (tmpPos = new BlockPos(0));", surfaceLayerPatch);
        Assert.Contains("BlockPos blockPos = tmpPos ?? (tmpPos = new BlockPos(0));", bleedingJsonPatch);
    }

    [Fact]
    public void PatcherProgram_RegistersDecompressAndTesselatorTargets()
    {
        string program = ReadRepoFile("Optimum.Patcher/Program.cs");
        string cecilOwned = ReadRepoFile("patches/cecil-owned.list");

        Assert.Contains("new(\"Vintagestory.Client.NoObf.ChunkTesselator\", \"TesselateBlock\", 6)", program);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.SurfaceLayerTesselator\", \"Tesselate\", 1)", program);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.BleedingJsonTesselator\", \"Tesselate\", 1)", program);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.BleedingJsonTesselator\", \"DoMeshWithBleeding\", 3)", program);
        Assert.Contains("new(\"Vintagestory.Common.Compression\", \"DecompressToInts\", 2)", program);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/SurfaceLayerTesselator.cs.patch", cecilOwned);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/BleedingJsonTesselator.cs.patch", cecilOwned);
    }
}
