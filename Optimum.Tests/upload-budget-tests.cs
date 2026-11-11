using System.Diagnostics;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class UploadBudgetTests
{
    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    private static double Ms(long ticks) => ticks / TicksPerMs;

    private static OptimumUploadBudget Settled(float frameMs, int frames = 40)
    {
        var budget = new OptimumUploadBudget();
        for (int i = 0; i < frames; i++)
        {
            budget.BeginFrame(frameMs, 0, 128);
        }

        return budget;
    }

    [Fact]
    public void AFastFrameGetsTheMaximumBudget()
    {
        var budget = Settled(6f);
        Assert.Equal(OptimumUploadBudget.MaxBudgetMs, Ms(budget.BeginFrame(6f, 0, 128)), 3);
    }

    [Fact]
    public void ASlowFrameStillGetsTheMinimum()
    {
        var budget = Settled(40f);
        Assert.Equal(OptimumUploadBudget.MinBudgetMs, Ms(budget.BeginFrame(40f, 0, 128)), 3);
    }

    [Fact]
    public void TheBudgetIsTheSlackBelowTheSixtyHertzTarget()
    {
        var budget = Settled(12f);
        double expected = OptimumUploadBudget.TargetFrameMs - 12.0;
        Assert.Equal(expected, Ms(budget.BeginFrame(12f, 0, 128)), 2);
    }

    [Fact]
    public void TimeSpentUploadingIsNotCountedAgainstTheRestOfTheFrame()
    {
        var budget = Settled(16f);
        long oneFrame = (long)(6 * TicksPerMs);
        for (int i = 0; i < 40; i++)
        {
            budget.RecordFrameUpload(oneFrame);
        }

        // 16 ms frames of which 6 ms are uploads leave 10 ms of other work, so about 6.7 ms of slack.
        Assert.Equal(OptimumUploadBudget.TargetFrameMs - 10.0, Ms(budget.BeginFrame(16f, 0, 128)), 1);
    }

    [Fact]
    public void ALongQueueKeepsAFloorEvenOnASlowFrame()
    {
        var budget = Settled(40f);
        double ms = Ms(budget.BeginFrame(40f, 64, 128));
        Assert.Equal(OptimumUploadBudget.BacklogFloorMs, ms, 3);
        Assert.True(ms > OptimumUploadBudget.MinBudgetMs);
    }

    [Fact]
    public void TheFirstChunkOfAFrameAlwaysFits()
    {
        var budget = new OptimumUploadBudget();
        budget.RecordChunk(1000, (long)(50 * TicksPerMs));

        Assert.True(budget.Fits(0, (long)(2 * TicksPerMs), 1_000_000, anyUploaded: false));
    }

    [Fact]
    public void ALaterChunkThatWouldOverrunWaitsForTheNextFrame()
    {
        var budget = new OptimumUploadBudget();
        // 1 ms per 1000 vertices.
        budget.RecordChunk(1000, (long)(1 * TicksPerMs));
        long limit = (long)(4 * TicksPerMs);

        Assert.True(budget.Fits((long)(2 * TicksPerMs), limit, 1500, anyUploaded: true));
        Assert.False(budget.Fits((long)(2 * TicksPerMs), limit, 3000, anyUploaded: true));
    }

    [Fact]
    public void TheCostPerVertexFollowsTheMeasuredUploads()
    {
        var budget = new OptimumUploadBudget();
        for (int i = 0; i < 60; i++)
        {
            budget.RecordChunk(2000, (long)(2 * TicksPerMs));
        }

        Assert.Equal(TicksPerMs / 1000.0, budget.TicksPerVertex, 4);
        Assert.InRange(budget.EstimateTicks(3000), (long)(2.9 * TicksPerMs), (long)(3.1 * TicksPerMs));
        budget.RecordChunk(0, 100);
        budget.RecordChunk(100, 0);
        Assert.Equal(TicksPerMs / 1000.0, budget.TicksPerVertex, 4);
    }

    [Fact]
    public void TheQueueAlwaysDrainsAtLeastOneChunkPerFrame()
    {
        var budget = new OptimumUploadBudget();
        budget.RecordChunk(10, (long)(30 * TicksPerMs)); // a pathologically slow upload
        long limit = budget.BeginFrame(80f, 0, 128);

        int uploaded = 0;
        long elapsed = 0;
        for (int i = 0; i < 10; i++)
        {
            if (!budget.Fits(elapsed, limit, 10, uploaded > 0))
            {
                break;
            }

            uploaded++;
            elapsed += (long)(30 * TicksPerMs);
        }

        Assert.Equal(1, uploaded);
    }

    [Fact]
    public void WithTheDeadlineOffTheLegacyTimeLimitApplies()
    {
        var budget = new OptimumUploadBudget();
        budget.RecordChunk(1000, (long)(1 * TicksPerMs));
        long limit = (long)(4 * TicksPerMs);

        // Legacy: only elapsed time matters, the estimate does not.
        Assert.True(budget.AllowsUpload(false, (long)(3 * TicksPerMs), limit, 50_000, anyUploaded: true));
        Assert.False(budget.AllowsUpload(false, (long)(4 * TicksPerMs), limit, 10, anyUploaded: true));
        Assert.False(budget.AllowsUpload(true, (long)(3 * TicksPerMs), limit, 50_000, anyUploaded: true));
        Assert.True(budget.AllowsUpload(true, (long)(3 * TicksPerMs), limit, 50_000, anyUploaded: false));
    }

    [Fact]
    public void TheSettingIsOnByDefaultAndRoundTrips()
    {
        bool previous = OptimumConfig.DeadlineUploadBudgetEnabled;
        try
        {
            Assert.True(new OptimumConfigData().DeadlineUploadBudget);
        }
        finally
        {
            OptimumConfig.DeadlineUploadBudgetEnabled = previous;
        }
    }

    [Fact]
    public void TheAverageMeshSizeFollowsRecentUploads()
    {
        var budget = new OptimumUploadBudget();
        Assert.Equal(0, budget.AverageVertices);

        for (int i = 0; i < 60; i++)
        {
            budget.RecordChunk(3000, 1000);
        }

        Assert.InRange(budget.AverageVertices, 2990, 3000);
        budget.Reset();
        Assert.Equal(0, budget.AverageVertices);
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var budget = Settled(6f);
        budget.RecordChunk(100, 1000);
        budget.Reset();

        Assert.Equal(0.0, budget.SmoothedFrameMs);
        Assert.Equal(0.0, budget.TicksPerVertex);
        Assert.Equal(0.0, budget.LastBudgetMs);
    }
}
