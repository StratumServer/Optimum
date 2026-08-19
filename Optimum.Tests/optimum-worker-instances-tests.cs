using System;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class OptimumWorkerInstancesTests
{
    [Fact]
    public void SlotsReturnDistinctInstances()
    {
        var pool = new OptimumWorkerInstances<TestWorkerState>(4);
        Assert.Equal(4, pool.SlotCount);

        var a = pool.Get(0);
        var b = pool.Get(1);
        var c = pool.Get(2);
        var d = pool.Get(3);

        Assert.NotSame(a, b);
        Assert.NotSame(b, c);
        Assert.NotSame(c, d);
    }

    [Fact]
    public void SameSlotReturnsSameReference()
    {
        var pool = new OptimumWorkerInstances<TestWorkerState>(3);
        Assert.Same(pool.Get(1), pool.Get(1));
        Assert.Same(pool.Get(0), pool.Get(0));
    }

    [Fact]
    public void MutatingOneSlotDoesNotAffectAnother()
    {
        var pool = new OptimumWorkerInstances<TestWorkerState>(2);

        pool.Get(0).Counter = 42;
        Assert.Equal(0, pool.Get(1).Counter);
    }

    [Fact]
    public void OutOfRangeThrows()
    {
        var pool = new OptimumWorkerInstances<TestWorkerState>(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Get(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Get(2));
    }

    [Fact]
    public void ZeroSlotCountThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OptimumWorkerInstances<TestWorkerState>(0));
    }

    [Fact]
    public void FactoryConstructorBuildsDistinct()
    {
        var pool = new OptimumWorkerInstances<TestWorkerState>(3, i => new TestWorkerState { Counter = i * 10 });
        Assert.Equal(0, pool.Get(0).Counter);
        Assert.Equal(10, pool.Get(1).Counter);
        Assert.Equal(20, pool.Get(2).Counter);
        Assert.NotSame(pool.Get(0), pool.Get(1));
    }

    [Fact]
    public void FactoryReturningNullThrows()
    {
        Assert.Throws<InvalidOperationException>(() => new OptimumWorkerInstances<TestWorkerState>(2, _ => null!));
    }

    internal class TestWorkerState
    {
        public int Counter { get; set; }
    }
}
