using Xunit;

namespace Optimum.Tests;

public sealed class WorldgenR1WorkspacePatchContractTests
{
    [Fact]
    public void TerrainGeneratorsOwnScratchStatePerWorker()
    {
        string terra = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/1.GenTerra/GenTerra.cs.patch");
        string rock = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/2.GenRockStrata/GenRockStrata.cs.patch");
        string caves = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/3.GenCaves/GenCaves.cs.patch");
        string layers = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/4.GenBlockLayers/GenBlockLayers.cs.patch");

        Assert.Contains("ThreadLocal<GenerationWorkspace> workspaces", terra);
        Assert.Contains("landformMapLock", terra);
        Assert.Contains("ThreadLocal<RockWorkspace> workspaces", rock);
        Assert.Contains("provinceMapLock", rock);
        Assert.Contains("ThreadLocal<LCGRandom> caveRandThreadLocal", caves);
        Assert.Contains("ThreadLocal<BlockLayerWorkspace> workspaces", layers);
    }

    [Fact]
    public void R1FixesTheKnownSharedCollectionGenerators()
    {
        string ponds = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/7.GenPonds/GenPonds.cs.patch");
        string postProcess = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/1.GenTerra/GenTerraPostProcess.cs.patch");
        string partial = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/GenPartial.cs.patch");
        string deposits = PatchReader.ReadPatch("patches/VSEssentials/Systems/WorldGen/Standard/ChunkGen/5.GenDeposits/GenDeposits.cs.patch");

        Assert.Contains("ThreadLocal<PondWorkspace> workspaces", ponds);
        Assert.Contains("ThreadLocal<IWorldGenBlockAccessor> blockAccessors", ponds);
        Assert.Contains("CurrentBlockAccessor", ponds);
        Assert.DoesNotContain("readonly QueueOfInt searchPositionsDeltas", ponds);
        Assert.Contains("ThreadLocal<TerraPostProcessWorkspace> workspaces", postProcess);
        Assert.Contains("ThreadLocal<IWorldGenBlockAccessor> blockAccessors", postProcess);
        Assert.Contains("CurrentBlockAccessor", postProcess);
        Assert.DoesNotContain("HashSet<int> chunkVisitedNodes", postProcess);
        Assert.Contains("ThreadLocal<LCGRandom> chunkRandThreadLocal", partial);
        Assert.Contains("chunkRandThreadLocal?.Value ?? this.chunkRand", deposits);
        Assert.Contains("ThreadLocal<IBlockAccessor> blockAccessors", deposits);
        Assert.DoesNotContain("ThreadLocal<LCGRandom> depositRandThreadLocal", deposits);
        Assert.DoesNotContain("ThreadLocal<Dictionary<BlockPos, DepositVariant>> subDepositsToPlaceThreadLocal", deposits);
    }

    [Fact]
    public void SchedulerOwnsFootprintsAroundEveryConcurrentPopulate()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ServerSystemSupplyChunks.cs.patch");

        Assert.Contains("chunkthread.optimumWorldgenFootprints", source);
        Assert.Contains("TryAcquireWorldgenFootprint", source);
        Assert.Contains("out OptimumWorldgenFootprintLease footprintLease", source);
        Assert.Contains("footprintLease?.Dispose()", source);
        Assert.Contains("requestedChunkColumn.FlagToRequeue();", source);
    }

    [Fact]
    public void UnloadReservesTheSameFootprintUntilPersistenceCompletes()
    {
        string unload = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ServerSystemUnloadChunks.cs.patch");
        string thread = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Server/ChunkServerThread.cs.patch");
        string patcher = System.IO.File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));

        int acquire = unload.IndexOf("TryAcquireOptimumWorldgenFootprint", System.StringComparison.Ordinal);
        int readLock = unload.IndexOf("item2.generatingLock.AcquireReadLock()", acquire, System.StringComparison.Ordinal);
        int setChunks = unload.IndexOf("gameDatabase.SetChunks", readLock, System.StringComparison.Ordinal);
        int setMapChunks = unload.IndexOf("gameDatabase.SetMapChunks", setChunks, System.StringComparison.Ordinal);
        int dispose = unload.IndexOf("leases[i]?.Dispose()", setMapChunks, System.StringComparison.Ordinal);

        Assert.True(acquire >= 0);
        Assert.True(readLock > acquire);
        Assert.True(setChunks > readLock);
        Assert.True(setMapChunks > setChunks);
        Assert.True(dispose > setMapChunks);
        Assert.Contains("optimumWorldgenFootprints", thread);
        Assert.Contains("TryAcquireOptimumWorldgenFootprint", thread);
        Assert.Contains("\"TryAcquireOptimumWorldgenFootprint\"", patcher);
        Assert.Contains("\"optimumUnloadGenLeases\"", patcher);
    }
}
