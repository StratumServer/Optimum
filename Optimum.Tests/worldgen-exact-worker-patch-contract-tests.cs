using System;
using Xunit;

namespace Optimum.Tests;

public class WorldgenExactWorkerPatchContractTests
{
    private const string ChunkThreadPatch = "patches/VintagestoryLib/Vintagestory.Server/ChunkServerThread.cs.patch";
    private const string SupplyChunksPatch = "patches/VintagestoryLib/Vintagestory.Server/ServerSystemSupplyChunks.cs.patch";

    [Fact]
    public void ChunkThreadUsesStrictOverrideResolutionAndRecordsExactMode()
    {
        string source = PatchReader.ReadPatch(ChunkThreadPatch);

        Assert.Contains("ResolveWorldgenWorkerCount", source);
        Assert.Contains("optimumExactWorldgenWorkerMode", source);
        Assert.Contains("worldgen worker override rejected", source);
        Assert.Contains("bool requestedParallelWorldgen = worldgenMtOverride == \"1\" && workerCountOverride != null", source);
        Assert.Contains("if (requestedParallelWorldgen && !optimumExactWorldgenWorkerMode)", source);
        Assert.DoesNotContain("Math.Clamp(forcedWorkers", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactModeFreezesBothAdaptiveMutationPaths()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);

        Assert.Contains("chunkthread.optimumExactWorldgenWorkerMode ? chunkthread.additionalWorldGenThreadsCount", source);
        Assert.Contains("!chunkthread.optimumExactWorldgenWorkerMode && Volatile.Read(ref optimumPostSpawnRaised)", source);
        Assert.Contains("!chunkthread.optimumExactWorldgenWorkerMode && optimumAdaptiveController.ShouldEvaluate()", source);
    }

    [Fact]
    public void WorkerLoopProtectsItsDivisionFromZero()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);

        Assert.Contains("Math.Max(1, chunkthread.additionalWorldGenThreadsCount)", source);
    }
}
