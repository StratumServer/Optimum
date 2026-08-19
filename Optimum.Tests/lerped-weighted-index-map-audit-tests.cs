using System;
using System.IO;
using Vintagestory.ServerMods;
using Xunit;

namespace Optimum.Tests;

public sealed class LerpedWeightedIndexMapAuditTests
{
    [Fact]
    public void WeightsAtReusesTheCallerBufferWithoutAllocations()
    {
        int[] rawValues =
        {
            0, 1, 2, 3,
            1, 2, 3, 0,
            2, 3, 0, 1,
            3, 0, 1, 2
        };
        var map = new LerpedWeightedIndex2DMap(rawValues, 4, 0, 1, 1);
        float[] output = new float[4];

        map.WeightsAt(1.25f, 1.25f, output);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
        {
            map.WeightsAt(1.25f, 1.25f, output);
        }

        Assert.Equal(allocatedBefore, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void WorldgenCallSitesExposeTheServerOnlyAllocationBoundary()
    {
        string map = Read("VSEssentials/Systems/WorldGen/Standard/Datastructures/LerpedWeightedIndex2DMap.cs");
        string terra = Read("VSEssentials/Systems/WorldGen/Standard/ChunkGen/1.GenTerra/GenTerra.cs");
        string rock = Read("VSEssentials/Systems/WorldGen/Standard/ChunkGen/2.GenRockStrata/GenRockStrata.cs");
        string dungeons = Read("VSSurvivalMod/Systems/WorldGen/Standard/ChunkGen/6.GenStructures/GenDungeons.cs");

        Assert.Contains("public float[] WeightsAt(float x, float z, float[] output)", map);
        Assert.Contains("return side == EnumAppSide.Server;", terra);
        Assert.Contains("return side == EnumAppSide.Server;", rock);
        Assert.Contains("float[] landformWeights = tempDataThreadLocal.Value.landformWeights;", terra);
        Assert.Contains("float[] columnLandformIndexedWeights = tempDataThreadLocal.Value.landformWeights;", terra);
        Assert.Contains("float[] indices = new float[provinces.Variants.Length];", rock);
        Assert.Contains("foreach (var index in map[posXInRegion, posZInRegion])", dungeons);
    }

    [Fact]
    public void RockStrataResultCachingHasSeparateChunkAndProspectingCallers()
    {
        string rock = Read("VSEssentials/Systems/WorldGen/Standard/ChunkGen/2.GenRockStrata/GenRockStrata.cs");
        string prospecting = Read("VSSurvivalMod/Systems/Prospecting/ProPickWorkSpace.cs");

        Assert.Contains("for (int x = 0; x < chunksize; x++)", rock);
        Assert.Contains("for (int z = 0; z < chunksize; z++)", rock);
        Assert.Contains("float distx = (float)distort2dx.Noise(chunkX * chunksize + lx, chunkZ * chunksize + lz);", rock);
        Assert.Contains("float distz = (float)distort2dz.Noise(chunkX * chunksize + lx, chunkZ * chunksize + lz);", rock);
        Assert.Contains("rockStrataGen.preLoad(chunks, chunkX, chunkZ);", prospecting);
        Assert.Contains("rockStrataGen.genBlockColumn(chunks, chunkX, chunkZ, lx, lz);", prospecting);
    }

    [Fact]
    public void GenTerraProfileShowsServerScopedScratchAndChunkAllocations()
    {
        string terra = Read("VSEssentials/Systems/WorldGen/Standard/ChunkGen/1.GenTerra/GenTerra.cs");

        Assert.Contains("return side == EnumAppSide.Server;", terra);
        Assert.Contains("float[] landformWeights = tempDataThreadLocal.Value.landformWeights;", terra);
        Assert.Contains("double[] lerpedAmps = tempDataThreadLocal.Value.LerpedAmplitudes;", terra);
        Assert.Contains("double[] lerpedTh = tempDataThreadLocal.Value.LerpedThresholds;", terra);
        Assert.Contains("amps = new double[terrainGenOctaves];", terra);
        Assert.Contains("thresholds = new double[terrainGenOctaves];", terra);
        Assert.Contains("new ParallelOptions() { MaxDegreeOfParallelism = maxThreads }", terra);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
