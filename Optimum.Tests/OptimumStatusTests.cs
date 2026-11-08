using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class OptimumStatusTests
{
    [Fact]
    public void DescribeTogglesCoversEveryPersistedField()
    {
        var names = OptimumConfig.DescribeToggles().Select(t => t.Name).ToHashSet();
        foreach (var prop in typeof(OptimumConfigData).GetProperties())
        {
            Assert.Contains(prop.Name, names);
        }
    }

    [Fact]
    public void DescribeTogglesReportsCurrentValues()
    {
        bool original = OptimumConfig.EntityShadowCull;
        try
        {
            OptimumConfig.EntityShadowCull = false;
            var entry = OptimumConfig.DescribeToggles().Single(t => t.Name == nameof(OptimumConfig.EntityShadowCull));
            Assert.Equal("False", entry.Value);
        }
        finally
        {
            OptimumConfig.EntityShadowCull = original;
        }
    }

    [Fact]
    public void WorldgenStatusReportsSuspendedPolicy()
    {
        string source = File.ReadAllText(PatchReader.FindRepositoryFile(
            "sources/VSEssentials/Systems/OptimumStatus.cs"));

        Assert.Contains("Worldgen work stealing: SUSPENDED (serial policy pending R1)", source);
        Assert.DoesNotContain("Worldgen work stealing: ON", source);
    }

    [Fact]
    public void StatusReportsEffectiveThreadPoolLimitsAndOptionalOptimumMeasurement()
    {
        string source = File.ReadAllText(PatchReader.FindRepositoryFile(
            "sources/VSEssentials/Systems/OptimumStatus.cs"));

        Assert.Contains("System.Threading.ThreadPool.GetMaxThreads", source);
        Assert.Contains("workerMax=", source);
        Assert.Contains("ioMax=", source);
        Assert.Contains("GetProperty(\"SetMaxThreadsResult\")", source);
        Assert.Contains("GetProperty(\"SetMaxThreadsWorkerBefore\")", source);
        Assert.Contains("GetProperty(\"SetMaxThreadsWorkerAfter\")", source);
        Assert.Contains("GetProperty(\"SetMaxThreadsIoBefore\")", source);
        Assert.Contains("GetProperty(\"SetMaxThreadsIoAfter\")", source);
        Assert.Contains("api.Logger.Notification(\"[Optimum] threadpool:", source);
    }

    [Fact]
    public void StatusLogsGameLaunchTaskSummaryAtLevelFinalize()
    {
        string source = File.ReadAllText(PatchReader.FindRepositoryFile(
            "sources/VSEssentials/Systems/OptimumStatus.cs"));

        Assert.Contains("api.Event.LevelFinalize += LogGameLaunchTaskSummary;", source);
        Assert.Contains("OptimumDiagnostics.GetGameLaunchTaskSummary()", source);
        Assert.Contains("[Optimum] \" + OptimumDiagnostics.GetGameLaunchTaskSummary()", source);
    }

    [Fact]
    public void ThreadPoolCapUsesProcessorDerivedValues()
    {
        string source = File.ReadAllText(PatchReader.FindRepositoryFile(
            "VintagestoryApi/Common/TyronThreadPool.cs"));

        Assert.Contains("int workerMax = Math.Max(10, Environment.ProcessorCount * 2);", source);
        Assert.Contains("int ioMax = Math.Max(1, Environment.ProcessorCount);", source);
        Assert.Contains("ThreadPool.SetMaxThreads(workerMax, ioMax)", source);
        Assert.DoesNotContain("ThreadPool.SetMaxThreads(10, 1)", source);
    }

    [Fact]
    public void RuntimeThreadPoolCapMatchesProcessorDerivedValues()
    {
        ThreadPool.GetMaxThreads(out int workerBefore, out int ioBefore);
        _ = TyronThreadPool.Inst;

        try
        {
            Assert.Equal(Math.Max(10, Environment.ProcessorCount * 2), TyronThreadPool.SetMaxThreadsWorkerAfter);
            Assert.Equal(Math.Max(1, Environment.ProcessorCount), TyronThreadPool.SetMaxThreadsIoAfter);
            Assert.True(TyronThreadPool.SetMaxThreadsWorkerAfter >= Environment.ProcessorCount);
        }
        finally
        {
            ThreadPool.SetMaxThreads(workerBefore, ioBefore);
        }
    }

    [Fact]
    public void GameLaunchTaskDiagnosticsReportTimingAndQueueDepth()
    {
        OptimumDiagnostics.ResetGameLaunchTasks();
        try
        {
            OptimumDiagnostics.RecordGameLaunchTaskFrame();
            OptimumDiagnostics.RecordGameLaunchTask(Stopwatch.Frequency / 1000, 3);
            string summary = OptimumDiagnostics.GetGameLaunchTaskSummary();

            Assert.Contains("frames=1", summary);
            Assert.Contains("tasks=1", summary);
            Assert.Contains("averageMs=1", summary);
            Assert.Contains("maxMs=1", summary);
            Assert.Contains("peakQueueDepth=3", summary);
        }
        finally
        {
            OptimumDiagnostics.ResetGameLaunchTasks();
        }
    }

    [Fact]
    public void GameLaunchTaskExecutionRecordsTheExistingSingleTaskBranch()
    {
        string clientSource = PatchReader.ReadPatch(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch");
        string diagnosticsSource = File.ReadAllText(PatchReader.FindRepositoryFile(
            "sources/VintagestoryApi/Config/OptimumConfig.cs"));

        Assert.Contains("if (GameLaunchTasks.Count > 0)", clientSource);
        Assert.Contains("ClientTask launchTask = GameLaunchTasks.Dequeue();", clientSource);
        Assert.Contains("Stopwatch.GetTimestamp()", clientSource);
        Assert.Contains("OptimumDiagnostics.RecordGameLaunchTask", clientSource);
        Assert.Contains("finally", clientSource);
        Assert.Contains("GetGameLaunchTaskSummary()", diagnosticsSource);
        Assert.Contains("ResetGameLaunchTasks();", diagnosticsSource);

        string patcherSource = File.ReadAllText(PatchReader.FindRepositoryFile(
            "Optimum.Patcher/Program.cs"));
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"ExecuteMainThreadTasks\", 1", patcherSource);
    }

    [Fact]
    public void StatusReportsShaderReportAdaptiveWorkerAndRadiusStatus()
    {
        string status = Vintagestory.GameContent.OptimumStatusModSystem.BuildStatus();

        Assert.Contains("shader-report:", status);
        Assert.Contains("adaptive-workers:", status);
        Assert.Contains("adaptive-radius:", status);
        Assert.Contains("circuit-breakers:", status);
    }

    [Fact]
    public void AdaptiveWorkerController_TracksPhasesThroughHysteresis()
    {
        var ctrl = new AdaptiveWorkerController(maxWorkers: 4, evalInterval: 10);
        Assert.Equal(AdaptiveWorkerPhase.Cold, ctrl.CurrentPhase);
        Assert.Equal("cold", ctrl.PhaseName);

        // Record chunks with high contention (>40%) and low queue depth -> scales down
        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 50, workTicks: 50);
        }
        ctrl.Evaluate(queueDepth: 5);
        Assert.Equal(AdaptiveWorkerPhase.ScalingDown, ctrl.CurrentPhase);
        Assert.Equal("scaling_down", ctrl.PhaseName);

        // High contention but deep queue -> queue constrained
        for (int i = 0; i < 10; i++)
        {
            ctrl.RecordChunk(lockWaitTicks: 50, workTicks: 50);
        }
        ctrl.Evaluate(queueDepth: 100);
        Assert.Equal(AdaptiveWorkerPhase.QueueConstrained, ctrl.CurrentPhase);
        Assert.Equal("queue_constrained", ctrl.PhaseName);

        // Low contention across intervals until EWMA falls below ScaleUpThreshold (0.15)
        while (ctrl.ContentionRatio > 0.15)
        {
            for (int i = 0; i < 10; i++)
            {
                ctrl.RecordChunk(lockWaitTicks: 1, workTicks: 99);
            }
            ctrl.Evaluate(queueDepth: 20);
        }
        Assert.Equal(AdaptiveWorkerPhase.ScalingUp, ctrl.CurrentPhase);
        Assert.Equal("scaling_up", ctrl.PhaseName);
    }

    [Fact]
    public void AdaptiveRadiusController_TracksPhasesThroughHysteresis()
    {
        var ctrl = new OptimumAdaptiveRadiusController(maxRadius: 12);
        Assert.Equal(AdaptiveRadiusPhase.Cold, ctrl.CurrentPhase);
        Assert.Equal("cold", ctrl.PhaseName);

        // Tick with high queue depth -> throttles radius
        ctrl.Tick(queueDepth: 100, maxRadius: 12);
        ctrl.Tick(queueDepth: 100, maxRadius: 12);
        Assert.Equal(AdaptiveRadiusPhase.Throttling, ctrl.CurrentPhase);
        Assert.Equal("throttling", ctrl.PhaseName);

        // Tick with low queue depth until smoothed queue depth drops below lowThreshold -> recovering
        while (ctrl.SmoothedQueueDepth > OptimumConfig.AdaptiveRadiusLowThreshold)
        {
            ctrl.Tick(queueDepth: 5, maxRadius: 12);
        }
        Assert.Equal(AdaptiveRadiusPhase.Recovering, ctrl.CurrentPhase);
        Assert.Equal("recovering", ctrl.PhaseName);

        // Once fully recovered to maxRadius -> stable
        for (int i = 0; i < 20; i++)
        {
            ctrl.Tick(queueDepth: 5, maxRadius: 12);
        }
        Assert.Equal(AdaptiveRadiusPhase.Stable, ctrl.CurrentPhase);
        Assert.Equal("stable", ctrl.PhaseName);
    }
}
