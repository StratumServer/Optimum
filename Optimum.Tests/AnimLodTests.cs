using System.Collections.Generic;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class AnimLodTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-9)]
    [InlineData(13)]
    public void PhaseCoversAllFourValuesAcrossFourConsecutivePositionsOnEachAxis(int baseCoord)
    {
        var xPhases = new HashSet<int>();
        var yPhases = new HashSet<int>();
        var zPhases = new HashSet<int>();

        for (int i = 0; i < 4; i++)
        {
            xPhases.Add(OptimumAnimLod.Phase(baseCoord + i, 0, 0));
            yPhases.Add(OptimumAnimLod.Phase(0, baseCoord + i, 0));
            zPhases.Add(OptimumAnimLod.Phase(0, 0, baseCoord + i));
        }

        Assert.Equal(4, xPhases.Count);
        Assert.Equal(4, yPhases.Count);
        Assert.Equal(4, zPhases.Count);
    }

    [Fact]
    public void PhaseIsAlwaysInRangeZeroToThree()
    {
        for (int x = -20; x <= 20; x++)
        {
            int phase = OptimumAnimLod.Phase(x, -x * 2, x + 7);
            Assert.InRange(phase, 0, 3);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MidTierDueFiresExactlyOncePerFourConsecutiveCounters(int phase)
    {
        for (int windowStart = -8; windowStart < 16; windowStart += 4)
        {
            int dueCount = 0;
            for (int frameCounter = windowStart; frameCounter < windowStart + 4; frameCounter++)
            {
                if (OptimumAnimLod.MidTierDue(frameCounter, phase)) dueCount++;
            }
            Assert.Equal(1, dueCount);
        }
    }

    [Fact]
    public void BudgetWindowConsumesBudgetWithinSameStamp()
    {
        long stamp = -1;
        int used = 0;

        Assert.True(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, 2));
        Assert.True(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, 2));
        Assert.False(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, 2));
    }

    [Fact]
    public void BudgetWindowResetsOnNewStamp()
    {
        long stamp = -1;
        int used = 0;

        Assert.True(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, 1));
        Assert.False(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, 1));
        Assert.True(OptimumAnimLod.BudgetWindow(101, ref stamp, ref used, 1));
        Assert.Equal(101, stamp);
    }

    [Fact]
    public void BudgetWindowZeroAlwaysRuns()
    {
        long stamp = -1;
        int used = 0;

        for (int i = 0; i < 5; i++)
        {
            Assert.True(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, 0));
        }
    }

    [Fact]
    public void BudgetWindowNegativeAlwaysRuns()
    {
        long stamp = -1;
        int used = 0;

        Assert.True(OptimumAnimLod.BudgetWindow(100, ref stamp, ref used, -1));
    }
}
