using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The swapchain's lifetime and recreation rules without a window or a device:
/// a replaced slot dies exactly when its last present submission completed,
/// acquire results map to rebuild decisions, the image count and present mode
/// follow the plan, the acquire-semaphore free list never hands out a semaphore
/// twice, and FIFO_RELAXED promotion needs sustained misses.
/// </summary>
public class SwapchainRetirementTests
{
    private sealed class FakeClock : ITimelineClock
    {
        public ulong FrameRecorded { get; set; }
        public ulong TransferRecorded { get; set; }
        public ulong FrameCompleted { get; set; }
        public ulong TransferCompleted { get; set; }
    }

    private sealed class Slot : IDisposable
    {
        private readonly List<string>? _order;
        public string Name { get; }
        public int DisposeCount { get; private set; }

        public Slot(string name, List<string>? order = null)
        {
            Name = name;
            _order = order;
        }

        public void Dispose()
        {
            DisposeCount++;
            _order?.Add(Name);
        }
    }

    // ------------------------------------------------------------- retirement

    [Fact]
    public void ASlotIsNotDestroyedBeforeItsLastPresentSubmissionCompleted()
    {
        var clock = new FakeClock();
        var retirement = new SwapchainRetirement(clock);
        var slot = new Slot("old");
        retirement.Retire(slot, lastPresentValue: 7);

        for (ulong completed = 0; completed < 7; completed++)
        {
            clock.FrameCompleted = completed;
            Assert.Equal(0, retirement.Collect());
            Assert.Equal(0, slot.DisposeCount);
            Assert.Equal(1, retirement.PendingCount);
        }

        clock.FrameCompleted = 7;
        Assert.Equal(1, retirement.Collect());
        Assert.Equal(1, slot.DisposeCount);
        Assert.Equal(0, retirement.PendingCount);

        clock.FrameCompleted = 50;
        Assert.Equal(0, retirement.Collect());
        Assert.Equal(1, slot.DisposeCount);
    }

    /// <summary>
    /// Phase 1 review regression: a replaced slot is keyed on the frame after its last present
    /// submission. That submission completing only signals the present semaphore; the
    /// vkQueuePresentKHR queued after it may still be pending, and only the next frame's
    /// submission (queued after the present) completing proves it was processed.
    /// </summary>
    [Fact]
    public void AReplacedSlotOutlivesItsLastPresentSubmissionByOneFrame()
    {
        Assert.Equal(0UL, SwapchainPolicy.RetireAfter(0));
        Assert.Equal(8UL, SwapchainPolicy.RetireAfter(7));

        var clock = new FakeClock();
        var retirement = new SwapchainRetirement(clock);
        var slot = new Slot("old");
        retirement.Retire(slot, SwapchainPolicy.RetireAfter(7));

        clock.FrameCompleted = 7;
        Assert.Equal(0, retirement.Collect());
        Assert.Equal(0, slot.DisposeCount);

        clock.FrameCompleted = 8;
        Assert.Equal(1, retirement.Collect());
        Assert.Equal(1, slot.DisposeCount);
    }

    [Fact]
    public void ASlotThatNeverPresentedGoesAtTheNextCollect()
    {
        var retirement = new SwapchainRetirement(new FakeClock());
        var slot = new Slot("unused");
        retirement.Retire(slot, lastPresentValue: 0);
        Assert.Equal(1, retirement.Collect());
        Assert.Equal(1, slot.DisposeCount);
    }

    [Fact]
    public void SlotsRetireIndependentlyAndReadyOnesGoInRetirementOrder()
    {
        var order = new List<string>();
        var clock = new FakeClock();
        var retirement = new SwapchainRetirement(clock);
        var a = new Slot("a", order);
        var b = new Slot("b", order);
        var c = new Slot("c", order);
        retirement.Retire(a, 9);
        retirement.Retire(b, 4);
        retirement.Retire(c, 6);

        clock.FrameCompleted = 6;
        Assert.Equal(2, retirement.Collect());
        Assert.Equal(new[] { "b", "c" }, order);
        Assert.Equal(0, a.DisposeCount);

        clock.FrameCompleted = 9;
        Assert.Equal(1, retirement.Collect());
        Assert.Equal(new[] { "b", "c", "a" }, order);
    }

    [Fact]
    public void TeardownDestroysEverySlotRegardlessOfTheTimeline()
    {
        var retirement = new SwapchainRetirement(new FakeClock());
        var a = new Slot("a");
        var b = new Slot("b");
        retirement.Retire(a, 100);
        retirement.Retire(b, 200);
        retirement.DisposeAll();
        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(1, b.DisposeCount);
        Assert.Equal(0, retirement.PendingCount);
    }

    // -------------------------------------------------------------- acquiring

    [Fact]
    public void AcquireResultsMapToThePlansRebuildRules()
    {
        Assert.Equal(AcquireAction.Present, SwapchainPolicy.OnAcquire(Result.Success, 0));
        Assert.Equal(AcquireAction.PresentThenRebuild, SwapchainPolicy.OnAcquire(Result.SuboptimalKhr, 0));
        Assert.Equal(AcquireAction.PresentThenRebuild, SwapchainPolicy.OnAcquire(Result.SuboptimalKhr, 1));
        // OUT_OF_DATE rebuilds and re-acquires exactly once.
        Assert.Equal(AcquireAction.RebuildAndRetry, SwapchainPolicy.OnAcquire(Result.ErrorOutOfDateKhr, 0));
        Assert.Equal(AcquireAction.SkipFrame, SwapchainPolicy.OnAcquire(Result.ErrorOutOfDateKhr, 1));
        Assert.Equal(AcquireAction.Fail, SwapchainPolicy.OnAcquire(Result.ErrorDeviceLost, 0));
        Assert.Equal(AcquireAction.Fail, SwapchainPolicy.OnAcquire(Result.ErrorSurfaceLostKhr, 0));
    }

    [Fact]
    public void AZeroExtentParksPresentation()
    {
        Assert.True(SwapchainPolicy.IsParked(new Extent2D(0, 0)));
        Assert.True(SwapchainPolicy.IsParked(new Extent2D(800, 0)));
        Assert.True(SwapchainPolicy.IsParked(new Extent2D(0, 600)));
        Assert.False(SwapchainPolicy.IsParked(new Extent2D(1, 1)));
    }

    [Theory]
    [InlineData(1u, 0u, PresentModeKHR.FifoKhr, 2u)]
    [InlineData(2u, 0u, PresentModeKHR.FifoKhr, 3u)]
    [InlineData(1u, 0u, PresentModeKHR.MailboxKhr, 3u)]
    [InlineData(1u, 0u, PresentModeKHR.ImmediateKhr, 2u)]
    [InlineData(3u, 0u, PresentModeKHR.MailboxKhr, 4u)]
    [InlineData(2u, 3u, PresentModeKHR.MailboxKhr, 3u)]
    [InlineData(3u, 3u, PresentModeKHR.FifoKhr, 3u)]
    [InlineData(1u, 2u, PresentModeKHR.MailboxKhr, 2u)]
    public void TheImageCountIsMinPlusOneWithAFloorOfTwoOrThreeForMailboxClamped(
        uint min, uint max, PresentModeKHR mode, uint expected) =>
        Assert.Equal(expected, SwapchainPolicy.ChooseImageCount(min, max, mode));

    [Fact]
    public void PresentModesFollowVsyncAndTheRelaxedPromotion()
    {
        var all = new[] { PresentModeKHR.ImmediateKhr, PresentModeKHR.MailboxKhr, PresentModeKHR.FifoKhr, PresentModeKHR.FifoRelaxedKhr };
        var fifoOnly = new[] { PresentModeKHR.FifoKhr };
        var noMailbox = new[] { PresentModeKHR.FifoKhr, PresentModeKHR.ImmediateKhr };

        Assert.Equal(PresentModeKHR.FifoKhr, SwapchainPolicy.ChoosePresentMode(true, false, all));
        Assert.Equal(PresentModeKHR.FifoRelaxedKhr, SwapchainPolicy.ChoosePresentMode(true, true, all));
        Assert.Equal(PresentModeKHR.FifoKhr, SwapchainPolicy.ChoosePresentMode(true, true, fifoOnly));
        Assert.Equal(PresentModeKHR.MailboxKhr, SwapchainPolicy.ChoosePresentMode(false, false, all));
        Assert.Equal(PresentModeKHR.MailboxKhr, SwapchainPolicy.ChoosePresentMode(false, true, all));
        Assert.Equal(PresentModeKHR.ImmediateKhr, SwapchainPolicy.ChoosePresentMode(false, false, noMailbox));
        Assert.Equal(PresentModeKHR.FifoKhr, SwapchainPolicy.ChoosePresentMode(false, false, fifoOnly));
    }

    // -------------------------------------------------------- acquire semaphores

    [Fact]
    public void TheAcquireFreeListHoldsImageCountPlusOneAndNeverHandsOutASemaphoreTwice()
    {
        const uint imageCount = 3;
        int capacity = AcquireSemaphoreFreeList.CapacityFor(imageCount);
        Assert.Equal(4, capacity);

        var handles = new ulong[capacity];
        for (int i = 0; i < capacity; i++) handles[i] = 100UL + (ulong)i;
        var list = new AcquireSemaphoreFreeList(handles);
        Assert.Equal(capacity, list.FreeCount);

        var taken = new HashSet<ulong>();
        for (int i = 0; i < capacity; i++) Assert.True(taken.Add(list.Take(0)), "a semaphore was handed out twice");
        Assert.Equal(0, list.FreeCount);
        Assert.Throws<InvalidOperationException>(() => list.Take(0));

        // A failed acquire returns its semaphore at once.
        ulong returned = 101;
        list.Return(returned);
        Assert.Equal(returned, list.Take(0));

        foreach (ulong handle in taken) list.Return(handle);
        Assert.Throws<InvalidOperationException>(() => list.Return(999));
        Assert.Throws<InvalidOperationException>(() => list.ReturnAfter(999, 1));
    }

    /// <summary>VUID-vkAcquireNextImageKHR-semaphore-01779: a semaphore an uncompleted submission waits on is not reused.</summary>
    [Fact]
    public void ASemaphoreWaitedOnByAPresentSubmissionIsReusedOnlyAfterThatSubmissionCompleted()
    {
        var list = new AcquireSemaphoreFreeList(new ulong[] { 1, 2, 3 });
        ulong first = list.Take(0);
        ulong second = list.Take(0);
        ulong third = list.Take(0);
        list.ReturnAfter(first, frameValue: 10);
        list.ReturnAfter(second, frameValue: 12);
        Assert.Equal(0, list.FreeCount);
        Assert.Equal(2, list.PendingCount);

        // Frame 9 completed: neither submission is done, nothing to hand out.
        Assert.Throws<InvalidOperationException>(() => list.Take(9));
        Assert.Equal(2, list.PendingCount);

        // Frame 10 completed: only the first comes back.
        Assert.Equal(first, list.Take(10));
        Assert.Equal(1, list.PendingCount);
        Assert.Throws<InvalidOperationException>(() => list.Take(11));

        list.ReturnAfter(third, frameValue: 13);
        list.ReturnAfter(first, frameValue: 14);
        var reclaimed = new HashSet<ulong> { list.Take(13), list.Take(13) };
        Assert.Equal(new HashSet<ulong> { second, third }, reclaimed);
        Assert.Equal(1, list.PendingCount);
    }

    // ---------------------------------------------------- FIFO_RELAXED promotion

    [Fact]
    public void SteadyVsyncedFramesNeverPromote()
    {
        var detector = new MissedVsyncDetector();
        for (int i = 0; i < MissedVsyncDetector.Window * 5; i++)
        {
            Assert.False(detector.NoteInterval(16.7));
        }
    }

    [Fact]
    public void AFewMissesDoNotPromoteButSustainedMissesDo()
    {
        var detector = new MissedVsyncDetector();
        int few = MissedVsyncDetector.MissesToPromote - 1;
        for (int i = 0; i < MissedVsyncDetector.Window * 3; i++)
        {
            // Misses spaced so any full window holds at most `few` of them.
            bool miss = i % (MissedVsyncDetector.Window / few + 1) == 0;
            Assert.False(detector.NoteInterval(miss ? 33.4 : 16.7), "promoted at interval " + i);
        }

        detector.Reset();
        bool promoted = false;
        int at = -1;
        for (int i = 0; i < MissedVsyncDetector.Window && !promoted; i++)
        {
            promoted = detector.NoteInterval(i % 5 == 0 ? 33.4 : 16.7);
            at = i;
        }
        Assert.True(promoted, "24 misses in a window must promote");
        Assert.Equal(MissedVsyncDetector.Window - 1, at);

        // The detector starts over after promoting.
        for (int i = 0; i < MissedVsyncDetector.Window - 1; i++)
        {
            Assert.False(detector.NoteInterval(i % 5 == 0 ? 33.4 : 16.7));
        }
    }

    [Fact]
    public void BurstIntervalsDoNotBecomeTheRefreshEstimate()
    {
        var detector = new MissedVsyncDetector();
        bool promoted = false;
        for (int i = 0; i < MissedVsyncDetector.Window; i++)
        {
            // A 1 ms burst after a stall is not a refresh; against a 1 ms period
            // every 16.7 ms frame would look like a miss.
            promoted |= detector.NoteInterval(i % 10 == 0 ? 1.0 : 16.7);
        }
        Assert.False(promoted);
        Assert.False(detector.NoteInterval(double.NaN));
        Assert.False(detector.NoteInterval(-3));
    }
}
