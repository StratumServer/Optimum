using Optimum.Render.Vulkan.Core;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The report buffer every CPU-timestamp backend keeps (None, Native, AMD),
/// added by the latency review of 2026-09-12: the backends used a
/// <c>List</c> with <c>RemoveAt(0)</c>, so once the buffer was full - which it is
/// for the whole session whenever nothing drains it, and nothing does unless
/// OPTIMUM_VULKAN_STATS is set - every present shifted the entire array down by
/// one inside the present path. The ring must drop the oldest entry without
/// moving anything and must keep the order the interface promises (oldest
/// first).
/// </summary>
public class LatencyReportBufferTests
{
    private static LatencyFrameReport ReportOf(ulong frameId) =>
        new(frameId, frameId + 100, 1, 2, 3, 4, 0, 0, 0, 10);

    [Fact]
    public void ItKeepsTheNewestReportsInOrderAndDropsTheOldestWhenFull()
    {
        var buffer = new LatencyReportBuffer(4);
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Take());

        for (ulong frame = 1; frame <= 6; frame++) buffer.Add(ReportOf(frame));

        Assert.Equal(4, buffer.Count);
        LatencyFrameReport[] taken = buffer.Take();
        Assert.Equal(new ulong[] { 3, 4, 5, 6 }, System.Array.ConvertAll(taken, r => r.FrameId));

        // Taking clears: the same reports are never handed out twice.
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Take());
    }

    [Fact]
    public void TakingAndAddingAcrossTheWrapKeepsTheOrder()
    {
        var buffer = new LatencyReportBuffer(4);
        for (ulong frame = 1; frame <= 3; frame++) buffer.Add(ReportOf(frame));
        Assert.Equal(new ulong[] { 1, 2, 3 }, System.Array.ConvertAll(buffer.Take(), r => r.FrameId));

        for (ulong frame = 4; frame <= 9; frame++) buffer.Add(ReportOf(frame));
        Assert.Equal(new ulong[] { 6, 7, 8, 9 }, System.Array.ConvertAll(buffer.Take(), r => r.FrameId));
    }

    /// <summary>
    /// The Native backend fills a frame's GPU interval in after the fact, when
    /// the next sleep observes that frame's present submission completed. It must
    /// find the newest report of that frame, and must not guess once the stats
    /// sample has taken it.
    /// </summary>
    [Fact]
    public void TheGpuIntervalIsAmendedOnTheWaitingReportOnly()
    {
        var buffer = new LatencyReportBuffer(4);
        buffer.Add(ReportOf(1));
        buffer.Add(ReportOf(2));

        Assert.True(buffer.AmendGpuUs(1, 4242));
        Assert.False(buffer.AmendGpuUs(99, 1));

        LatencyFrameReport[] taken = buffer.Take();
        Assert.Equal(4242UL, taken[0].GpuUs);
        Assert.Equal(0UL, taken[1].GpuUs);

        // Already drained: the interval stays 0 rather than landing on a later frame.
        Assert.False(buffer.AmendGpuUs(1, 5));
    }
}
