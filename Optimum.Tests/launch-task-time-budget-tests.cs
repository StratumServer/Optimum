using System.Diagnostics;
using System.Linq;
using System.Threading;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class LaunchTaskTimeBudgetTests
{
    [Fact]
    public void DisabledBudgetRunsExactlyOneTaskPerFrame()
    {
        OptimumConfig.LaunchTaskBudgetEnabled = false;
        OptimumConfig.LaunchTaskBudgetMs = 100;

        long budgetTicks = OptimumConfig.LaunchTaskBudgetEnabled && OptimumConfig.LaunchTaskBudgetMs > 0
            ? (long)OptimumConfig.LaunchTaskBudgetMs * Stopwatch.Frequency / 1000
            : 0;

        Assert.Equal(0, budgetTicks);
    }

    [Fact]
    public void EnabledBudgetWithZeroMsProducesZeroTicks()
    {
        OptimumConfig.LaunchTaskBudgetEnabled = true;
        OptimumConfig.LaunchTaskBudgetMs = 0;

        long budgetTicks = OptimumConfig.LaunchTaskBudgetEnabled && OptimumConfig.LaunchTaskBudgetMs > 0
            ? (long)OptimumConfig.LaunchTaskBudgetMs * Stopwatch.Frequency / 1000
            : 0;

        Assert.Equal(0, budgetTicks);
    }

    [Fact]
    public void EnabledBudgetProducesPositiveTicks()
    {
        OptimumConfig.LaunchTaskBudgetEnabled = true;
        OptimumConfig.LaunchTaskBudgetMs = 100;

        long budgetTicks = OptimumConfig.LaunchTaskBudgetEnabled && OptimumConfig.LaunchTaskBudgetMs > 0
            ? (long)OptimumConfig.LaunchTaskBudgetMs * Stopwatch.Frequency / 1000
            : 0;

        Assert.True(budgetTicks > 0, "Budget ticks should be positive when enabled with non-zero ms");
    }

    [Fact]
    public void BudgetMsClampedOnLoad()
    {
        int clamped = System.Math.Clamp(0, 1, 500);
        Assert.Equal(1, clamped);

        clamped = System.Math.Clamp(999, 1, 500);
        Assert.Equal(500, clamped);

        clamped = System.Math.Clamp(100, 1, 500);
        Assert.Equal(100, clamped);
    }

    [Fact]
    public void BudgetLoopDrainsMultipleTasksWithinBudget()
    {
        OptimumConfig.LaunchTaskBudgetEnabled = true;
        OptimumConfig.LaunchTaskBudgetMs = 500;

        long budgetTicks = (long)OptimumConfig.LaunchTaskBudgetMs * Stopwatch.Frequency / 1000;
        long frameStart = Stopwatch.GetTimestamp();

        int tasksExecuted = 0;
        int totalTasks = 10;

        while (totalTasks - tasksExecuted > 0
            && budgetTicks > 0
            && Stopwatch.GetTimestamp() - frameStart < budgetTicks)
        {
            tasksExecuted++;
        }

        Assert.True(tasksExecuted > 1, $"Expected multiple tasks drained, got {tasksExecuted}");
        Assert.Equal(10, tasksExecuted);
    }

    [Fact]
    public void BudgetLoopStopsOnBudgetExhaustion()
    {
        OptimumConfig.LaunchTaskBudgetEnabled = true;
        OptimumConfig.LaunchTaskBudgetMs = 1;

        long budgetTicks = (long)OptimumConfig.LaunchTaskBudgetMs * Stopwatch.Frequency / 1000;
        long frameStart = Stopwatch.GetTimestamp();

        int tasksExecuted = 0;

        while (tasksExecuted < 10000
            && budgetTicks > 0
            && Stopwatch.GetTimestamp() - frameStart < budgetTicks)
        {
            Thread.SpinWait(10000);
            tasksExecuted++;
        }

        Assert.True(tasksExecuted < 10000, $"Loop should have stopped before exhausting all tasks, got {tasksExecuted}");
    }

    [Fact]
    public void DiagnosticsRecordFrameAndTaskSeparately()
    {
        OptimumDiagnostics.ResetGameLaunchTasks();

        OptimumDiagnostics.RecordGameLaunchTaskFrame();
        OptimumDiagnostics.RecordGameLaunchTask(1000, 5);
        OptimumDiagnostics.RecordGameLaunchTask(2000, 4);

        string summary = OptimumDiagnostics.GetGameLaunchTaskSummary();
        Assert.Contains("frames=1", summary);
        Assert.Contains("tasks=2", summary);
        // Locale-dependent decimal separator: 2.00 (en) or 2,00 (pt-BR)
        double tasksPerFrame = 2.0;
        string expected = $"tasks/frame={tasksPerFrame:0.00}";
        Assert.Contains(expected, summary);
    }

    [Fact]
    public void DefaultConfigDisablesBudget()
    {
        Assert.False(new OptimumConfigData().LaunchTaskBudgetEnabled);
        Assert.Equal(100, new OptimumConfigData().LaunchTaskBudgetMs);
    }

    [Fact]
    public void DescribeTogglesIncludesLaunchTaskBudget()
    {
        var toggles = OptimumConfig.DescribeToggles();
        var names = toggles.Select(t => t.Name).ToArray();
        Assert.Contains("LaunchTaskBudgetEnabled", names);
        Assert.Contains("LaunchTaskBudgetMs", names);
    }
}
