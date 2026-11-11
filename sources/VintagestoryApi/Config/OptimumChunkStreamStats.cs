using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;

namespace Vintagestory.API.Config;

/// <summary>
/// Fixed-bucket histogram over non-negative values. Four buckets per power of two keep the
/// relative width of a bucket at or below 25 percent. Recording never allocates.
/// </summary>
public sealed class OptimumHistogram
{
    private const int SubBucketBits = 2;
    private const int SubBuckets = 1 << SubBucketBits;
    private const int MaxMostSignificantBit = 47;

    /// <summary>Values at or above this bound land in the last bucket.</summary>
    public const long MaxValue = (1L << (MaxMostSignificantBit + 1)) - 1;

    public const int BucketCount = SubBuckets * MaxMostSignificantBit;

    private readonly long[] buckets = new long[BucketCount];
    private long count;
    private long sum;
    private long max;

    public long Count => Interlocked.Read(ref count);

    public long Max => Interlocked.Read(ref max);

    public double Mean
    {
        get
        {
            long n = Count;
            return n == 0 ? 0.0 : (double)Interlocked.Read(ref sum) / n;
        }
    }

    public static int BucketIndex(long value)
    {
        if (value < SubBuckets)
        {
            return value < 0 ? 0 : (int)value;
        }

        if (value > MaxValue)
        {
            value = MaxValue;
        }

        int msb = 63 - BitOperations.LeadingZeroCount((ulong)value);
        int sub = (int)((value >> (msb - SubBucketBits)) & (SubBuckets - 1));
        return ((msb - 1) << SubBucketBits) + sub;
    }

    /// <summary>Largest value that maps to the bucket.</summary>
    public static long BucketUpperBound(int index)
    {
        if (index < SubBuckets)
        {
            return index;
        }

        int msb = (index >> SubBucketBits) + 1;
        int sub = index & (SubBuckets - 1);
        int shift = msb - SubBucketBits;
        return ((long)(SubBuckets + sub) << shift) + (1L << shift) - 1;
    }

    public void Record(long value)
    {
        if (value < 0)
        {
            value = 0;
        }

        Interlocked.Increment(ref buckets[BucketIndex(value)]);
        Interlocked.Increment(ref count);
        Interlocked.Add(ref sum, value);

        long observed = Volatile.Read(ref max);
        while (value > observed)
        {
            long previous = Interlocked.CompareExchange(ref max, value, observed);
            if (previous == observed)
            {
                break;
            }

            observed = previous;
        }
    }

    /// <summary>
    /// Upper bound of the bucket that holds the requested fraction of samples, clamped to the
    /// largest recorded value. Returns 0 when nothing was recorded.
    /// </summary>
    public long Percentile(double fraction)
    {
        long total = Count;
        if (total == 0)
        {
            return 0;
        }

        long rank = (long)Math.Ceiling(Math.Clamp(fraction, 0.0, 1.0) * total);
        if (rank < 1)
        {
            rank = 1;
        }

        long seen = 0;
        for (int i = 0; i < buckets.Length; i++)
        {
            seen += Volatile.Read(ref buckets[i]);
            if (seen >= rank)
            {
                return Math.Min(BucketUpperBound(i), Max);
            }
        }

        return Max;
    }

    /// <summary>Appends "index:count" pairs for non-empty buckets, so logs can be merged exactly.</summary>
    public void AppendNonZeroBuckets(StringBuilder sb)
    {
        bool first = true;
        for (int i = 0; i < buckets.Length; i++)
        {
            long n = Volatile.Read(ref buckets[i]);
            if (n == 0)
            {
                continue;
            }

            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append(i).Append(':').Append(n);
        }
    }

    public void Reset()
    {
        for (int i = 0; i < buckets.Length; i++)
        {
            Volatile.Write(ref buckets[i], 0);
        }

        Interlocked.Exchange(ref count, 0);
        Interlocked.Exchange(ref sum, 0);
        Interlocked.Exchange(ref max, 0);
    }
}

/// <summary>
/// Chunk streaming distributions and counters for the flight benchmark: how long uploads take
/// per frame and per chunk, how long a finished mesh waits for upload, how many chunks the
/// server sends per call, and how often the pool grows. Recording is off unless
/// <see cref="Enabled"/> is set (OPTIMUM_CHUNK_STREAM_LOG=1), so normal play pays one volatile read.
/// </summary>
public static class OptimumChunkStreamStats
{
    private static readonly double MicrosPerTick = 1_000_000.0 / Stopwatch.Frequency;

    public static volatile bool Enabled;

    /// <summary>Time between frames, as reported to the frame hook.</summary>
    public static readonly OptimumHistogram FrameMicros = new();

    /// <summary>Wall time of one frame's chunk upload pass, for frames that uploaded at least one chunk.</summary>
    public static readonly OptimumHistogram UploadFrameMicros = new();

    /// <summary>Wall time of one AddTesselatedChunk call.</summary>
    public static readonly OptimumHistogram UploadChunkMicros = new();

    /// <summary>Time a finished mesh waited between tessellation and upload.</summary>
    public static readonly OptimumHistogram UploadQueueAgeMicros = new();

    /// <summary>Wall time of one sendAndEnqueueChunks call that sent chunks.</summary>
    public static readonly OptimumHistogram SendCallMicros = new();

    /// <summary>Chunks sent by one sendAndEnqueueChunks call that sent chunks.</summary>
    public static readonly OptimumHistogram ChunksSentPerCall = new();

    private const long Over33Micros = 33_333;
    private const long StutterMicros = 50_000;
    private const long HitchMicros = 100_000;

    private static long chunksSent;
    private static long poolAllocations;
    private static long uploadedChunks;
    private static long tessBaseline;
    private static long framesOver33;
    private static long framesOver50;
    private static long framesOver100;

    public static long ChunksSent => Interlocked.Read(ref chunksSent);

    public static long PoolAllocations => Interlocked.Read(ref poolAllocations);

    public static long UploadedChunks => Interlocked.Read(ref uploadedChunks);

    public static long FramesOver33Ms => Interlocked.Read(ref framesOver33);

    /// <summary>Frames longer than 50 ms, the stutter threshold of the flight benchmark.</summary>
    public static long FramesOver50Ms => Interlocked.Read(ref framesOver50);

    public static long FramesOver100Ms => Interlocked.Read(ref framesOver100);

    /// <summary>Timestamp to pass to a Record call, or 0 when recording is off.</summary>
    public static long Now() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static long TicksToMicros(long ticks) => (long)(ticks * MicrosPerTick);

    /// <summary>Records one frame interval from the render thread.</summary>
    public static void RecordFrame(float deltaSeconds)
    {
        if (!Enabled || deltaSeconds <= 0f)
        {
            return;
        }

        long micros = (long)(deltaSeconds * 1_000_000.0);
        FrameMicros.Record(micros);
        if (micros > Over33Micros)
        {
            Interlocked.Increment(ref framesOver33);
            if (micros > StutterMicros)
            {
                Interlocked.Increment(ref framesOver50);
                if (micros > HitchMicros)
                {
                    Interlocked.Increment(ref framesOver100);
                }
            }
        }
    }

    public static void RecordUploadChunk(long startTicks)
    {
        if (startTicks == 0)
        {
            return;
        }

        Interlocked.Increment(ref uploadedChunks);
        UploadChunkMicros.Record(TicksToMicros(Stopwatch.GetTimestamp() - startTicks));
    }

    public static void RecordUploadFrame(long startTicks, int uploaded)
    {
        if (startTicks == 0 || uploaded <= 0)
        {
            return;
        }

        UploadFrameMicros.Record(TicksToMicros(Stopwatch.GetTimestamp() - startTicks));
    }

    public static void RecordQueueAge(long readyToUploadTicks)
    {
        if (Enabled)
        {
            UploadQueueAgeMicros.Record(TicksToMicros(readyToUploadTicks));
        }
    }

    public static void RecordSend(long startTicks, int sent)
    {
        if (startTicks == 0 || sent <= 0)
        {
            return;
        }

        Interlocked.Add(ref chunksSent, sent);
        ChunksSentPerCall.Record(sent);
        SendCallMicros.Record(TicksToMicros(Stopwatch.GetTimestamp() - startTicks));
    }

    public static void RecordPoolAllocation()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref poolAllocations);
        }
    }

    public static void Reset()
    {
        FrameMicros.Reset();
        UploadFrameMicros.Reset();
        UploadChunkMicros.Reset();
        UploadQueueAgeMicros.Reset();
        SendCallMicros.Reset();
        ChunksSentPerCall.Reset();
        Interlocked.Exchange(ref chunksSent, 0);
        Interlocked.Exchange(ref poolAllocations, 0);
        Interlocked.Exchange(ref uploadedChunks, 0);
        Interlocked.Exchange(ref tessBaseline, OptimumDiagnostics.TessChunksProcessed);
        Interlocked.Exchange(ref framesOver33, 0);
        Interlocked.Exchange(ref framesOver50, 0);
        Interlocked.Exchange(ref framesOver100, 0);
    }

    /// <summary>
    /// One log line for the window since the last <see cref="Reset"/>. Tessellations per sent
    /// chunk above 1 means neighbours are re-tessellated as chunks arrive.
    /// </summary>
    public static string GetSummary()
    {
        long sent = ChunksSent;
        long tessellated = OptimumDiagnostics.TessChunksProcessed - Interlocked.Read(ref tessBaseline);
        double ratio = sent == 0 ? 0.0 : (double)tessellated / sent;
        var sb = new StringBuilder(384);
        sb.Append("Optimum chunk stream: ");
        AppendHistogram(sb, "frameUs", FrameMicros);
        sb.Append("over33ms=").Append(FramesOver33Ms)
            .Append(", over50ms=").Append(FramesOver50Ms)
            .Append(", over100ms=").Append(FramesOver100Ms)
            .Append(", ");
        AppendHistogram(sb, "uploadFrameUs", UploadFrameMicros);
        AppendHistogram(sb, "uploadChunkUs", UploadChunkMicros);
        AppendHistogram(sb, "queueAgeUs", UploadQueueAgeMicros);
        AppendHistogram(sb, "sendCallUs", SendCallMicros);
        AppendHistogram(sb, "sentPerCall", ChunksSentPerCall);
        sb.Append("sent=").Append(sent)
            .Append(", uploaded=").Append(UploadedChunks)
            .Append(", tessellated=").Append(tessellated)
            .Append(", tessPerSent=").Append(ratio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            .Append(", poolAllocs=").Append(PoolAllocations)
            .Append(", uploadBudgetMs=").Append(OptimumUploadBudget.Shared.LastBudgetMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
            .Append(", pacerBudget=").Append(OptimumInflowPacer.Shared.Budget)
            .Append(", pacerPhase=").Append(OptimumInflowPacer.Shared.Phase)
            .Append(", pacerCuts=").Append(OptimumInflowPacer.Shared.Decreases)
            .Append(", gc0/1/2=").Append(GC.CollectionCount(0)).Append('/').Append(GC.CollectionCount(1)).Append('/').Append(GC.CollectionCount(2))
            .Append(", gcPauseMs=").Append(GC.GetTotalPauseDuration().TotalMilliseconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
            .Append(", allocMB=").Append((GC.GetTotalAllocatedBytes() / (1024.0 * 1024.0)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
            .Append(", frameMeanUs=").Append(FrameMicros.Mean.ToString("0", System.Globalization.CultureInfo.InvariantCulture))
            .Append(", frameBuckets=");
        FrameMicros.AppendNonZeroBuckets(sb);
        return sb.ToString();
    }

    /// <summary>Adds the stable numeric keys for the benchmark metrics contract.</summary>
    public static void AddMetrics(IDictionary<string, double> values)
    {
        AddHistogram(values, "stream.frame.micros", FrameMicros);
        values["stream.frames.over33ms.count"] = FramesOver33Ms;
        values["stream.frames.over50ms.count"] = FramesOver50Ms;
        values["stream.frames.over100ms.count"] = FramesOver100Ms;
        AddHistogram(values, "stream.uploadFrame.micros", UploadFrameMicros);
        AddHistogram(values, "stream.uploadChunk.micros", UploadChunkMicros);
        AddHistogram(values, "stream.uploadQueueAge.micros", UploadQueueAgeMicros);
        AddHistogram(values, "stream.sendCall.micros", SendCallMicros);
        AddHistogram(values, "stream.sentPerCall.count", ChunksSentPerCall);
        values["stream.chunksSent.count"] = ChunksSent;
        values["stream.chunksUploaded.count"] = UploadedChunks;
        values["stream.poolAllocations.count"] = PoolAllocations;
        values["stream.uploadBudget.micros"] = OptimumUploadBudget.Shared.LastBudgetMs * 1000.0;
        values["stream.pacer.budget.count"] = OptimumInflowPacer.Shared.Budget;
        values["stream.pacer.decreases.count"] = OptimumInflowPacer.Shared.Decreases;
        values["stream.pacer.increases.count"] = OptimumInflowPacer.Shared.Increases;
        values["gc.collections.gen0.count"] = GC.CollectionCount(0);
        values["gc.collections.gen1.count"] = GC.CollectionCount(1);
        values["gc.collections.gen2.count"] = GC.CollectionCount(2);
        values["gc.pause.timeSpanTicks"] = GC.GetTotalPauseDuration().Ticks;
        values["gc.allocated.bytes"] = GC.GetTotalAllocatedBytes();
    }

    private static void AppendHistogram(StringBuilder sb, string name, OptimumHistogram histogram)
    {
        sb.Append(name).Append("=n").Append(histogram.Count)
            .Append("/p50 ").Append(histogram.Percentile(0.50))
            .Append("/p95 ").Append(histogram.Percentile(0.95))
            .Append("/p99 ").Append(histogram.Percentile(0.99))
            .Append("/max ").Append(histogram.Max)
            .Append(", ");
    }

    private static void AddHistogram(IDictionary<string, double> values, string prefix, OptimumHistogram histogram)
    {
        values[prefix + ".samples.count"] = histogram.Count;
        values[prefix + ".p50"] = histogram.Percentile(0.50);
        values[prefix + ".p95"] = histogram.Percentile(0.95);
        values[prefix + ".p99"] = histogram.Percentile(0.99);
        values[prefix + ".max"] = histogram.Max;
    }
}
