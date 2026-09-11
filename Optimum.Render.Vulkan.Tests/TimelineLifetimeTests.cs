using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Optimum.Render.Vulkan.Core;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The deferred-destruction rules of <see cref="RetireQueue" /> against a fake
/// timeline, no device: an entry is never destroyed before both of its recorded
/// timeline values completed, always destroyed at the first collect after, and
/// ready entries go in the order they were retired.
/// </summary>
public class TimelineLifetimeTests
{
    private sealed class FakeClock : ITimelineClock
    {
        public ulong FrameRecorded { get; set; }
        public ulong TransferRecorded { get; set; }
        public ulong FrameCompleted { get; set; }
        public ulong TransferCompleted { get; set; }
    }

    private sealed class Tracked : IDisposable
    {
        private readonly List<int>? _order;
        public int Name { get; }
        public int DisposeCount { get; private set; }
        public Action? OnDispose { get; set; }

        public Tracked(int name, List<int>? order = null)
        {
            Name = name;
            _order = order;
        }

        public bool Disposed => DisposeCount > 0;

        public void Dispose()
        {
            DisposeCount++;
            _order?.Add(Name);
            OnDispose?.Invoke();
        }
    }

    [Fact]
    public void NothingIsFreedBeforeItsFrameValueCompleted()
    {
        var clock = new FakeClock { FrameRecorded = 5 };
        var queue = new RetireQueue(clock);
        var resource = new Tracked(1);
        queue.Retire(resource);

        for (ulong completed = 0; completed < 5; completed++)
        {
            clock.FrameCompleted = completed;
            Assert.Equal(0, queue.Collect());
            Assert.False(resource.Disposed, $"freed with Frame completed at {completed}, recorded at 5");
            Assert.Equal(1, queue.PendingCount);
        }

        clock.FrameCompleted = 5;
        Assert.Equal(1, queue.Collect());
        Assert.True(resource.Disposed);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void BothTimelinesMustPass()
    {
        var clock = new FakeClock { FrameRecorded = 3, TransferRecorded = 7 };
        var queue = new RetireQueue(clock);
        var resource = new Tracked(1);
        queue.Retire(resource);

        clock.FrameCompleted = 100;
        clock.TransferCompleted = 6;
        queue.Collect();
        Assert.False(resource.Disposed, "freed before its Transfer value completed");

        clock.FrameCompleted = 2;
        clock.TransferCompleted = 7;
        queue.Collect();
        Assert.False(resource.Disposed, "freed before its Frame value completed");

        clock.FrameCompleted = 3;
        queue.Collect();
        Assert.True(resource.Disposed);
    }

    [Fact]
    public void TheValuesAreThoseRecordedAtRetirementNotAtCollection()
    {
        var clock = new FakeClock { FrameRecorded = 2 };
        var queue = new RetireQueue(clock);
        var resource = new Tracked(1);
        queue.Retire(resource);

        // Later frames are reserved; they cannot reference a resource released before them.
        clock.FrameRecorded = 9;
        clock.FrameCompleted = 2;
        queue.Collect();
        Assert.True(resource.Disposed);
    }

    [Fact]
    public void EverythingIsFreedExactlyOnceAfterItsValuesPassed()
    {
        var clock = new FakeClock();
        var queue = new RetireQueue(clock);
        var resources = new List<Tracked>();
        for (int i = 0; i < 50; i++)
        {
            clock.FrameRecorded = (ulong)(i / 5);
            clock.TransferRecorded = (ulong)(i / 10);
            var resource = new Tracked(i);
            resources.Add(resource);
            queue.Retire(resource);
        }

        clock.FrameCompleted = 9;
        clock.TransferCompleted = 4;
        Assert.Equal(50, queue.Collect());
        Assert.Equal(0, queue.Collect());
        Assert.Equal(0, queue.PendingCount);
        Assert.All(resources, resource => Assert.Equal(1, resource.DisposeCount));
    }

    [Fact]
    public void ReadyEntriesAreFreedInRetirementOrder()
    {
        var order = new List<int>();
        var clock = new FakeClock();
        var queue = new RetireQueue(clock);

        for (int i = 0; i < 20; i++)
        {
            clock.FrameRecorded = (ulong)(i % 3 + 1);
            queue.Retire(new Tracked(i, order));
        }

        // Partial: only the entries recorded at 1 are ready, in their original order.
        clock.FrameCompleted = 1;
        queue.Collect();
        var expectedFirst = new List<int>();
        for (int i = 0; i < 20; i += 3) expectedFirst.Add(i);
        Assert.Equal(expectedFirst, order);

        // The rest, still in retirement order.
        order.Clear();
        clock.FrameCompleted = 3;
        queue.Collect();
        var expectedRest = new List<int>();
        for (int i = 0; i < 20; i++)
        {
            if (i % 3 != 0) expectedRest.Add(i);
        }
        Assert.Equal(expectedRest, order);
    }

    /// <summary>
    /// A finalizer can retire with an older value after the render thread retired
    /// with a newer one; the old entry must not wait behind the new one.
    /// </summary>
    [Fact]
    public void AnEntryThatHasNotPassedDoesNotHoldBackLaterReadyOnes()
    {
        var clock = new FakeClock { FrameRecorded = 8 };
        var queue = new RetireQueue(clock);
        var late = new Tracked(1);
        queue.Retire(late);
        clock.FrameRecorded = 4;
        var early = new Tracked(2);
        queue.Retire(early);

        clock.FrameCompleted = 4;
        Assert.Equal(1, queue.Collect());
        Assert.True(early.Disposed);
        Assert.False(late.Disposed);
        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public void ADisposeThatRetiresSomethingElseDoesNotDeadlockAndWaitsForTheNextCollect()
    {
        var clock = new FakeClock { FrameRecorded = 1 };
        var queue = new RetireQueue(clock);
        var child = new Tracked(2);
        var parent = new Tracked(1) { OnDispose = () => queue.Retire(child) };
        queue.Retire(parent);

        clock.FrameRecorded = 2;
        clock.FrameCompleted = 1;
        Assert.Equal(1, queue.Collect());
        Assert.True(parent.Disposed);
        Assert.False(child.Disposed);

        clock.FrameCompleted = 2;
        queue.Collect();
        Assert.True(child.Disposed);
    }

    [Fact]
    public void RetirementsFromManyThreadsAreAllKeptAndFreed()
    {
        var clock = new FakeClock { FrameRecorded = 1 };
        var queue = new RetireQueue(clock);
        var resources = new List<Tracked>();
        for (int i = 0; i < 1000; i++) resources.Add(new Tracked(i));

        Parallel.ForEach(resources, resource => queue.Retire(resource));
        Assert.Equal(1000, queue.PendingCount);

        Assert.Equal(0, queue.Collect());
        clock.FrameCompleted = 1;
        Assert.Equal(1000, queue.Collect());
        Assert.All(resources, resource => Assert.Equal(1, resource.DisposeCount));
    }

    [Fact]
    public void DisposeAllFreesEverythingInOrderRegardlessOfTheTimelines()
    {
        var order = new List<int>();
        var clock = new FakeClock { FrameRecorded = 50, TransferRecorded = 50 };
        var queue = new RetireQueue(clock);
        for (int i = 0; i < 5; i++) queue.Retire(new Tracked(i, order));

        queue.DisposeAll();
        Assert.Equal(new List<int> { 0, 1, 2, 3, 4 }, order);
        Assert.Equal(0, queue.PendingCount);
    }

    [Theory]
    [InlineData(1UL, 2, 0UL)]
    [InlineData(2UL, 2, 0UL)]
    [InlineData(3UL, 2, 1UL)]
    [InlineData(100UL, 2, 98UL)]
    [InlineData(4UL, 3, 1UL)]
    public void FrameNWaitsForTheFrameThatLastUsedItsSlot(ulong frame, int framesInFlight, ulong expected) =>
        Assert.Equal(expected, FrameTimeline.PacingTarget(frame, framesInFlight));

    [Theory]
    [InlineData(5UL, 7UL, 5UL)]
    [InlineData(9UL, 7UL, 7UL)]
    [InlineData(0UL, 0UL, 0UL)]
    public void AWaitNeverTargetsAValueNoSubmissionSignalled(ulong requested, ulong signalled, ulong expected) =>
        Assert.Equal(expected, FrameTimeline.WaitTarget(requested, signalled));
}
