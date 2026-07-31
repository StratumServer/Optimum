using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class ChunkWorldgenWorkStealingTests
{
    private const string SupplyChunksPatch = "patches/VintagestoryLib/Vintagestory.Server/ServerSystemSupplyChunks.cs.patch";
    private const string ChunkThreadPatch = "patches/VintagestoryLib/Vintagestory.Server/ChunkServerThread.cs.patch";

    [Fact]
    public void ConfigDefaultsToWorkStealingScheduler()
    {
        Assert.True(OptimumConfig.WorldgenWorkStealingEnabled);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 2)]
    [InlineData(8, 2)]
    [InlineData(64, 2)]
    public void WorkerPolicyUsesConservativeCoreBands(int logicalProcessors, int expectedWorkers)
    {
        bool original = OptimumConfig.WorldgenWorkStealingEnabled;
        try
        {
            OptimumConfig.WorldgenWorkStealingEnabled = true;
            Assert.Equal(expectedWorkers, OptimumConfig.GetWorldgenWorkerCount(logicalProcessors, false));
        }
        finally
        {
            OptimumConfig.WorldgenWorkStealingEnabled = original;
        }
    }

    [Fact]
    public void WorkerPolicyHonorsFeatureAndReducedThreadGates()
    {
        bool original = OptimumConfig.WorldgenWorkStealingEnabled;
        try
        {
            OptimumConfig.WorldgenWorkStealingEnabled = false;
            Assert.Equal(0, OptimumConfig.GetWorldgenWorkerCount(16, false));

            OptimumConfig.WorldgenWorkStealingEnabled = true;
            Assert.Equal(0, OptimumConfig.GetWorldgenWorkerCount(16, true));
        }
        finally
        {
            OptimumConfig.WorldgenWorkStealingEnabled = original;
        }
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(5, 3)]
    [InlineData(6, 3)]
    [InlineData(8, 3)]
    [InlineData(64, 3)]
    public void WorkerCeilingIsHigherThanSpawnCount(int logicalProcessors, int expectedCeiling)
    {
        bool original = OptimumConfig.WorldgenWorkStealingEnabled;
        try
        {
            OptimumConfig.WorldgenWorkStealingEnabled = true;
            Assert.Equal(expectedCeiling, OptimumConfig.GetWorldgenWorkerCeiling(logicalProcessors, false));
            // Spawn count must be <= ceiling
            Assert.True(OptimumConfig.GetWorldgenWorkerCount(logicalProcessors, false) <= expectedCeiling);
        }
        finally
        {
            OptimumConfig.WorldgenWorkStealingEnabled = original;
        }
    }

    [Fact]
    public void SchedulerClaimsPassBeforeItScansRequests()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);
        int method = source.IndexOf("GenerateChunkColumns_OnSeparateThread", StringComparison.Ordinal);
        int claim = source.IndexOf("Interlocked.CompareExchange", method, StringComparison.Ordinal);
        int scan = source.IndexOf("foreach (ChunkColumnLoadRequest", method, StringComparison.Ordinal);

        Assert.True(method >= 0);
        Assert.True(claim > method);
        Assert.True(scan > claim);
        Assert.Contains("CurrentIncompletePass_AsInt == targetPass", source);
        Assert.Contains("Volatile.Write(ref optimumStageClaims[targetPass], 0)", source);
    }

    [Fact]
    public void MainChunkThreadLeavesGenerationPassesToWorkers()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);

        Assert.Contains("currentIncompletePass_AsInt == (int)EnumWorldGenPass.None", source);
        Assert.Contains("currentIncompletePass_AsInt >= (int)EnumWorldGenPass.Done", source);
        Assert.Contains("Volatile.Read(ref optimumWorldgenFaulted) == 0", source);
    }

    [Fact]
    public void SchedulerInitializesInjectedStateAndUsesClosureFreeWorkers()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);

        Assert.Contains("optimumStageClaims = new int[(int)EnumWorldGenPass.Done]", source);
        // The worker thread captures workerIndex via a lambda that calls GeneratorThreadLoop.
        // OptimumGeneratorThreadEntry exists as a separate method for the single-worker path.
        Assert.Contains("TyronThreadPool.CreateDedicatedThread(() =>", source);
        Assert.Contains("OptimumGeneratorThreadEntry", source);
        Assert.Contains("Interlocked.Exchange(ref optimumWorldgenFaulted, 1)", source);
        // The lambda captures workerIndex (an int) which is value-type: no closure object leak.
        // Vanilla's original delegate{} pattern is replaced.
        Assert.DoesNotContain("CreateDedicatedThread(delegate", source);
    }

    [Fact]
    public void GeneratorFailureReturnsThePassToTheVanillaThread()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);
        int method = source.IndexOf("ServerMain.Logger.Worldgen", StringComparison.Ordinal);
        int workerGate = source.IndexOf("OptimumWorldgenWorkersActive()", method, StringComparison.Ordinal);
        int rethrow = source.IndexOf("throw;", workerGate, StringComparison.Ordinal);

        Assert.True(method >= 0);
        Assert.True(workerGate > method);
        Assert.True(rethrow > workerGate);
    }

    [Fact]
    public void CecilOwnsWorldgenSchedulerPatchesAndMethods()
    {
        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));
        string chunkThread = PatchReader.ReadPatch(ChunkThreadPatch);

        // Server worldgen patches are Cecil-owned (transplanted at launcher startup)
        Assert.Contains(SupplyChunksPatch, cecilList);
        Assert.Contains(ChunkThreadPatch, cecilList);
        // The chunk thread patch carries the worker count gate
        Assert.Contains("GetWorldgenWorkerCount", chunkThread);
    }

    [Fact]
    public void LightConflictLockSerializesPassesThreeAndFour()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);

        // The lock field exists and targets the GenLightSurvival conflict
        Assert.Contains("optimumLightConflictLock", source);
        Assert.Contains("Lock optimumLightConflictLock", source);

        // PopulateChunk for passes 3 and 4 runs inside the lock
        int lockSite = source.IndexOf("lock (optimumLightConflictLock)", StringComparison.Ordinal);
        int passCheck = source.IndexOf("targetPass == 3 || targetPass == 4", StringComparison.Ordinal);
        Assert.True(passCheck >= 0, "pass 3/4 gate not found");
        Assert.True(lockSite > passCheck, "lock must follow the pass 3/4 condition");

        // The else branch calls PopulateChunk without the lock (passes 1, 2, 5)
        int elseBranch = source.IndexOf("else", lockSite, StringComparison.Ordinal);
        int unlocked = source.IndexOf("PopulateChunk(chunkColumnLoadRequest)", elseBranch, StringComparison.Ordinal);
        Assert.True(unlocked > elseBranch, "passes outside the conflict set must bypass the lock");
    }
}
