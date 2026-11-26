#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;

namespace Vintagestory.GameContent;

/// <summary>
/// In-process CLR EventListener capturing sampled GC allocation types, GC pause durations,
/// and Monitor lock contentions without external tracing tools. Instantiated only when the
/// profiler is explicitly started and disposed immediately on stop.
/// </summary>
public sealed class OptimumRuntimeEventListener : EventListener
{
    private const string DotNetRuntimeProviderName = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private const EventKeywords ContentionKeyword = (EventKeywords)0x4000;
    private const EventKeywords TypeKeyword = (EventKeywords)0x80000;
    private const int MaxTrackedTypes = 512;

    private sealed class TypeBucket
    {
        public long Bytes;
        public long Ticks;
        public long LohBytes;
    }

    private readonly ConcurrentDictionary<string, TypeBucket> allocByType = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, (int Gen, long StartTicks)> activeGcStarts = new();
    private readonly ConcurrentDictionary<int, long> contentionStartByTid = new();

    private EventSource? clrSource;
    private bool disposed;

    private long sampledAllocBytes;
    private long sohBytes;
    private long lohBytes;
    private long pohBytes;
    private long allocTicks;

    private int gen0Count;
    private int gen1Count;
    private int gen2Count;
    private long gcPauseTotalTicks;
    private long gcPauseMaxTicks;

    private long contentionCount;
    private long contentionTotalNs;
    private long contentionMaxNs;

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (disposed)
        {
            return;
        }

        if (string.Equals(eventSource.Name, DotNetRuntimeProviderName, StringComparison.Ordinal))
        {
            clrSource = eventSource;
            try
            {
                EnableEvents(eventSource, EventLevel.Verbose, GcKeyword | ContentionKeyword | TypeKeyword);
            }
            catch
            {
                // Ignore if runtime event source rejects verbose enable in restricted hosts
            }
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (disposed || eventData.EventName == null)
        {
            return;
        }

        string name = eventData.EventName;
        if (name.StartsWith("GCAllocationTick", StringComparison.Ordinal))
        {
            HandleAllocationTick(eventData);
        }
        else if (name.StartsWith("GCStart", StringComparison.Ordinal))
        {
            HandleGcStart(eventData);
        }
        else if (name.StartsWith("GCStop", StringComparison.Ordinal))
        {
            HandleGcStop(eventData);
        }
        else if (name.StartsWith("ContentionStart", StringComparison.Ordinal))
        {
            contentionStartByTid[Environment.CurrentManagedThreadId] = Stopwatch.GetTimestamp();
        }
        else if (name.StartsWith("ContentionStop", StringComparison.Ordinal))
        {
            HandleContentionStop(eventData);
        }
    }

    public void Reset()
    {
        allocByType.Clear();
        activeGcStarts.Clear();
        contentionStartByTid.Clear();
        Interlocked.Exchange(ref sampledAllocBytes, 0);
        Interlocked.Exchange(ref sohBytes, 0);
        Interlocked.Exchange(ref lohBytes, 0);
        Interlocked.Exchange(ref pohBytes, 0);
        Interlocked.Exchange(ref allocTicks, 0);
        Interlocked.Exchange(ref gen0Count, 0);
        Interlocked.Exchange(ref gen1Count, 0);
        Interlocked.Exchange(ref gen2Count, 0);
        Interlocked.Exchange(ref gcPauseTotalTicks, 0);
        Interlocked.Exchange(ref gcPauseMaxTicks, 0);
        Interlocked.Exchange(ref contentionCount, 0);
        Interlocked.Exchange(ref contentionTotalNs, 0);
        Interlocked.Exchange(ref contentionMaxNs, 0);
    }

    public ProfilerRuntimeSnapshot GetSnapshot(int maxTypes = 20)
    {
        long total = Interlocked.Read(ref sampledAllocBytes);
        var entries = new List<ProfilerAllocTypeEntry>(Math.Min(maxTypes, allocByType.Count));
        foreach (var kv in allocByType
            .OrderByDescending(x => Interlocked.Read(ref x.Value.Bytes))
            .Take(maxTypes))
        {
            long bytes = Interlocked.Read(ref kv.Value.Bytes);
            entries.Add(new ProfilerAllocTypeEntry
            {
                TypeName = kv.Key,
                Bytes = bytes,
                Ticks = Interlocked.Read(ref kv.Value.Ticks),
                SharePct = total > 0 ? bytes * 100.0 / total : 0.0,
                LohBytes = Interlocked.Read(ref kv.Value.LohBytes),
            });
        }

        return new ProfilerRuntimeSnapshot
        {
            SampledAllocBytes = total,
            SohBytes = Interlocked.Read(ref sohBytes),
            LohBytes = Interlocked.Read(ref lohBytes),
            PohBytes = Interlocked.Read(ref pohBytes),
            AllocTicks = Interlocked.Read(ref allocTicks),
            Gen0Collections = Volatile.Read(ref gen0Count),
            Gen1Collections = Volatile.Read(ref gen1Count),
            Gen2Collections = Volatile.Read(ref gen2Count),
            GcPauseTotalMs = Interlocked.Read(ref gcPauseTotalTicks) * TicksToMs,
            GcPauseMaxMs = Interlocked.Read(ref gcPauseMaxTicks) * TicksToMs,
            ContentionCount = Interlocked.Read(ref contentionCount),
            ContentionTotalMs = Interlocked.Read(ref contentionTotalNs) / 1_000_000.0,
            ContentionMaxMs = Interlocked.Read(ref contentionMaxNs) / 1_000_000.0,
            TopTypes = entries,
        };
    }

    private void HandleAllocationTick(EventWrittenEventArgs ev)
    {
        long amount = TryGetInt64(ev, "AllocationAmount64", -1);
        if (amount <= 0)
        {
            amount = TryGetInt64(ev, "AllocationAmount", 0);
        }
        if (amount <= 0)
        {
            return;
        }

        int kind = (int)TryGetInt64(ev, "AllocationKind", 0);
        string typeName = TryGetString(ev, "TypeName");
        if (string.IsNullOrWhiteSpace(typeName))
        {
            typeName = "<unknown>";
        }

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

        if (!allocByType.TryGetValue(typeName, out TypeBucket? bucket))
        {
            if (allocByType.Count >= MaxTrackedTypes)
            {
                typeName = "<other>";
            }
            bucket = allocByType.GetOrAdd(typeName, static _ => new TypeBucket());
        }

        Interlocked.Add(ref bucket.Bytes, amount);
        Interlocked.Increment(ref bucket.Ticks);
        if (kind == 1)
        {
            Interlocked.Add(ref bucket.LohBytes, amount);
        }
    }

    private void HandleGcStart(EventWrittenEventArgs ev)
    {
        int count = (int)TryGetInt64(ev, "Count", -1);
        int depth = (int)TryGetInt64(ev, "Depth", 0);
        if (count >= 0)
        {
            activeGcStarts[count] = (depth, Stopwatch.GetTimestamp());
        }
    }

    private void HandleGcStop(EventWrittenEventArgs ev)
    {
        int count = (int)TryGetInt64(ev, "Count", -1);
        int depth = (int)TryGetInt64(ev, "Depth", -1);
        if (count >= 0 && activeGcStarts.TryRemove(count, out var start))
        {
            int gen = depth >= 0 ? depth : start.Gen;
            if (gen == 0) Interlocked.Increment(ref gen0Count);
            else if (gen == 1) Interlocked.Increment(ref gen1Count);
            else Interlocked.Increment(ref gen2Count);

            long elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - start.StartTicks);
            Interlocked.Add(ref gcPauseTotalTicks, elapsedTicks);
            UpdateMax(ref gcPauseMaxTicks, elapsedTicks);
        }
    }

    private void HandleContentionStop(EventWrittenEventArgs ev)
    {
        long durNs = TryGetInt64(ev, "DurationNs", -1);
        if (durNs < 0 && contentionStartByTid.TryRemove(Environment.CurrentManagedThreadId, out long startTicks))
        {
            long elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - startTicks);
            durNs = (long)(elapsedTicks * (1_000_000_000.0 / Stopwatch.Frequency));
        }
        else
        {
            contentionStartByTid.TryRemove(Environment.CurrentManagedThreadId, out _);
        }

        Interlocked.Increment(ref contentionCount);
        if (durNs > 0)
        {
            Interlocked.Add(ref contentionTotalNs, durNs);
            UpdateMax(ref contentionMaxNs, durNs);
        }
    }

    private static long TryGetInt64(EventWrittenEventArgs ev, string payloadName, long fallback)
    {
        if (ev.PayloadNames == null || ev.Payload == null)
        {
            return fallback;
        }

        int n = Math.Min(ev.PayloadNames.Count, ev.Payload.Count);
        for (int i = 0; i < n; i++)
        {
            if (string.Equals(ev.PayloadNames[i], payloadName, StringComparison.Ordinal))
            {
                object? val = ev.Payload[i];
                if (val == null) return fallback;
                try { return Convert.ToInt64(val); }
                catch { return fallback; }
            }
        }
        return fallback;
    }

    private static string TryGetString(EventWrittenEventArgs ev, string payloadName)
    {
        if (ev.PayloadNames == null || ev.Payload == null)
        {
            return "";
        }

        int n = Math.Min(ev.PayloadNames.Count, ev.Payload.Count);
        for (int i = 0; i < n; i++)
        {
            if (string.Equals(ev.PayloadNames[i], payloadName, StringComparison.Ordinal))
            {
                return ev.Payload[i]?.ToString() ?? "";
            }
        }
        return "";
    }

    private static void UpdateMax(ref long target, long candidate)
    {
        long cur = Volatile.Read(ref target);
        while (candidate > cur)
        {
            long prev = Interlocked.CompareExchange(ref target, candidate, cur);
            if (prev == cur) break;
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
            try { DisableEvents(clrSource); } catch { }
            clrSource = null;
        }
        base.Dispose();
    }
}
