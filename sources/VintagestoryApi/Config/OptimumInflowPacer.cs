using System;
using System.Diagnostics;
using System.Threading;

namespace Vintagestory.API.Config;

/// <summary>
/// Paces how many chunks the integrated server sends to the local client each tick, so a flight
/// that outruns tessellation and upload backs off at the source instead of cutting the view radius.
/// Every chunk inside the view distance is still sent, only later.
///
/// A token bucket refills once per control interval. The budget per interval follows AIMD:
/// it halves when the client reports congestion and grows by a fixed step when the client has room.
/// The client reports upload queue occupancy, the age of uploaded meshes and the frame time.
/// The budget never drops below <see cref="MinBudgetPerInterval"/>, so nothing starves.
/// </summary>
public sealed class OptimumInflowPacer
{
    /// <summary>Control interval in milliseconds. One AIMD decision and one refill per interval.</summary>
    public const int ControlIntervalMs = 50;

    /// <summary>Smallest budget per interval, three columns of eight chunks.</summary>
    public const int MinBudgetPerInterval = 24;

    /// <summary>Chunks added to the budget per interval while the client has room.</summary>
    public const int AdditiveStep = 48;

    /// <summary>Budget multiplier on congestion.</summary>
    public const double DecreaseFactor = 0.5;

    /// <summary>Upload queue fullness at which the client counts as congested.</summary>
    public const double HighOccupancy = 0.75;

    /// <summary>Upload queue fullness below which the client counts as having room.</summary>
    public const double LowOccupancy = 0.40;

    /// <summary>Mean age of recently uploaded meshes, in milliseconds, that counts as congested.</summary>
    public const double HighUploadAgeMs = 400.0;

    /// <summary>Mean upload age below which the client counts as having room.</summary>
    public const double LowUploadAgeMs = 150.0;

    /// <summary>Smoothed frame time, in milliseconds, that counts as congested.</summary>
    public const double HighFrameMs = 50.0;

    /// <summary>Smoothed frame time below which the client counts as having room.</summary>
    public const double LowFrameMs = 30.0;

    private const double SmoothingAlpha = 0.2;
    private const int BurstIntervals = 2;

    public static OptimumInflowPacer Shared { get; } = new();

    private static readonly double MicrosPerTick = 1_000_000.0 / Stopwatch.Frequency;

    private readonly long intervalTicks;
    private int occupancy;
    private int capacity = 1;
    private long frameMicros;
    private long ageMicros;
    private long lastStepTicks;
    private int budget;
    private double tokens;
    private long decreases;
    private long increases;
    private int phaseValue;

    public OptimumInflowPacer()
        : this(Stopwatch.Frequency * ControlIntervalMs / 1000)
    {
    }

    public OptimumInflowPacer(long intervalTicks)
    {
        this.intervalTicks = Math.Max(1, intervalTicks);
    }

    public enum InflowPhase : byte
    {
        Open = 0,
        BackingOff = 1,
        Recovering = 2,
    }

    /// <summary>Chunks the pacer lets through per control interval right now.</summary>
    public int Budget => Volatile.Read(ref budget);

    public InflowPhase Phase => (InflowPhase)Volatile.Read(ref phaseValue);

    public long Decreases => Interlocked.Read(ref decreases);

    public long Increases => Interlocked.Read(ref increases);

    public double SmoothedFrameMs => Interlocked.Read(ref frameMicros) / 1000.0;

    public double SmoothedUploadAgeMs => Interlocked.Read(ref ageMicros) / 1000.0;

    /// <summary>Called by the render thread once per frame with the upload queue state.</summary>
    public void PublishClientSample(int queued, int queueCapacity, float frameMs)
    {
        Volatile.Write(ref occupancy, Math.Max(0, queued));
        Volatile.Write(ref capacity, Math.Max(1, queueCapacity));
        if (frameMs > 0f)
        {
            Smooth(ref frameMicros, (long)(frameMs * 1000.0));
        }
    }

    /// <summary>Called by the render thread when a finished mesh is uploaded.</summary>
    public void RecordUploadAge(long ticks)
    {
        Smooth(ref ageMicros, (long)(ticks * MicrosPerTick));
    }

    /// <summary>
    /// Chunks the server may send in this call, at most <paramref name="vanillaLimit"/>. Runs the
    /// control step when an interval has passed.
    /// </summary>
    public int AcquireSendBudget(long nowTicks, int vanillaLimit)
    {
        if (vanillaLimit <= 0)
        {
            return 0;
        }

        Step(nowTicks, vanillaLimit);
        return (int)Math.Min(vanillaLimit, tokens);
    }

    /// <summary>Takes the chunks actually sent out of the bucket.</summary>
    public void Consume(int sent)
    {
        if (sent > 0)
        {
            tokens = Math.Max(0.0, tokens - sent);
        }
    }

    public void Reset()
    {
        lastStepTicks = 0;
        budget = 0;
        tokens = 0.0;
        Volatile.Write(ref occupancy, 0);
        Volatile.Write(ref capacity, 1);
        Interlocked.Exchange(ref frameMicros, 0);
        Interlocked.Exchange(ref ageMicros, 0);
        Interlocked.Exchange(ref decreases, 0);
        Interlocked.Exchange(ref increases, 0);
        Volatile.Write(ref phaseValue, (int)InflowPhase.Open);
    }

    private void Step(long nowTicks, int vanillaLimit)
    {
        if (lastStepTicks == 0)
        {
            lastStepTicks = nowTicks;
            Volatile.Write(ref budget, vanillaLimit);
            tokens = vanillaLimit;
            return;
        }

        long intervals = (nowTicks - lastStepTicks) / intervalTicks;
        if (intervals < 1)
        {
            return;
        }

        lastStepTicks += intervals * intervalTicks;
        int current = Math.Min(Volatile.Read(ref budget), vanillaLimit);
        double fullness = (double)Volatile.Read(ref occupancy) / Volatile.Read(ref capacity);
        double frameMs = SmoothedFrameMs;
        double ageMs = SmoothedUploadAgeMs;

        bool congested = fullness >= HighOccupancy || ageMs >= HighUploadAgeMs || frameMs >= HighFrameMs;
        bool hasRoom = fullness < LowOccupancy && ageMs < LowUploadAgeMs && frameMs < LowFrameMs;

        if (congested)
        {
            current = Math.Max(MinBudgetPerInterval, (int)(current * DecreaseFactor));
            Interlocked.Increment(ref decreases);
            Volatile.Write(ref phaseValue, (int)InflowPhase.BackingOff);
        }
        else if (hasRoom && current < vanillaLimit)
        {
            current = Math.Min(vanillaLimit, current + AdditiveStep);
            Interlocked.Increment(ref increases);
            Volatile.Write(ref phaseValue, (int)(current >= vanillaLimit ? InflowPhase.Open : InflowPhase.Recovering));
        }
        else if (current >= vanillaLimit)
        {
            Volatile.Write(ref phaseValue, (int)InflowPhase.Open);
        }

        Volatile.Write(ref budget, current);
        tokens = Math.Min(tokens + (double)current * Math.Min(intervals, BurstIntervals), (double)current * BurstIntervals);
    }

    private static void Smooth(ref long target, long sampleMicros)
    {
        long previous = Interlocked.Read(ref target);
        long next = previous == 0 ? sampleMicros : (long)(SmoothingAlpha * sampleMicros + (1.0 - SmoothingAlpha) * previous);
        Interlocked.Exchange(ref target, next);
    }
}
