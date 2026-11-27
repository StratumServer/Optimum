#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;

namespace Vintagestory.GameContent;

/// <summary>
/// In-process CLR EventListener for sampled allocation types (GCAllocationTick, about one event
/// per 100 KB) and Monitor contention durations. GC counts and pauses come from the exact
/// GC.CollectionCount / GC.GetTotalPauseDuration APIs instead: runtime events reach an
/// EventListener in batches on a dispatcher thread, so timing them on arrival is meaningless.
/// </summary>
public sealed class OptimumRuntimeEventListener : EventListener
{
    private const string DotNetRuntimeProviderName = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private const EventKeywords ContentionKeyword = (EventKeywords)0x4000;
    private const int MaxTrackedTypes = 512;

    private sealed class TypeBucket
    {
        public long Bytes;
        public long Ticks;
        public long LohBytes;
    }

    private readonly ConcurrentDictionary<string, TypeBucket> allocByType = new(StringComparer.Ordinal);
    private EventSource? clrSource;
    private volatile bool disposed;

    private long sampledAllocBytes;
    private long sohBytes;
    private long lohBytes;
    private long pohBytes;
    private long allocTicks;
    private long contentionCount;
    private long contentionTotalNs;
    private long contentionMaxNs;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (!disposed && eventSource.Name == DotNetRuntimeProviderName)
        {
            clrSource = eventSource;
            EnableEvents(eventSource, EventLevel.Verbose, GcKeyword | ContentionKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (disposed)
        {
            return;
        }
        string? name = eventData.EventName;
        if (name == null)
        {
            return;
        }
        if (name.StartsWith("GCAllocationTick", StringComparison.Ordinal))
        {
            HandleAllocationTick(eventData);
        }
        else if (name.StartsWith("ContentionStop", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref contentionCount);
            long durNs = ReadInt64(eventData, "DurationNs", 0);
            if (durNs > 0)
            {
                Interlocked.Add(ref contentionTotalNs, durNs);
                UpdateMax(ref contentionMaxNs, durNs);
            }
        }
    }

    public void Reset()
    {
        allocByType.Clear();
        Interlocked.Exchange(ref sampledAllocBytes, 0);
        Interlocked.Exchange(ref sohBytes, 0);
        Interlocked.Exchange(ref lohBytes, 0);
        Interlocked.Exchange(ref pohBytes, 0);
        Interlocked.Exchange(ref allocTicks, 0);
        Interlocked.Exchange(ref contentionCount, 0);
        Interlocked.Exchange(ref contentionTotalNs, 0);
        Interlocked.Exchange(ref contentionMaxNs, 0);
    }

    public ProfilerRuntimeSnapshot GetSnapshot(int maxTypes)
    {
        long total = Interlocked.Read(ref sampledAllocBytes);
        List<ProfilerAllocTypeEntry> entries = allocByType
            .Select(kv => (kv.Key, Bucket: kv.Value, Bytes: Interlocked.Read(ref kv.Value.Bytes)))
            .OrderByDescending(x => x.Bytes)
            .Take(maxTypes)
            .Select(x => new ProfilerAllocTypeEntry
            {
                TypeName = x.Key,
                Bytes = x.Bytes,
                Ticks = Interlocked.Read(ref x.Bucket.Ticks),
                SharePct = total > 0 ? x.Bytes * 100.0 / total : 0.0,
                LohBytes = Interlocked.Read(ref x.Bucket.LohBytes),
            })
            .ToList();

        return new ProfilerRuntimeSnapshot
        {
            SampledAllocBytes = total,
            SohBytes = Interlocked.Read(ref sohBytes),
            LohBytes = Interlocked.Read(ref lohBytes),
            PohBytes = Interlocked.Read(ref pohBytes),
            AllocTicks = Interlocked.Read(ref allocTicks),
            ContentionCount = Interlocked.Read(ref contentionCount),
            ContentionTotalMs = Interlocked.Read(ref contentionTotalNs) / 1_000_000.0,
            ContentionMaxMs = Interlocked.Read(ref contentionMaxNs) / 1_000_000.0,
            TopTypes = entries,
        };
    }

    private void HandleAllocationTick(EventWrittenEventArgs ev)
    {
        long amount = ReadInt64(ev, "AllocationAmount64", 0);
        if (amount <= 0)
        {
            amount = ReadInt64(ev, "AllocationAmount", 0);
        }
        if (amount <= 0)
        {
            return;
        }

        // AllocationKind: 0 = small object heap, 1 = large object heap, 2 = pinned object heap.
        long kind = ReadInt64(ev, "AllocationKind", 0);
        Interlocked.Add(ref sampledAllocBytes, amount);
        Interlocked.Increment(ref allocTicks);
        if (kind == 1)
        {
            Interlocked.Add(ref lohBytes, amount);
        }
        else if (kind == 2)
        {
            Interlocked.Add(ref pohBytes, amount);
        }
        else
        {
            Interlocked.Add(ref sohBytes, amount);
        }

        string typeName = ReadString(ev, "TypeName");
        if (typeName.Length == 0)
        {
            typeName = "<unknown>";
        }
        if (!allocByType.TryGetValue(typeName, out TypeBucket? bucket))
        {
            bucket = allocByType.GetOrAdd(allocByType.Count >= MaxTrackedTypes ? "<other>" : typeName, static _ => new TypeBucket());
        }
        Interlocked.Add(ref bucket.Bytes, amount);
        Interlocked.Increment(ref bucket.Ticks);
        if (kind == 1)
        {
            Interlocked.Add(ref bucket.LohBytes, amount);
        }
    }

    private static int IndexOfPayload(EventWrittenEventArgs ev, string payloadName)
    {
        if (ev.PayloadNames == null || ev.Payload == null)
        {
            return -1;
        }
        int n = Math.Min(ev.PayloadNames.Count, ev.Payload.Count);
        for (int i = 0; i < n; i++)
        {
            if (ev.PayloadNames[i] == payloadName)
            {
                return i;
            }
        }
        return -1;
    }

    private static long ReadInt64(EventWrittenEventArgs ev, string payloadName, long fallback)
    {
        int i = IndexOfPayload(ev, payloadName);
        return i >= 0 && ev.Payload![i] is IConvertible value ? value.ToInt64(null) : fallback;
    }

    private static string ReadString(EventWrittenEventArgs ev, string payloadName)
    {
        int i = IndexOfPayload(ev, payloadName);
        return i >= 0 ? ev.Payload![i]?.ToString() ?? "" : "";
    }

    private static void UpdateMax(ref long target, long candidate)
    {
        long cur = Volatile.Read(ref target);
        while (candidate > cur)
        {
            long prev = Interlocked.CompareExchange(ref target, candidate, cur);
            if (prev == cur)
            {
                break;
            }
            cur = prev;
        }
    }

    public override void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (clrSource != null)
        {
            DisableEvents(clrSource);
            clrSource = null;
        }
        base.Dispose();
    }
}
