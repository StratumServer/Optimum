using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class ChunkWorldgenWorkStealingTests
{
    private const string SupplyChunksPatch = "patches/VintagestoryLib/Vintagestory.Server/ServerSystemSupplyChunks.cs.patch";
    private const string ChunkThreadPatch = "patches/VintagestoryLib/Vintagestory.Server/ChunkServerThread.cs.patch";
    private const string RequestPatch = "patches/VintagestoryLib/Vintagestory.Server/ChunkColumnLoadRequest.cs.patch";

    [Fact]
    public void ConfigDefaultsToSerialWorldgen()
    {
        Assert.False(OptimumConfig.WorldgenWorkStealingEnabled);
        Assert.False(new OptimumConfigData().WorldgenWorkStealing);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(8, false)]
    public void AutoPolicyReturnsSerialForEightOrFewerCores(
        int logicalProcessors,
        bool staleEnabledValue)
    {
        bool original = OptimumConfig.WorldgenWorkStealingEnabled;
        string originalPolicy = OptimumConfig.WorldgenWorkerPolicy;
        try
        {
            OptimumConfig.WorldgenWorkStealingEnabled = staleEnabledValue;
            OptimumConfig.WorldgenWorkerPolicy = "auto";

            Assert.Equal(0, OptimumConfig.GetWorldgenWorkerCount(logicalProcessors, false));
            Assert.Equal(0, OptimumConfig.GetWorldgenWorkerCeiling(logicalProcessors, false));
        }
        finally
        {
            OptimumConfig.WorldgenWorkStealingEnabled = original;
            OptimumConfig.WorldgenWorkerPolicy = originalPolicy;
        }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(64)]
    public void AutoPolicyReturnsOneWorkerForMoreThanEightCores(int logicalProcessors)
    {
        string originalPolicy = OptimumConfig.WorldgenWorkerPolicy;
        try
        {
            OptimumConfig.WorldgenWorkerPolicy = "auto";

            Assert.Equal(1, OptimumConfig.GetWorldgenWorkerCount(logicalProcessors, false));
            Assert.True(OptimumConfig.GetWorldgenWorkerCeiling(logicalProcessors, false) >= 1);
        }
        finally
        {
            OptimumConfig.WorldgenWorkerPolicy = originalPolicy;
        }
    }

    [Theory]
    [InlineData("serial", 64, 0)]
    [InlineData("0", 64, 0)]
    [InlineData("1", 64, 1)]
    [InlineData("2", 64, 2)]
    [InlineData("3", 64, 3)]
    public void ExplicitPolicyOverridesAutoDetection(string policy, int cores, int expected)
    {
        string originalPolicy = OptimumConfig.WorldgenWorkerPolicy;
        try
        {
            OptimumConfig.WorldgenWorkerPolicy = policy;
            Assert.Equal(expected, OptimumConfig.GetWorldgenWorkerCount(cores, false));
        }
        finally
        {
            OptimumConfig.WorldgenWorkerPolicy = originalPolicy;
        }
    }

    [Fact]
    public void AutomaticWorkerPolicyReturnsZeroWithReducedThreads()
    {
        bool original = OptimumConfig.WorldgenWorkStealingEnabled;
        try
        {
            OptimumConfig.WorldgenWorkStealingEnabled = true;
            Assert.Equal(0, OptimumConfig.GetWorldgenWorkerCount(16, true));
            Assert.Equal(0, OptimumConfig.GetWorldgenWorkerCeiling(16, true));
        }
        finally
        {
            OptimumConfig.WorldgenWorkStealingEnabled = original;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExactOverrideReturnsRequestedWorkerCount(int requestedWorkers)
    {
        Assert.Equal(
            requestedWorkers,
            OptimumConfig.ResolveWorldgenWorkerCount(
                logicalProcessors: 1,
                reducedServerThreads: false,
                mtOverride: "1",
                workerCountOverride: requestedWorkers.ToString()));
    }

    [Theory]
    [InlineData("0", "1")]
    [InlineData("1", "")]
    [InlineData("1", "invalid")]
    [InlineData("1", "0")]
    [InlineData("1", "-1")]
    [InlineData("1", "4")]
    public void InvalidOrIncompleteOverrideReturnsZero(string? mtOverride, string? workerCountOverride)
    {
        string originalPolicy = OptimumConfig.WorldgenWorkerPolicy;
        try
        {
            OptimumConfig.WorldgenWorkerPolicy = "serial";
            Assert.Equal(
                0,
                OptimumConfig.ResolveWorldgenWorkerCount(
                    logicalProcessors: 64,
                    reducedServerThreads: false,
                    mtOverride,
                    workerCountOverride));
        }
        finally
        {
            OptimumConfig.WorldgenWorkerPolicy = originalPolicy;
        }
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData("", "1")]
    [InlineData("2", "1")]
    [InlineData("1", null)]
    public void NonExplicitOverrideFallsToConfigPolicy(string? mtOverride, string? workerCountOverride)
    {
        string originalPolicy = OptimumConfig.WorldgenWorkerPolicy;
        try
        {
            OptimumConfig.WorldgenWorkerPolicy = "auto";
            int result = OptimumConfig.ResolveWorldgenWorkerCount(
                logicalProcessors: 64,
                reducedServerThreads: false,
                mtOverride,
                workerCountOverride);
            // Auto on 64 cores → 1 worker
            Assert.Equal(1, result);
        }
        finally
        {
            OptimumConfig.WorldgenWorkerPolicy = originalPolicy;
        }
    }

    [Fact]
    public void ReducedThreadsOverrideExactWorkerRequest()
    {
        Assert.Equal(
            0,
            OptimumConfig.ResolveWorldgenWorkerCount(
                logicalProcessors: 64,
                reducedServerThreads: true,
                mtOverride: "1",
                workerCountOverride: "3"));
    }

    [Fact]
    public void SchedulerClaimsPassBeforeItScansRequests()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);
        int method = source.IndexOf("GenerateChunkColumns_OnSeparateThread", StringComparison.Ordinal);
        int passClaim = source.IndexOf("TryClaimWorldgenPass(targetPass)", method, StringComparison.Ordinal);
        int dispatchClaim = source.IndexOf("TryClaimDispatch()", method, StringComparison.Ordinal);
        int scan = source.IndexOf("foreach (ChunkColumnLoadRequest", method, StringComparison.Ordinal);
        int neighbourhood = source.IndexOf("ensurePrettyNeighbourhood(requestedChunkColumn)", method, StringComparison.Ordinal);

        Assert.True(method >= 0);
        Assert.True(passClaim > method);
        Assert.True(dispatchClaim > scan);
        Assert.True(scan > passClaim);
        Assert.True(neighbourhood > dispatchClaim);
        Assert.Contains("if (!ensurePrettyNeighbourhood(requestedChunkColumn))", source);
        Assert.Contains("continue;", source[source.IndexOf("if (!ensurePrettyNeighbourhood(requestedChunkColumn)", method, StringComparison.Ordinal)..]);
        Assert.Contains("requestedChunkColumn.CurrentIncompletePass_AsInt != targetPass", source);
        Assert.Contains("ReleaseDispatch()", source);
        Assert.Contains("ReleaseWorldgenPass(targetPass)", source);
        Assert.DoesNotContain("optimumStageClaims", source);
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

        Assert.Contains("optimumWorldgenPassCaps = new int[(int)EnumWorldGenPass.Done]", source);
        Assert.Contains("optimumWorldgenPassInFlight = new int[(int)EnumWorldGenPass.Done]", source);
        Assert.Contains("OptimumInitializeWorldgenWorkers()", source);
        Assert.Contains("Volatile.Write(ref optimumWorldgenAuditReady, 1)", source);
        Assert.Contains("Volatile.Write(ref optimumWorldgenWorkersStarted, 1)", source);
        Assert.DoesNotContain("optimumStageClaims", source);
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
    public void RequestCarriesTheSecondDispatchClaimLayer()
    {
        string source = PatchReader.ReadPatch(RequestPatch);

        Assert.Contains("OptimumDispatchClaim dispatchClaim", source);
        Assert.Contains("TryClaimDispatch", source);
        Assert.Contains("ReleaseDispatch", source);
    }

    [Fact]
    public void GeneratorFailureKeepsVanillaPassBehavior()
    {
        string source = File.ReadAllText(PatchReader.FindRepositoryFile(
            "build/VintagestoryLib/Vintagestory.Server/ServerSystemSupplyChunks.cs"));
        int method = source.IndexOf("An error was thrown in pass {5} by handler '{6}' when generating chunk column", StringComparison.Ordinal);
        int passCondition = source.IndexOf("CurrentIncompletePass <= EnumWorldGenPass.Terrain", method, StringComparison.Ordinal);

        Assert.True(method >= 0);
        Assert.True(passCondition > method);

        string runGenerators = source.Substring(method, passCondition - method);
        Assert.DoesNotContain("OptimumWorldgenWorkersActive()", runGenerators);
        Assert.Contains("if (optimumWorkerIndex > 0)", runGenerators);
        Assert.Contains("throw;", runGenerators);
        Assert.Contains("handlerName", runGenerators);
        Assert.Contains("CurrentIncompletePass <= EnumWorldGenPass.Terrain", source);
    }

    [Fact]
    public void WorkerFailureDrainsBeforeTheChunkThreadResumes()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);

        Assert.Contains("optimumWorldgenDispatchesInFlight", source);
        Assert.Contains("OptimumWorldgenWorkersDrained()", source);
        Assert.Contains("Thread.Sleep(1)", source);
        Assert.Contains("Volatile.Read(ref optimumWorldgenFaulted) != 0", source);
    }

    [Fact]
    public void CecilOwnsWorldgenSchedulerPatchesAndMethods()
    {
        string cecilList = File.ReadAllText(PatchReader.FindRepositoryFile("patches/cecil-owned.list"));
        string chunkThread = PatchReader.ReadPatch(ChunkThreadPatch);
        string patcher = File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));

        // Server worldgen patches are Cecil-owned (transplanted at launcher startup)
        Assert.Contains(SupplyChunksPatch, cecilList);
        Assert.Contains(ChunkThreadPatch, cecilList);
        Assert.Contains("\"Vintagestory.Server.ServerSystemSupplyChunks\", \"runGenerators\", 2", patcher);
        Assert.Contains("\"Vintagestory.Server.ServerSystemSupplyChunks\", \"loadChunkAreaBlocking\", 6", patcher);
        Assert.Contains("\"RunWorldgenGeneratorsUnlocked\"", patcher);
        // The chunk thread patch carries the exact override gate.
        Assert.Contains("ResolveWorldgenWorkerCount", chunkThread);
    }

    [Fact]
    public void MainChunkThreadGenerationUsesTheRequestDispatchClaim()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);

        Assert.Contains("if (!requestedChunkColumn.TryClaimDispatch())", source);
        Assert.Contains("requestedChunkColumn.ReleaseDispatch()", source);
        Assert.Contains("if (!requestedChunkColumn2.TryClaimDispatch())", source);
        Assert.Contains("requestedChunkColumn2.ReleaseDispatch()", source);
    }

    [Fact]
    public void LightConflictLockSerializesEveryWorldgenEntryPoint()
    {
        string source = PatchReader.ReadPatch(SupplyChunksPatch);

        // The lock field exists and targets the GenLightSurvival conflict
        Assert.Contains("optimumLightConflictLock", source);
        Assert.Contains("Lock optimumLightConflictLock", source);

        // The shared illuminator is protected at the common generator entry point.
        int runGenerators = source.IndexOf("private void runGenerators", StringComparison.Ordinal);
        int lockSite = source.IndexOf("optimumLightConflictLock.EnterScope()", runGenerators, StringComparison.Ordinal);
        int passCheck = source.IndexOf("EnumWorldGenPass.Vegetation", runGenerators, StringComparison.Ordinal);
        Assert.True(passCheck >= 0, "pass 3/4 gate not found");
        Assert.True(runGenerators >= 0, "generator entry point not found");
        Assert.True(lockSite > passCheck, "lock must follow the pass 3/4 condition");
        Assert.Contains("EnumWorldGenPass.NeighbourSunLightFlood", source);
        Assert.Contains("RunWorldgenGeneratorsUnlocked(chunkRequest, forPass)", source);
    }
}
