using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ChunkFlightMeter;

/// <summary>
/// Records the time between frames and logs one window every five seconds in the grammar that
/// scripts/analyze-chunk-flight.py reads: frameUs=n/p50/p95/p99/max, over50ms, frameMeanUs and the
/// non-empty histogram buckets. The bucket layout matches OptimumHistogram, four buckets per
/// power of two.
/// </summary>
public sealed class ChunkFlightMeterSystem : ModSystem
{
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        var meter = new FrameMeter(api);
        api.Event.RegisterRenderer(meter, EnumRenderStage.Before, "chunkflightmeter");
        api.Event.RegisterGameTickListener(_ => meter.Flush(), 5000);
        api.Event.LeaveWorld += meter.Flush;
    }
}

internal sealed class FrameMeter : IRenderer
{
    private const int SubBucketBits = 2;
    private const int SubBuckets = 1 << SubBucketBits;
    private const int MaxMostSignificantBit = 47;
    private const long StutterMicros = 50_000;

    private readonly ICoreClientAPI api;
    private readonly long[] buckets = new long[SubBuckets * MaxMostSignificantBit];
    private long lastTicks;
    private long count;
    private long sum;
    private long max;
    private long over33;
    private long over50;
    private long over100;

    public FrameMeter(ICoreClientAPI api)
    {
        this.api = api;
    }

    public double RenderOrder => 0;

    public int RenderRange => 1;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        long now = Stopwatch.GetTimestamp();
        if (lastTicks != 0)
        {
            long micros = (long)((now - lastTicks) * (1_000_000.0 / Stopwatch.Frequency));
            Record(micros);
        }

        lastTicks = now;
    }

    private void Record(long micros)
    {
        lock (buckets)
        {
            buckets[BucketIndex(micros)]++;
            count++;
            sum += micros;
            if (micros > max) max = micros;
            if (micros > 33_333) over33++;
            if (micros > StutterMicros) over50++;
            if (micros > 100_000) over100++;
        }
    }

    public void Flush()
    {
        string line;
        lock (buckets)
        {
            if (count == 0)
            {
                return;
            }

            var sb = new StringBuilder(512);
            sb.Append("ChunkFlightMeter: frameUs=n").Append(count)
                .Append("/p50 ").Append(Percentile(0.50))
                .Append("/p95 ").Append(Percentile(0.95))
                .Append("/p99 ").Append(Percentile(0.99))
                .Append("/max ").Append(max)
                .Append(", over33ms=").Append(over33)
                .Append(", over50ms=").Append(over50)
                .Append(", over100ms=").Append(over100)
                .Append(", frameMeanUs=").Append((sum / count).ToString(CultureInfo.InvariantCulture))
                .Append(", frameBuckets=");
            bool first = true;
            for (int i = 0; i < buckets.Length; i++)
            {
                if (buckets[i] == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(i).Append(':').Append(buckets[i]);
            }

            line = sb.ToString();
            Array.Clear(buckets);
            count = sum = max = over33 = over50 = over100 = 0;
        }

        api.Logger.Notification(line);
    }

    private long Percentile(double fraction)
    {
        long rank = Math.Max(1, (long)Math.Ceiling(fraction * count));
        long seen = 0;
        for (int i = 0; i < buckets.Length; i++)
        {
            seen += buckets[i];
            if (seen >= rank)
            {
                return Math.Min(BucketUpperBound(i), max);
            }
        }

        return max;
    }

    private static int BucketIndex(long value)
    {
        if (value < SubBuckets)
        {
            return value < 0 ? 0 : (int)value;
        }

        int msb = 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)value);
        if (msb > MaxMostSignificantBit)
        {
            return SubBuckets * MaxMostSignificantBit - 1;
        }

        int sub = (int)((value >> (msb - SubBucketBits)) & (SubBuckets - 1));
        return ((msb - 1) << SubBucketBits) + sub;
    }

    private static long BucketUpperBound(int index)
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

    public void Dispose()
    {
    }
}
