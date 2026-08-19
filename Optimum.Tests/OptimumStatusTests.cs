using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

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
    public void StatusReportsThreadPoolSetMaxThreadsMeasurement()
    {
        string source = File.ReadAllText(PatchReader.FindRepositoryFile(
            "sources/VSEssentials/Systems/OptimumStatus.cs"));

        Assert.Contains("TyronThreadPool.SetMaxThreadsResult", source);
        Assert.Contains("TyronThreadPool.SetMaxThreadsWorkerBefore", source);
        Assert.Contains("TyronThreadPool.SetMaxThreadsWorkerAfter", source);
        Assert.Contains("TyronThreadPool.SetMaxThreadsIoBefore", source);
        Assert.Contains("TyronThreadPool.SetMaxThreadsIoAfter", source);
        Assert.Contains("threadpool: setMaxThreads=", source);
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
        string clientSource = File.ReadAllText(PatchReader.FindRepositoryFile(
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs"));
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
}
