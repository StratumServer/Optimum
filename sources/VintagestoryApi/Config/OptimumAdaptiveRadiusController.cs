using System;
using System.Diagnostics;
using System.Threading;

namespace Vintagestory.API.Config;

/// <summary>
/// Adaptive chunk generation radius controller. Reduces the effective view
/// radius during exploration spikes (high gen-queue depth) so fewer chunk
/// columns queue at once, cutting stutter from gen thread saturation.
///
/// Design:
///   Tick() samples the gen-queue depth each server tick. An EWMA smooths
///   spikes from single-frame bursts. When smoothed depth exceeds the high
///   threshold, EffectiveRadius drops by 1 per tick (min = floor). When it
///   falls below the low threshold, radius recovers by 1 per tick (max =
///   configured MaxChunkRadius). The hysteresis band prevents oscillation.
///
///   The controller never changes Config.MaxChunkRadius itself. Callers
///   read EffectiveRadius and clamp their per-client radius against it.
/// </summary>
public sealed class OptimumAdaptiveRadiusController
{
    private const double EwmaAlpha = 0.15;
    private static readonly long KometProbeIntervalTicks = Stopwatch.Frequency / 4;

    private int _effectiveRadius;
    private int _configuredMax;
    private double _smoothedQueueDepth;
    private bool _warmedUp;
    private long _lastKometProbeTicks;
    private bool _hasKometProbe;

    /// <summary>Current effective radius in chunk columns. Thread-safe read.</summary>
    public int EffectiveRadius => Volatile.Read(ref _effectiveRadius);

    /// <summary>Smoothed queue depth from the last Tick (diagnostics).</summary>
    public double SmoothedQueueDepth => _smoothedQueueDepth;

    /// <summary>
    /// Create the controller. Call once at server startup.
    /// </summary>
    /// <param name="maxRadius">Initial max radius (ServerConfig.MaxChunkRadius).</param>
    public OptimumAdaptiveRadiusController(int maxRadius)
    {
        _configuredMax = Math.Max(1, maxRadius);
        _effectiveRadius = _configuredMax;
        _smoothedQueueDepth = 0;
        _warmedUp = false;
    }

    /// <summary>
    /// Update the configured max (e.g. when a singleplayer client changes view distance).
    /// The effective radius re-clamps on the next Tick.
    /// </summary>
    public void SetMaxRadius(int maxRadius)
    {
        _configuredMax = Math.Max(1, maxRadius);
    }

    /// <summary>
    /// Called once per server tick (or per supply-chunks tick). Reads the
    /// current gen-queue depth and adjusts EffectiveRadius.
    /// </summary>
    /// <param name="queueDepth">Current requestedChunkColumns.Count</param>
    /// <param name="maxRadius">Current Config.MaxChunkRadius (may change in SP)</param>
    public void Tick(int queueDepth, int maxRadius)
    {
        RefreshKometAdaptiveChunkInflowState();

        // Track dynamic MaxChunkRadius changes (singleplayer view distance)
        _configuredMax = Math.Max(1, maxRadius);

        if (!OptimumConfig.EffectiveAdaptiveRadiusEnabled)
        {
            // Disabled: pin to max and skip all math.
            Volatile.Write(ref _effectiveRadius, _configuredMax);
            OptimumConfig.AdaptiveRadiusEffective = _configuredMax;
            return;
        }

        // EWMA update
        double sample = queueDepth;
        if (!_warmedUp)
        {
            _smoothedQueueDepth = sample;
            _warmedUp = true;
        }
        else
        {
            _smoothedQueueDepth = EwmaAlpha * sample + (1.0 - EwmaAlpha) * _smoothedQueueDepth;
        }

        // Hysteresis decision
        int current = Volatile.Read(ref _effectiveRadius);
        int floor = Math.Min(OptimumConfig.AdaptiveRadiusFloor, _configuredMax);
        int highThreshold = OptimumConfig.AdaptiveRadiusHighThreshold;
        int lowThreshold = OptimumConfig.AdaptiveRadiusLowThreshold;

        int next = current;
        if (_smoothedQueueDepth > highThreshold && current > floor)
        {
            next = current - 1;
        }
        else if (_smoothedQueueDepth < lowThreshold && current < _configuredMax)
        {
            next = current + 1;
        }

        Volatile.Write(ref _effectiveRadius, Math.Clamp(next, floor, _configuredMax));

        // Publish to the static volatile for cross-system access
        // (ServerSystemSendChunks reads this without holding a reference to the controller).
        OptimumConfig.AdaptiveRadiusEffective = Volatile.Read(ref _effectiveRadius);
    }

    private void RefreshKometAdaptiveChunkInflowState()
    {
        if (!OptimumConfig.KometDetected)
        {
            OptimumConfig.KometAdaptiveChunkInflowEnabled = false;
            VsModInterop.Release(VsModInterop.FeatureAdaptiveRadius, VsModInterop.RequesterLegacyKometFallback);
            _hasKometProbe = false;
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_hasKometProbe && now - _lastKometProbeTicks < KometProbeIntervalTicks) return;

        _lastKometProbeTicks = now;
        _hasKometProbe = true;

        bool inflow = OptimumCompatibilityGuard.IsKometAdaptiveChunkInflowEnabled();
        OptimumConfig.KometAdaptiveChunkInflowEnabled = inflow;

        // Hold the adaptive radius down while Komet brakes the chunk inflow itself: two
        // controllers steering one backlog is what this guard exists to prevent. The hold goes
        // through the interop registry rather than a private flag, so it is visible in
        // .optimum status and in Komet's own report, and it is dropped the moment the brake is
        // off or Komet is gone. The user's saved AdaptiveRadius preference is never touched.
        if (inflow && !OptimumCompatibilityGuard.HasKometInteropProvider)
        {
            VsModInterop.TryYield(VsModInterop.FeatureAdaptiveRadius, VsModInterop.RequesterLegacyKometFallback, VsModInterop.ReasonKometInflow, out _);
        }
        else
        {
            VsModInterop.Release(VsModInterop.FeatureAdaptiveRadius, VsModInterop.RequesterLegacyKometFallback);
        }
    }

    /// <summary>
    /// Reset state. Used when the world reloads or for testing.
    /// </summary>
    internal void Reset(int? newMax = null)
    {
        _configuredMax = newMax ?? _configuredMax;
        _effectiveRadius = _configuredMax;
        _smoothedQueueDepth = 0;
        _warmedUp = false;
        _lastKometProbeTicks = 0;
        _hasKometProbe = false;
        VsModInterop.Release(VsModInterop.FeatureAdaptiveRadius, VsModInterop.RequesterLegacyKometFallback);
    }
}
