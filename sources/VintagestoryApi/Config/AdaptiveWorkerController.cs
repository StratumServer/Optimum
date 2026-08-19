using System;
using System.Diagnostics;
using System.Threading;

namespace Vintagestory.API.Config;

/// <summary>
/// Adaptive worldgen worker cap controller. Adjusts the active worker count
/// based on measured lock contention (the E.2 illuminator lock that serializes
/// passes 3/4). Pure logic: no threads, no side effects, fully testable.
///
/// Architecture:
///   Workers accumulate lock-wait ticks and work ticks into shared atomics.
///   After every EvalInterval chunks, the scheduler calls Evaluate() which
///   reads the atomics, updates the EWMA contention ratio, and returns the
///   new active worker cap. Workers whose index >= cap yield until re-evaluated.
///
/// Anti-thrashing design:
///   - Asymmetric hysteresis: scale-down fires at >40% contention, scale-up at <15%
///   - Cooldown: after each adjustment, the next EvalInterval chunks generate data
///     for the new worker count before any further change
///   - EWMA smoothing (alpha 0.3): a single slow chunk or GC spike does not trigger
///   - Queue pressure override: if contention is moderate (15-40%) but the queue
///     keeps growing, the controller does not scale down (starvation guard)
/// </summary>
public sealed class AdaptiveWorkerController
{
    // Thresholds for hysteresis
    private const double ScaleDownThreshold = 0.40;
    private const double ScaleUpThreshold = 0.15;
    private const double EwmaAlpha = 0.3;
    private const int QueuePressureFloor = 10;

    private int _maxWorkers;
    private readonly int _evalInterval;

    private int _activeWorkerCap;
    private double _contentionEwma;
    private long _chunksSinceLastEval;
    private bool _warmedUp;

    // Atomics that workers write to (exposed for the scheduler to accumulate into)
    private long _lockWaitTicks;
    private long _workTicks;
    private long _chunksGenerated;

    /// <summary>
    /// Fires when Evaluate() changes the active cap. Arguments: oldCap, newCap, contentionEwma.
    /// Wire this to the server logger at construction. Null-safe (no-op if unset).
    /// </summary>
    public Action<int, int, double> OnCapChanged;

    /// <summary>
    /// Current active worker cap. Workers with index >= this value yield.
    /// </summary>
    public int ActiveWorkerCap => Volatile.Read(ref _activeWorkerCap);

    /// <summary>
    /// The EWMA contention ratio from the last evaluation (for diagnostics).
    /// </summary>
    public double ContentionRatio => _contentionEwma;

    /// <summary>
    /// Total chunks generated since controller creation (for diagnostics).
    /// </summary>
    public long TotalChunksGenerated => Volatile.Read(ref _chunksGenerated);

    /// <param name="maxWorkers">
    /// Hard ceiling: the static-policy worker count for this machine. The
    /// controller never exceeds this.
    /// </param>
    /// <param name="evalInterval">
    /// Chunks between evaluations. Default 50. Lower values react faster but
    /// risk thrashing on short noise bursts.
    /// </param>
    public AdaptiveWorkerController(int maxWorkers, int evalInterval = 50)
    {
        _maxWorkers = Math.Max(0, maxWorkers);
        _evalInterval = Math.Max(10, evalInterval);
        _activeWorkerCap = _maxWorkers;
        _contentionEwma = 0.0;
        _warmedUp = false;
    }

    /// <summary>
    /// Record a completed chunk generation with its timing breakdown.
    /// Called by each worker after PopulateChunk returns.
    /// </summary>
    /// <param name="lockWaitTicks">Stopwatch ticks spent waiting on the E.2 lock (0 if pass was not 3/4)</param>
    /// <param name="workTicks">Stopwatch ticks spent doing actual generation (inside lock or outside)</param>
    public void RecordChunk(long lockWaitTicks, long workTicks)
    {
        Interlocked.Add(ref _lockWaitTicks, lockWaitTicks);
        Interlocked.Add(ref _workTicks, workTicks);
        Interlocked.Increment(ref _chunksGenerated);
        Interlocked.Increment(ref _chunksSinceLastEval);
    }

    /// <summary>
    /// Check whether an evaluation is due. Call from the thread loop; if true,
    /// call Evaluate(). Split from Evaluate() so only one thread runs the
    /// evaluation logic (the caller should Interlocked.Exchange the counter).
    /// </summary>
    public bool ShouldEvaluate()
    {
        return Volatile.Read(ref _chunksSinceLastEval) >= _evalInterval;
    }

    /// <summary>
    /// Run the adaptive evaluation. Returns the new active worker cap.
    /// Only one thread should call this per interval (use a CAS gate).
    /// </summary>
    /// <param name="queueDepth">Current pending chunk column count (for starvation guard)</param>
    public int Evaluate(int queueDepth)
    {
        // Atomically drain the accumulators
        long lockWait = Interlocked.Exchange(ref _lockWaitTicks, 0);
        long work = Interlocked.Exchange(ref _workTicks, 0);
        Interlocked.Exchange(ref _chunksSinceLastEval, 0);

        // Compute instantaneous contention ratio
        long total = lockWait + work;
        double instantRatio = total > 0 ? (double)lockWait / total : 0.0;

        // Update EWMA (first evaluation seeds it instead of smoothing from 0)
        if (!_warmedUp)
        {
            _contentionEwma = instantRatio;
            _warmedUp = true;
        }
        else
        {
            _contentionEwma = EwmaAlpha * instantRatio + (1.0 - EwmaAlpha) * _contentionEwma;
        }

        // Decision logic with hysteresis
        int current = Volatile.Read(ref _activeWorkerCap);
        int next = current;

        if (_contentionEwma > ScaleDownThreshold && current > 1)
        {
            // High contention: workers spend >40% waiting. Drop one.
            // Starvation guard: if the queue is deep, contention might be
            // from workload peaking (many pass-3/4 chunks at once), not from
            // too many workers. Only scale down if queue is not growing fast.
            if (queueDepth < QueuePressureFloor * current)
            {
                next = current - 1;
            }
        }
        else if (_contentionEwma < ScaleUpThreshold && current < _maxWorkers)
        {
            // Low contention AND under ceiling: try adding one worker.
            // Only scale up if there is work to do (queue non-empty).
            if (queueDepth > QueuePressureFloor)
            {
                next = current + 1;
            }
        }

        Volatile.Write(ref _activeWorkerCap, next);
        if (next != current)
        {
            try { OnCapChanged?.Invoke(current, next, _contentionEwma); } catch { }
        }
        return next;
    }

    /// <summary>
    /// Force the cap to a specific value. Used by tests and the env override.
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
        _contentionEwma = 0.0;
        _warmedUp = false;
        Interlocked.Exchange(ref _lockWaitTicks, 0);
        Interlocked.Exchange(ref _workTicks, 0);
        Interlocked.Exchange(ref _chunksGenerated, 0);
        Interlocked.Exchange(ref _chunksSinceLastEval, 0);
    }
}
