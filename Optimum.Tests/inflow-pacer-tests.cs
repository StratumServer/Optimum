using System;
using System.Diagnostics;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class InflowPacerTests
{
    private const long Interval = 100;
    private const int Limit = 1536;

    private static OptimumInflowPacer Create() => new(Interval);

    private static void Quiet(OptimumInflowPacer pacer)
    {
        for (int i = 0; i < 30; i++)
        {
            pacer.PublishClientSample(0, 100, 10f);
        }

        pacer.RecordUploadAge(0);
        for (int i = 0; i < 30; i++)
        {
            pacer.RecordUploadAge(0);
        }
    }

    private static void Congest(OptimumInflowPacer pacer) => pacer.PublishClientSample(90, 100, 10f);

    private static long Now(long intervals) => 1_000 + intervals * Interval;

    [Fact]
    public void StartsOpenAtTheVanillaLimit()
    {
        var pacer = Create();
        Quiet(pacer);

        Assert.Equal(Limit, pacer.AcquireSendBudget(Now(0), Limit));
        Assert.Equal(Limit, pacer.Budget);
        Assert.Equal(OptimumInflowPacer.InflowPhase.Open, pacer.Phase);
    }

    [Fact]
    public void CongestionHalvesTheBudgetEachIntervalDownToTheMinimum()
    {
        var pacer = Create();
        Quiet(pacer);
        pacer.AcquireSendBudget(Now(0), Limit);
        Congest(pacer);

        int previous = pacer.Budget;
        for (int i = 1; i <= 12; i++)
        {
            pacer.AcquireSendBudget(Now(i), Limit);
            Assert.True(pacer.Budget <= previous);
            Assert.True(pacer.Budget >= OptimumInflowPacer.MinBudgetPerInterval);
            previous = pacer.Budget;
        }

        Assert.Equal(OptimumInflowPacer.MinBudgetPerInterval, pacer.Budget);
        Assert.Equal(OptimumInflowPacer.InflowPhase.BackingOff, pacer.Phase);
        Assert.True(pacer.Decreases >= 6);
    }

    [Theory]
    [InlineData("occupancy")]
    [InlineData("age")]
    [InlineData("frame")]
    public void EachSignalCanTriggerBackoffOnItsOwn(string signal)
    {
        var pacer = Create();
        Quiet(pacer);
        pacer.AcquireSendBudget(Now(0), Limit);

        switch (signal)
        {
            case "occupancy":
                pacer.PublishClientSample(80, 100, 10f);
                break;
            case "age":
                for (int i = 0; i < 20; i++)
                {
                    pacer.RecordUploadAge(Stopwatch.Frequency); // one second per mesh
                }

                break;
            default:
                for (int i = 0; i < 30; i++)
                {
                    pacer.PublishClientSample(0, 100, 90f);
                }

                break;
        }

        pacer.AcquireSendBudget(Now(1), Limit);
        Assert.Equal(Limit / 2, pacer.Budget);
    }

    [Fact]
    public void BudgetRecoversInSteps()
    {
        var pacer = Create();
        Quiet(pacer);
        pacer.AcquireSendBudget(Now(0), Limit);
        Congest(pacer);
        for (int i = 1; i <= 10; i++)
        {
            pacer.AcquireSendBudget(Now(i), Limit);
        }

        Assert.Equal(OptimumInflowPacer.MinBudgetPerInterval, pacer.Budget);

        Quiet(pacer);
        pacer.AcquireSendBudget(Now(11), Limit);
        Assert.Equal(OptimumInflowPacer.MinBudgetPerInterval + OptimumInflowPacer.AdditiveStep, pacer.Budget);
        Assert.Equal(OptimumInflowPacer.InflowPhase.Recovering, pacer.Phase);

        int intervals = 12;
        while (pacer.Budget < Limit && intervals < 80)
        {
            pacer.AcquireSendBudget(Now(intervals++), Limit);
        }

        Assert.Equal(Limit, pacer.Budget);
        Assert.Equal(OptimumInflowPacer.InflowPhase.Open, pacer.Phase);
        Assert.True(intervals < 60, $"recovery took {intervals} intervals");
    }

    [Fact]
    public void BetweenTheThresholdsTheBudgetHolds()
    {
        var pacer = Create();
        Quiet(pacer);
        pacer.AcquireSendBudget(Now(0), Limit);
        Congest(pacer);
        pacer.AcquireSendBudget(Now(1), Limit);
        int held = pacer.Budget;

        pacer.PublishClientSample(55, 100, 10f);
        for (int i = 2; i < 10; i++)
        {
            pacer.AcquireSendBudget(Now(i), Limit);
            Assert.Equal(held, pacer.Budget);
        }
    }

    [Fact]
    public void ABackedOffClientStillReceivesEveryChunkEventually()
    {
        var pacer = Create();
        Quiet(pacer);
        int remaining = 6000;
        long interval = 0;

        pacer.AcquireSendBudget(Now(interval), Limit);
        Congest(pacer);
        int sentWhileStalled = 0;
        for (; interval < 100; interval++)
        {
            int allowed = pacer.AcquireSendBudget(Now(interval), Limit);
            int send = Math.Min(allowed, remaining);
            remaining -= send;
            sentWhileStalled += send;
            pacer.Consume(send);
        }

        Assert.True(sentWhileStalled > 0, "a stalled client must still make progress");
        Assert.True(remaining > 0);

        Quiet(pacer);
        for (; interval < 400 && remaining > 0; interval++)
        {
            int allowed = pacer.AcquireSendBudget(Now(interval), Limit);
            int send = Math.Min(allowed, remaining);
            remaining -= send;
            pacer.Consume(send);
        }

        Assert.Equal(0, remaining);
    }

    [Fact]
    public void TheTrajectoryDependsOnTimeNotOnHowOftenItIsCalled()
    {
        int Run(int callsPerInterval)
        {
            var pacer = Create();
            Quiet(pacer);
            long step = Interval / callsPerInterval;
            long now = Now(0);
            pacer.AcquireSendBudget(now, Limit);
            Congest(pacer);
            for (int i = 0; i < 8 * callsPerInterval; i++)
            {
                now += step;
                pacer.AcquireSendBudget(now, Limit);
            }

            Quiet(pacer);
            for (int i = 0; i < 6 * callsPerInterval; i++)
            {
                now += step;
                pacer.AcquireSendBudget(now, Limit);
            }

            return pacer.Budget;
        }

        Assert.Equal(Run(1), Run(10));
        Assert.Equal(Run(1), Run(2));
    }

    [Fact]
    public void BudgetNeverExceedsTheVanillaLimitOrDropsToZero()
    {
        var random = new Random(11);
        var pacer = Create();
        long now = Now(0);
        for (int i = 0; i < 20_000; i++)
        {
            now += random.Next(1, 150);
            pacer.PublishClientSample(random.Next(0, 150), random.Next(1, 200), random.Next(1, 120));
            int limit = random.Next(1, 3000);
            int allowed = pacer.AcquireSendBudget(now, limit);
            Assert.InRange(allowed, 0, limit);
            Assert.InRange(pacer.Budget, 1, Math.Max(limit, Limit * 2));
            pacer.Consume(random.Next(0, allowed + 1));
        }
    }

    [Fact]
    public void ResetReturnsToTheInitialState()
    {
        var pacer = Create();
        pacer.AcquireSendBudget(Now(0), Limit);
        Congest(pacer);
        pacer.AcquireSendBudget(Now(1), Limit);
        Assert.True(pacer.Decreases > 0);

        pacer.Reset();

        Assert.Equal(0, pacer.Budget);
        Assert.Equal(0, pacer.Decreases);
        Assert.Equal(OptimumInflowPacer.InflowPhase.Open, pacer.Phase);
        Assert.Equal(Limit, pacer.AcquireSendBudget(Now(2), Limit));
    }

    [Fact]
    public void LeavingTheWorldResetsTheSharedPacer()
    {
        OptimumInflowPacer.Shared.AcquireSendBudget(Stopwatch.GetTimestamp(), Limit);
        Assert.True(OptimumInflowPacer.Shared.Budget > 0);

        OptimumAdaptiveRadiusController.ReleaseActive();

        Assert.Equal(0, OptimumInflowPacer.Shared.Budget);
    }
}
