using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Xunit;
using OptimumInterop = Vintagestory.API.Config.VsModInterop;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class ChunkStreamStatsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void SmallValuesGetTheirOwnBucket(long value)
    {
        int index = OptimumHistogram.BucketIndex(value);
        Assert.Equal((int)value, index);
        Assert.Equal(value, OptimumHistogram.BucketUpperBound(index));
    }

    [Fact]
    public void BucketsAreMonotonicAndTightAroundEveryValue()
    {
        int previous = -1;
        for (long value = 0; value < 70_000; value++)
        {
            int index = OptimumHistogram.BucketIndex(value);
            Assert.True(index >= previous, $"index went backwards at {value}");
            previous = index;

            long upper = OptimumHistogram.BucketUpperBound(index);
            Assert.True(upper >= value, $"upper bound {upper} below {value}");
            Assert.True(upper - value <= value / 4 + 1, $"bucket for {value} is wider than a quarter");
        }
    }

    [Fact]
    public void HugeValuesLandInTheLastBucket()
    {
        Assert.Equal(OptimumHistogram.BucketCount - 1, OptimumHistogram.BucketIndex(long.MaxValue));
        Assert.Equal(OptimumHistogram.BucketCount - 1, OptimumHistogram.BucketIndex(OptimumHistogram.MaxValue));
        Assert.Equal(0, OptimumHistogram.BucketIndex(-5));
    }

    [Fact]
    public void PercentilesStayInsideTheRecordedRange()
    {
        var histogram = new OptimumHistogram();
        for (int value = 1; value <= 100; value++)
        {
            histogram.Record(value);
        }

        Assert.Equal(100, histogram.Count);
        Assert.Equal(100, histogram.Max);
        Assert.Equal(50.5, histogram.Mean, 6);
        Assert.InRange(histogram.Percentile(0.50), 50, 63);
        Assert.InRange(histogram.Percentile(0.95), 95, 100);
        Assert.InRange(histogram.Percentile(0.99), 99, 100);
        Assert.Equal(100, histogram.Percentile(1.0));
    }

    [Fact]
    public void EmptyHistogramReportsZeroAndResetClears()
    {
        var histogram = new OptimumHistogram();
        Assert.Equal(0, histogram.Percentile(0.99));
        Assert.Equal(0, histogram.Max);
        Assert.Equal(0.0, histogram.Mean);

        histogram.Record(900);
        histogram.Reset();
        Assert.Equal(0, histogram.Count);
        Assert.Equal(0, histogram.Percentile(0.5));
    }

    [Fact]
    public void NonZeroBucketsRoundTripThroughTheLogFormat()
    {
        var histogram = new OptimumHistogram();
        histogram.Record(2);
        histogram.Record(2);
        histogram.Record(1000);

        var sb = new System.Text.StringBuilder();
        histogram.AppendNonZeroBuckets(sb);

        string expected = $"2:2,{OptimumHistogram.BucketIndex(1000)}:1";
        Assert.Equal(expected, sb.ToString());
    }

    [Fact]
    public void RecordingDoesNotAllocate()
    {
        var histogram = new OptimumHistogram();
        histogram.Record(10);

        // The runtime can allocate on this thread while it tiers up the loop, so one clean pass is the proof:
        // an allocation inside Record would show up in every pass.
        long fewest = long.MaxValue;
        for (int pass = 0; pass < 5 && fewest != 0; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
            {
                histogram.Record(i);
            }

            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, fewest);
    }

    [Fact]
    public void RecordingIsOffUnlessEnabled()
    {
        bool wasEnabled = OptimumChunkStreamStats.Enabled;
        try
        {
            OptimumChunkStreamStats.Enabled = false;
            OptimumChunkStreamStats.Reset();

            Assert.Equal(0, OptimumChunkStreamStats.Now());
            OptimumChunkStreamStats.RecordUploadChunk(0);
            OptimumChunkStreamStats.RecordUploadFrame(0, 3);
            OptimumChunkStreamStats.RecordSend(0, 3);
            OptimumChunkStreamStats.RecordQueueAge(5000);
            OptimumChunkStreamStats.RecordPoolAllocation();
            OptimumChunkStreamStats.RecordFrame(0.2f);

            Assert.Equal(0, OptimumChunkStreamStats.FrameMicros.Count);
            Assert.Equal(0, OptimumChunkStreamStats.UploadChunkMicros.Count);
            Assert.Equal(0, OptimumChunkStreamStats.UploadFrameMicros.Count);
            Assert.Equal(0, OptimumChunkStreamStats.SendCallMicros.Count);
            Assert.Equal(0, OptimumChunkStreamStats.UploadQueueAgeMicros.Count);
            Assert.Equal(0, OptimumChunkStreamStats.PoolAllocations);
            Assert.Equal(0, OptimumChunkStreamStats.ChunksSent);
        }
        finally
        {
            OptimumChunkStreamStats.Enabled = wasEnabled;
        }
    }

    [Fact]
    public void EnabledRecordingFeedsEveryDistribution()
    {
        bool wasEnabled = OptimumChunkStreamStats.Enabled;
        try
        {
            OptimumChunkStreamStats.Enabled = true;
            OptimumChunkStreamStats.Reset();

            long start = OptimumChunkStreamStats.Now();
            Assert.NotEqual(0, start);

            OptimumChunkStreamStats.RecordUploadChunk(start);
            OptimumChunkStreamStats.RecordUploadFrame(start, 2);
            OptimumChunkStreamStats.RecordUploadFrame(start, 0);
            OptimumChunkStreamStats.RecordSend(start, 7);
            OptimumChunkStreamStats.RecordSend(start, 0);
            OptimumChunkStreamStats.RecordQueueAge(Stopwatch.Frequency / 1000);
            OptimumChunkStreamStats.RecordPoolAllocation();

            Assert.Equal(1, OptimumChunkStreamStats.UploadChunkMicros.Count);
            Assert.Equal(1, OptimumChunkStreamStats.UploadFrameMicros.Count);
            Assert.Equal(1, OptimumChunkStreamStats.SendCallMicros.Count);
            Assert.Equal(7, OptimumChunkStreamStats.ChunksSentPerCall.Max);
            Assert.Equal(7, OptimumChunkStreamStats.ChunksSent);
            Assert.Equal(1, OptimumChunkStreamStats.UploadedChunks);
            Assert.Equal(1, OptimumChunkStreamStats.PoolAllocations);
            Assert.InRange(OptimumChunkStreamStats.UploadQueueAgeMicros.Max, 900, 1100);

            OptimumChunkStreamStats.RecordFrame(0.016f);
            OptimumChunkStreamStats.RecordFrame(0.040f);
            OptimumChunkStreamStats.RecordFrame(0.060f);
            OptimumChunkStreamStats.RecordFrame(0.150f);
            OptimumChunkStreamStats.RecordFrame(0f);
            Assert.Equal(4, OptimumChunkStreamStats.FrameMicros.Count);
            Assert.Equal(3, OptimumChunkStreamStats.FramesOver33Ms);
            Assert.Equal(2, OptimumChunkStreamStats.FramesOver50Ms);
            Assert.Equal(1, OptimumChunkStreamStats.FramesOver100Ms);

            string summary = OptimumChunkStreamStats.GetSummary();
            Assert.Contains("frameUs=n4", summary);
            Assert.Contains("over50ms=2", summary);
            Assert.Contains("uploadFrameUs=n1", summary);
            Assert.Contains("sent=7", summary);
            Assert.Contains("poolAllocs=1", summary);
            Assert.Contains("gc0/1/2=", summary);

            OptimumChunkStreamStats.Reset();
            Assert.Equal(0, OptimumChunkStreamStats.ChunksSent);
            Assert.Equal(0, OptimumChunkStreamStats.UploadFrameMicros.Count);
            Assert.Equal(0, OptimumChunkStreamStats.FrameMicros.Count);
            Assert.Equal(0, OptimumChunkStreamStats.FramesOver50Ms);
        }
        finally
        {
            OptimumChunkStreamStats.Reset();
            OptimumChunkStreamStats.Enabled = wasEnabled;
        }
    }

    [Fact]
    public void BenchmarkMetricsCarryTheStreamKeys()
    {
        var metrics = OptimumDiagnostics.CaptureBenchmarkMetrics();

        foreach (string key in new[]
        {
            "stream.frame.micros.p99",
            "stream.frames.over50ms.count",
            "stream.uploadFrame.micros.p99",
            "stream.uploadFrame.micros.samples.count",
            "stream.uploadChunk.micros.max",
            "stream.uploadQueueAge.micros.p95",
            "stream.inFrustumQueueAge.micros.samples.count",
            "stream.outOfFrustumQueueAge.micros.p95",
            "stream.frameAfterUpload.micros.p99",
            "stream.frameAfterNoUpload.micros.p99",
            "stream.uploadVisibility.attempted.count",
            "stream.uploadVisibility.below250ms.count",
            "stream.uploadVisibility.classificationFailures.count",
            "stream.uploadVisibility.capture.micros.p95",
            "stream.uploadVisibility.classify.micros.p95",
            "stream.uploadVisibility.record.micros.p95",
            "stream.sendCall.micros.p50",
            "stream.sentPerCall.count.p99",
            "stream.chunksSent.count",
            "stream.poolAllocations.count",
            "gc.collections.gen0.count",
            "gc.pause.timeSpanTicks",
            "gc.allocated.bytes",
        })
        {
            Assert.Contains(key, metrics.Keys);
        }

        Assert.All(metrics.Values, value => Assert.True(double.IsFinite(value)));
    }

    [Fact]
    public void VisibilityAgesUseExactQuarterSecondBoundaryAndSeparateFrustumBuckets()
    {
        bool wasEnabled = OptimumChunkStreamStats.Enabled;
        try
        {
            OptimumChunkStreamStats.Enabled = true;
            OptimumChunkStreamStats.Reset();
            long thresholdTicks = (Stopwatch.Frequency + 3L) / 4L;
            long[] ages = { thresholdTicks - 1, thresholdTicks, thresholdTicks + 1 };
            byte[] classifications = { 0, 0, 1 };

            OptimumChunkStreamStats.RecordUploadVisibilityFrame(
                4, 3, 1, ages, classifications, Stopwatch.Frequency / 10, Stopwatch.Frequency / 1000, false,
                frameDeltaSeconds: 0.015625f, hasPreviousFrame: true, previousFrameUploaded: true);

            Assert.Equal(2, OptimumChunkStreamStats.InFrustumQueueAgeMicros.Count);
            Assert.Equal(1, OptimumChunkStreamStats.OutOfFrustumQueueAgeMicros.Count);
            Assert.Equal(1, OptimumChunkStreamStats.VisibilityClassifyMicros.Count);
            Assert.Equal(1, OptimumChunkStreamStats.VisibilityRecordMicros.Count);
            Assert.Equal(1, OptimumChunkStreamStats.FrameMicros.Count);
            Assert.Equal(1, OptimumChunkStreamStats.FrameAfterUploadMicros.Count);
            string summary = OptimumChunkStreamStats.GetSummary();
            Assert.Contains("visibilityAttempted=4, visibilityCaptured=3", summary);
            Assert.Contains("visibilityInside=2, visibilityOutside=1", summary);
            Assert.Contains("visibilityCapacityDropped=1", summary);
            Assert.Contains("visibilityBelow250Ms=1", summary);
            Assert.Contains("visibilityPending=0", summary);
            Assert.Contains("visibilityRecordUs=n1", summary);
            Assert.Contains("frameAfterUploadUs=n1", summary);
        }
        finally
        {
            OptimumChunkStreamStats.Reset();
            OptimumChunkStreamStats.Enabled = wasEnabled;
        }
    }

    [Fact]
    public void FrameIntervalsUsePreviousFramesUploadStateAndSkipUnpairedInterval()
    {
        bool wasEnabled = OptimumChunkStreamStats.Enabled;
        try
        {
            OptimumChunkStreamStats.Enabled = true;
            OptimumChunkStreamStats.Reset();

            OptimumChunkStreamStats.RecordFrameInterval(0.016f, hasPreviousFrame: false, previousFrameUploaded: false);
            OptimumChunkStreamStats.RecordFrameInterval(0.015625f, hasPreviousFrame: true, previousFrameUploaded: true);
            OptimumChunkStreamStats.RecordFrameInterval(0.03125f, hasPreviousFrame: true, previousFrameUploaded: false);

            Assert.Equal(3, OptimumChunkStreamStats.FrameMicros.Count);
            Assert.Equal(1, OptimumChunkStreamStats.FrameAfterUploadMicros.Count);
            Assert.Equal(1, OptimumChunkStreamStats.FrameAfterNoUploadMicros.Count);
            Assert.Equal(15_625, OptimumChunkStreamStats.FrameAfterUploadMicros.Max);
            Assert.Equal(31_250, OptimumChunkStreamStats.FrameAfterNoUploadMicros.Max);
        }
        finally
        {
            OptimumChunkStreamStats.Reset();
            OptimumChunkStreamStats.Enabled = wasEnabled;
        }
    }

    [Fact]
    public async Task VisibilitySummaryAndResetDoNotSplitConcurrentFrameBatches()
    {
        bool wasEnabled = OptimumChunkStreamStats.Enabled;
        try
        {
            OptimumChunkStreamStats.Enabled = true;
            OptimumChunkStreamStats.Reset();
            const int batches = 1000;
            long[] ages = { Stopwatch.Frequency / 10 };
            byte[] classifications = { 0 };
            long summarizedAttempts = 0;

            Task writer = Task.Run(() => Parallel.For(0, batches, _ =>
            {
                OptimumChunkStreamStats.RecordUploadVisibilityFrame(1, 1, 0, ages, classifications, 1, Stopwatch.GetTimestamp(), false);
            }));

            while (!writer.IsCompleted)
            {
                string window = OptimumChunkStreamStats.GetSummaryAndReset();
                summarizedAttempts += ParseSummaryCount(window, "visibilityAttempted");
                Assert.Equal(ParseSummaryCount(window, "visibilityCaptured"), ParseSummaryCount(window, "visibilityInside") + ParseSummaryCount(window, "visibilityOutside") + ParseSummaryCount(window, "visibilityUnclassified") + ParseSummaryCount(window, "visibilityPending"));
            }

            await writer;
            summarizedAttempts += ParseSummaryCount(OptimumChunkStreamStats.GetSummaryAndReset(), "visibilityAttempted");
            Assert.Equal(batches, summarizedAttempts);
        }
        finally
        {
            OptimumChunkStreamStats.Reset();
            OptimumChunkStreamStats.Enabled = wasEnabled;
        }
    }

    [Fact]
    public void InvalidVisibilityBatchIsReportedInsteadOfBreakingTheClient()
    {
        bool wasEnabled = OptimumChunkStreamStats.Enabled;
        try
        {
            OptimumChunkStreamStats.Enabled = true;
            OptimumChunkStreamStats.Reset();
            long[] ages = { 10 };
            byte[] classifications = { 0 };

            OptimumChunkStreamStats.RecordUploadVisibilityFrame(2, 1, 0, ages, classifications, 0, 0, false);

            Assert.Contains("visibilityContractFailures=1", OptimumChunkStreamStats.GetSummary());
        }
        finally
        {
            OptimumChunkStreamStats.Reset();
            OptimumChunkStreamStats.Enabled = wasEnabled;
        }
    }

    [Fact]
    public void VisibilityBatchRecordingDoesNotAllocateAfterStorageIsPrepared()
    {
        bool wasEnabled = OptimumChunkStreamStats.Enabled;
        try
        {
            OptimumChunkStreamStats.Enabled = true;
            OptimumChunkStreamStats.Reset();
            long[] ages = { Stopwatch.Frequency / 10 };
            byte[] classifications = { 0 };
            OptimumChunkStreamStats.RecordUploadVisibilityFrame(1, 1, 0, ages, classifications, 10, Stopwatch.GetTimestamp(), false);
            OptimumChunkStreamStats.Reset();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                OptimumChunkStreamStats.RecordUploadVisibilityFrame(1, 1, 0, ages, classifications, 10, Stopwatch.GetTimestamp(), false);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            OptimumChunkStreamStats.Reset();
            OptimumChunkStreamStats.Enabled = wasEnabled;
        }
    }

    private static long ParseSummaryCount(string summary, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(summary, $@"(?:^|[ ,]){name}=([0-9]+)");
        Assert.True(match.Success, $"Missing {name} from summary: {summary}");
        return long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ConfigureController()
    {
        OptimumConfig.AdaptiveRadiusEnabled = true;
        OptimumConfig.AdaptiveRadiusFloor = 4;
        OptimumConfig.AdaptiveRadiusHighThreshold = 50;
        OptimumConfig.AdaptiveRadiusLowThreshold = 10;
        OptimumConfig.KometDetected = false;
        OptimumConfig.KometAdaptiveChunkInflowEnabled = false;
        OptimumInterop.ResetYieldsForTests();
        OptimumConfig.AdaptiveRadiusEffective = int.MaxValue;
    }

    [Fact]
    public void ReleaseActiveClearsTheStaticState()
    {
        ConfigureController();
        var controller = new OptimumAdaptiveRadiusController(12);
        controller.Tick(100, 12);
        Assert.Same(controller, OptimumAdaptiveRadiusController.ActiveInstance);
        Assert.True(OptimumConfig.AdaptiveRadiusEffective < int.MaxValue);

        OptimumAdaptiveRadiusController.ReleaseActive();

        Assert.Null(OptimumAdaptiveRadiusController.ActiveInstance);
        Assert.Equal(int.MaxValue, OptimumConfig.AdaptiveRadiusEffective);
    }

    [Fact]
    public void PhaseTransitionsAreSilentUnlessStatsAreEnabled()
    {
        bool wasEnabled = OptimumChunkStreamStats.Enabled;
        try
        {
            ConfigureController();
            OptimumChunkStreamStats.Enabled = false;
            var controller = new OptimumAdaptiveRadiusController(12);

            controller.Tick(100, 12, 5f);
            controller.Tick(100, 12, 5f);

            Assert.Null(controller.TakeTransitionLog());
        }
        finally
        {
            OptimumChunkStreamStats.Enabled = wasEnabled;
        }
    }

    [Fact]
    public void PhaseTransitionsProduceOneLineAndAreRateLimited()
    {
        bool wasEnabled = OptimumChunkStreamStats.Enabled;
        try
        {
            ConfigureController();
            OptimumChunkStreamStats.Enabled = true;
            var controller = new OptimumAdaptiveRadiusController(12);

            controller.Tick(100, 12, 5f);
            string? line = controller.TakeTransitionLog();

            Assert.NotNull(line);
            Assert.Contains("phase=throttling", line);
            Assert.Contains("effective=11/12", line);
            Assert.Contains("phaseChanges=", line);
            Assert.Null(controller.TakeTransitionLog());

            // The queue drains and the phase leaves throttling, but the rate limit holds the line back.
            for (int i = 0; i < 60; i++)
            {
                controller.Tick(0, 12, 5f);
            }

            Assert.NotEqual("throttling", controller.PhaseName);
            Assert.Null(controller.TakeTransitionLog());
        }
        finally
        {
            OptimumChunkStreamStats.Enabled = wasEnabled;
            OptimumAdaptiveRadiusController.ReleaseActive();
        }
    }
}
