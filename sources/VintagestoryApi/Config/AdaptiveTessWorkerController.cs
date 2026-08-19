using System;
using System.Diagnostics;
using System.Threading;

namespace Vintagestory.API.Config;

/// <summary>
/// Adaptive tessellation worker cap controller. Adjusts active tessellation
/// worker count based on measured upload-queue backpressure and throughput.
///
/// Unlike the worldgen AdaptiveWorkerController which measures lock contention,
/// this controller measures the ratio of time workers spend blocked on the
/// upload handoff (backpressure) vs time spent tessellating (productive work).
/// When the render thread cannot drain uploads fast enough, adding more workers
/// wastes CPU spinning on the bounded handoff.
///
/// Anti-thrashing design (same pattern as worldgen controller):
///   - Asymmetric hysteresis: scale-down at >40% backpressure, scale-up at <10%
///   - Cooldown via eval interval (configurable, default 30 chunks)
///   - EWMA smoothing (alpha 0.3)
///   - Queue depth starvation guard: no scale-down while queue keeps growing
///   - Respects OptimumConfig.TesselationWorkerCap hard ceiling
///   - Respects OptimumTesselationSafetyGate (foreign ITexPositionSource forces cap=1)
/// </summary>
public sealed class AdaptiveTessWorkerController
{
    private const double ScaleDownThreshold = 0.40;
    private const double ScaleUpThreshold = 0.10;
    private const double EwmaAlpha = 0.3;
    private const int QueuePressureFloor = 4;

    private int _maxWorkers;
    private readonly int _evalInterval;

    private int _activeWorkerCap;
    private double _backpressureEwma;
    private long _chunksSinceLastEval;
    private bool _warmedUp;

    private long _backpressureTicks;
    private long _workTicks;
    private long _chunksProcessed;

    /// <summary>
    /// Fires when Evaluate() changes the active cap. Arguments: oldCap, newCap, backpressureEwma.
    /// </summary>
    public Action<int, int, double>? OnCapChanged;

    /// <summary>
    /// Current active worker cap. Workers with index >= this value yield.
    /// </summary>
    public int ActiveWorkerCap => Volatile.Read(ref _activeWorkerCap);

    /// <summary>
    /// The EWMA backpressure ratio from the last evaluation (for diagnostics).
    /// </summary>
    public double BackpressureRatio => _backpressureEwma;

    /// <summary>
    /// Total chunks tessellated since controller creation (for diagnostics).
    /// </summary>
    public long TotalChunksProcessed => Volatile.Read(ref _chunksProcessed);

    /// <param name="maxWorkers">
    /// Hard ceiling from OptimumConfig.TesselationWorkerCap (after safety gate).
    /// The controller never exceeds this. 0 means tessellation is disabled.
    /// </param>
    /// <param name="evalInterval">
    /// Chunks between evaluations. Default 30 (tessellation produces fewer
    /// chunks per second than worldgen, so a smaller interval reacts faster).
    /// </param>
    public AdaptiveTessWorkerController(int maxWorkers, int evalInterval = 30)
    {
        _maxWorkers = Math.Max(0, maxWorkers);
        _evalInterval = Math.Max(5, evalInterval);
        _activeWorkerCap = _maxWorkers;
        _backpressureEwma = 0.0;
        _warmedUp = false;
    }

    /// <summary>
    /// Record a completed chunk tessellation with timing breakdown.
    /// Called by each tessellation worker after processing a chunk.
    /// </summary>
    /// <param name="backpressureTicks">Ticks spent waiting on bounded handoff (TryReserve spin/block)</param>
    /// <param name="workTicks">Ticks spent tessellating the chunk (productive work)</param>
    public void RecordChunk(long backpressureTicks, long workTicks)
    {
        Interlocked.Add(ref _backpressureTicks, backpressureTicks);
        Interlocked.Add(ref _workTicks, workTicks);
        Interlocked.Increment(ref _chunksProcessed);
        Interlocked.Increment(ref _chunksSinceLastEval);
    }

    /// <summary>
    /// Check whether an evaluation is due.
    /// </summary>
    public bool ShouldEvaluate()
    {
        return Volatile.Read(ref _chunksSinceLastEval) >= _evalInterval;
    }

    /// <summary>
    /// Run the adaptive evaluation. Returns the new active worker cap.
    /// </summary>
    /// <param name="queueDepth">Current pending tessellation queue depth</param>
    public int Evaluate(int queueDepth)
    {
        long bp = Interlocked.Exchange(ref _backpressureTicks, 0);
        long work = Interlocked.Exchange(ref _workTicks, 0);
        Interlocked.Exchange(ref _chunksSinceLastEval, 0);

        long total = bp + work;
        double instantRatio = total > 0 ? (double)bp / total : 0.0;

        if (!_warmedUp)
        {
            _backpressureEwma = instantRatio;
            _warmedUp = true;
        }
        else
        {
            _backpressureEwma = EwmaAlpha * instantRatio + (1.0 - EwmaAlpha) * _backpressureEwma;
        }

        int current = Volatile.Read(ref _activeWorkerCap);
        int next = current;

        if (_backpressureEwma > ScaleDownThreshold && current > 1)
        {
            if (queueDepth < QueuePressureFloor * current)
            {
                next = current - 1;
            }
        }
        else if (_backpressureEwma < ScaleUpThreshold && current < _maxWorkers)
        {
            if (queueDepth > QueuePressureFloor)
            {
                next = current + 1;
            }
        }

        Volatile.Write(ref _activeWorkerCap, next);
        if (next != current)
        {
            try { OnCapChanged?.Invoke(current, next, _backpressureEwma); } catch { }
        }
        return next;
    }

    /// <summary>
    /// Update the max worker ceiling at runtime (e.g. when safety gate fires).
    /// Clamps current cap to the new ceiling.
    /// </summary>
    public void SetMaxWorkers(int maxWorkers)
    {
        _maxWorkers = Math.Max(0, maxWorkers);
        int current = Volatile.Read(ref _activeWorkerCap);
        if (current > _maxWorkers)
        {
            Volatile.Write(ref _activeWorkerCap, _maxWorkers);
        }
    }

    /// <summary>
    /// Force the cap to a specific value. Used by tests.
    /// </summary>
    internal void ForceCapForTesting(int cap)
    {
        int next = _maxWorkers == 0 ? 0 : Math.Clamp(cap, 1, _maxWorkers);
        Volatile.Write(ref _activeWorkerCap, next);
    }

    /// <summary>
    /// Reset all state. Used between test cases.
    /// </summary>
    internal void Reset(int? newMax = null)
    {
        if (newMax.HasValue)
        {
            _maxWorkers = Math.Max(0, newMax.Value);
        }

        Volatile.Write(ref _activeWorkerCap, _maxWorkers);
        _backpressureEwma = 0.0;
        _warmedUp = false;
        Interlocked.Exchange(ref _backpressureTicks, 0);
        Interlocked.Exchange(ref _workTicks, 0);
        Interlocked.Exchange(ref _chunksProcessed, 0);
        Interlocked.Exchange(ref _chunksSinceLastEval, 0);
    }
}
