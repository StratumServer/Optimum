using System;

namespace Vintagestory.API.Config;

/// <summary>
/// Active upscaling method used when presenting the 3D scene to the default framebuffer.
/// </summary>
public enum OptimumDynamicUpscalerKind
{
    Native = 0,
    Fsr = 1,
    Bilinear = 2
}

/// <summary>
/// Configured preference for dynamic-resolution upscaling.
/// Auto uses FSR1 (EASU+RCAS) while upscaling overhead fits within the GPU budget and
/// falls back to single-pass hardware bilinear blitting on bandwidth-limited GPUs where
/// full-screen FSR passes exceed the frame-time savings of downscaling.
/// </summary>
public enum OptimumDynamicUpscalerPreference
{
    Auto = 0,
    Fsr = 1,
    Bilinear = 2
}

/// <summary>
/// Step result emitted by <see cref="OptimumDynamicRenderScale"/> per completed GPU measurement batch.
/// </summary>
public readonly struct OptimumDynamicScaleDecision
{
    public readonly float TargetScale;
    public readonly OptimumDynamicUpscalerKind Upscaler;
    public readonly bool ScaleChanged;
    public readonly bool UpscalerDemotedToBilinear;

    public OptimumDynamicScaleDecision(
        float targetScale,
        OptimumDynamicUpscalerKind upscaler,
        bool scaleChanged,
        bool upscalerDemotedToBilinear)
    {
        TargetScale = targetScale;
        Upscaler = upscaler;
        ScaleChanged = scaleChanged;
        UpscalerDemotedToBilinear = upscalerDemotedToBilinear;
    }
}

/// <summary>
/// Non-blocking FIFO ring bookkeeping for OpenGL timestamp query triples
/// (frame start, pre-upscale, frame end).
/// Never overwrites an in-flight unread slot and never drains unready results.
/// </summary>
public sealed class OptimumGpuTimerRing
{
    public const int DefaultCapacity = 6;

    private readonly int capacity;
    private int writeIndex;
    private int readIndex;
    private int pendingCount;

    public int Capacity => capacity;
    public int PendingCount => pendingCount;
    public bool IsFull => pendingCount >= capacity;

    public OptimumGpuTimerRing(int capacity = DefaultCapacity)
    {
        if (capacity < 2 || capacity > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Ring capacity must be in [2, 32].");
        }
        this.capacity = capacity;
    }

    public bool TryAcquireWriteSlot(out int slotIndex)
    {
        if (pendingCount >= capacity)
        {
            slotIndex = -1;
            return false;
        }
        slotIndex = writeIndex;
        writeIndex = (writeIndex + 1) % capacity;
        pendingCount++;
        return true;
    }

    public bool TryPeekWriteSlot(out int slotIndex)
    {
        if (pendingCount >= capacity)
        {
            slotIndex = -1;
            return false;
        }
        slotIndex = writeIndex;
        return true;
    }

    public void CommitWriteSlot()
    {
        if (pendingCount >= capacity)
        {
            return;
        }
        writeIndex = (writeIndex + 1) % capacity;
        pendingCount++;
    }

    public bool TryPeekReadSlot(out int slotIndex)
    {
        if (pendingCount <= 0)
        {
            slotIndex = -1;
            return false;
        }
        slotIndex = readIndex;
        return true;
    }

    public void ConsumeReadSlot()
    {
        if (pendingCount <= 0)
        {
            return;
        }
        readIndex = (readIndex + 1) % capacity;
        pendingCount--;
    }

    public void Reset()
    {
        writeIndex = 0;
        readIndex = 0;
        pendingCount = 0;
    }
}

/// <summary>
/// Bounded LRU cache planner for offscreen resolution tiers. Keeps at most
/// <see cref="MaxCachedTiers"/> (active + 1 recent fallback tier) so 4K sessions
/// do not double or triple VRAM usage across all 4 quantized scale tiers.
/// </summary>
public sealed class OptimumTierCache<T> where T : class
{
    public const int DefaultMaxCachedTiers = 2;

    private struct CacheEntry
    {
        public int ScaleKey;
        public int NativeWidth;
        public int NativeHeight;
        public long LastUsedSeq;
        public T Resource;
    }

    private readonly int maxEntries;
    private readonly CacheEntry[] entries;
    private int count;
    private long sequence;

    public int Count => count;
    public int Capacity => maxEntries;

    public OptimumTierCache(int maxEntries = DefaultMaxCachedTiers)
    {
        if (maxEntries < 1 || maxEntries > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Cache capacity must be in [1, 8].");
        }
        this.maxEntries = maxEntries;
        this.entries = new CacheEntry[maxEntries];
    }

    public static int ToScaleKey(float scale)
    {
        return (int)MathF.Round(Math.Clamp(scale, 0.25f, 4.0f) * 100f);
    }

    public bool TryGet(float scale, int nativeWidth, int nativeHeight, out T resource)
    {
        int key = ToScaleKey(scale);
        for (int i = 0; i < count; i++)
        {
            if (entries[i].ScaleKey == key
                && entries[i].NativeWidth == nativeWidth
                && entries[i].NativeHeight == nativeHeight)
            {
                entries[i].LastUsedSeq = ++sequence;
                resource = entries[i].Resource;
                return true;
            }
        }
        resource = null;
        return false;
    }

    public void Put(float scale, int nativeWidth, int nativeHeight, T resource, out T evicted)
    {
        int key = ToScaleKey(scale);
        evicted = null;

        for (int i = 0; i < count; i++)
        {
            if (entries[i].ScaleKey == key
                && entries[i].NativeWidth == nativeWidth
                && entries[i].NativeHeight == nativeHeight)
            {
                if (!ReferenceEquals(entries[i].Resource, resource))
                {
                    evicted = entries[i].Resource;
                }
                entries[i].Resource = resource;
                entries[i].LastUsedSeq = ++sequence;
                return;
            }
        }

        if (count < maxEntries)
        {
            entries[count++] = new CacheEntry
            {
                ScaleKey = key,
                NativeWidth = nativeWidth,
                NativeHeight = nativeHeight,
                LastUsedSeq = ++sequence,
                Resource = resource
            };
            return;
        }

        int lruIndex = 0;
        long oldestSeq = entries[0].LastUsedSeq;
        for (int i = 1; i < count; i++)
        {
            if (entries[i].LastUsedSeq < oldestSeq)
            {
                oldestSeq = entries[i].LastUsedSeq;
                lruIndex = i;
            }
        }
        evicted = entries[lruIndex].Resource;
        entries[lruIndex] = new CacheEntry
        {
            ScaleKey = key,
            NativeWidth = nativeWidth,
            NativeHeight = nativeHeight,
            LastUsedSeq = ++sequence,
            Resource = resource
        };
    }

    /// <summary>
    /// Removes entries matching a resolution change or full teardown into caller-supplied buffer.
    /// Returns number of evicted resources appended to <paramref name="removed"/>.
    /// </summary>
    public int DrainAll(T[] removed)
    {
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            if (entries[i].Resource != null && removed != null && n < removed.Length)
            {
                removed[n++] = entries[i].Resource;
            }
            entries[i] = default;
        }
        count = 0;
        return n;
    }

    /// <summary>
    /// Evicts non-matching entries (typically any cached fallback tiers when dynamic scaling is disabled).
    /// Returns number of evicted resources appended to <paramref name="removed"/>.
    /// </summary>
    public int DrainExcept(T keepResource, T[] removed)
    {
        int removedCount = 0;
        int writeIdx = 0;
        for (int i = 0; i < count; i++)
        {
            if (ReferenceEquals(entries[i].Resource, keepResource))
            {
                if (writeIdx != i)
                {
                    entries[writeIdx] = entries[i];
                }
                writeIdx++;
            }
            else
            {
                if (entries[i].Resource != null && removed != null && removedCount < removed.Length)
                {
                    removed[removedCount++] = entries[i].Resource;
                }
            }
            if (writeIdx <= i)
            {
                entries[i] = default;
            }
        }
        count = writeIdx;
        return removedCount;
    }
}

/// <summary>
/// Issue #610: Cost-aware quantized dynamic resolution controller with hysteresis
/// and runtime upscaler cost calibration over cached offscreen FBO tiers.
/// </summary>
public sealed class OptimumDynamicRenderScale
{
    public const int MaxCachedTiers = OptimumTierCache<object>.DefaultMaxCachedTiers;
    public const double MaxValidGpuSampleMs = 2000.0;
    public const int MinTargetFps = 15;
    public const int MaxTargetFps = 240;

    public const double DownscaleOverBudgetRatio = 1.06;
    public const double SpikeDownscaleRatio = 1.38;
    public const double UpscaleUnderBudgetRatio = 0.76;

    public const int DownscaleConsecutiveSamples = 3;
    public const int DownscaleCooldownSamples = 15;
    public const int UpscaleStableSamples = 90;
    public const int PostDownscaleUpscaleLockoutSamples = 120;
    public const int CalibrationWindowSamples = 4;
    public const double MaxFsrBudgetFraction = 0.35;
    public const double MinStepImprovementRatio = 0.96;

    public static readonly float[] QuantizedTiers = new float[] { 1.0f, 0.85f, 0.77f, 0.67f };

    private float ceilingScale = 1.0f;
    private float minScale = 0.67f;
    private int currentTierIndex;
    private int nativeWidth;
    private int nativeHeight;
    private double targetBudgetMs = 1000.0 / 60.0;
    private OptimumDynamicUpscalerPreference preference = OptimumDynamicUpscalerPreference.Auto;

    private double smoothedSceneMs;
    private double smoothedUpscaleMs;
    private double smoothedTotalMs;
    private bool hasSamples;

    private double baselineNativeBlitMs;
    private bool hasBaselineBlit;
    private double calibratedFsrMs;
    private bool hasCalibratedFsr;
    private bool fsrDemotedToBilinear;
    private bool probingFsrEffectiveness;
    private double preStepTotalMs;
    private double preStepSceneMs;
    private int samplesAtCurrentTier;
    private int zeroGainLockoutSamples;

    private int overBudgetStreak;
    private int underBudgetStreak;
    private int downscaleCooldown;
    private int upscaleLockout;
    private int warmupSamplesRemaining;

    public float CurrentScale => QuantizedTiers[currentTierIndex];
    public float CeilingScale => ceilingScale;
    public float MinScale => minScale;
    public double TargetBudgetMs => targetBudgetMs;
    public double SmoothedSceneMs => smoothedSceneMs;
    public double SmoothedUpscaleMs => smoothedUpscaleMs;
    public double SmoothedTotalMs => smoothedTotalMs;
    public bool FsrDemotedToBilinear => fsrDemotedToBilinear;

    public OptimumDynamicUpscalerKind ActiveUpscaler
    {
        get
        {
            if (CurrentScale >= 1.0f)
            {
                return OptimumDynamicUpscalerKind.Native;
            }
            if (preference == OptimumDynamicUpscalerPreference.Bilinear
                || (preference == OptimumDynamicUpscalerPreference.Auto && fsrDemotedToBilinear))
            {
                return OptimumDynamicUpscalerKind.Bilinear;
            }
            return OptimumDynamicUpscalerKind.Fsr;
        }
    }

    public static float SnapToQuantizedTier(float scale)
    {
        float clamped = Math.Clamp(scale, QuantizedTiers[QuantizedTiers.Length - 1], 1.0f);
        int bestIdx = QuantizedTiers.Length - 1;
        float bestDiff = MathF.Abs(clamped - QuantizedTiers[bestIdx]);
        for (int i = 0; i < QuantizedTiers.Length - 1; i++)
        {
            float diff = MathF.Abs(clamped - QuantizedTiers[i]);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIdx = i;
            }
        }
        return QuantizedTiers[bestIdx];
    }

    public static int FindTierIndex(float scale)
    {
        float snapped = SnapToQuantizedTier(scale);
        for (int i = 0; i < QuantizedTiers.Length; i++)
        {
            if (MathF.Abs(QuantizedTiers[i] - snapped) < 0.001f)
            {
                return i;
            }
        }
        return 0;
    }

    public static OptimumDynamicUpscalerPreference ParseUpscalerPreference(string value)
    {
        if (string.Equals(value, "Fsr", StringComparison.OrdinalIgnoreCase))
        {
            return OptimumDynamicUpscalerPreference.Fsr;
        }
        if (string.Equals(value, "Bilinear", StringComparison.OrdinalIgnoreCase))
        {
            return OptimumDynamicUpscalerPreference.Bilinear;
        }
        return OptimumDynamicUpscalerPreference.Auto;
    }

    public static string FormatUpscalerPreference(OptimumDynamicUpscalerPreference pref)
    {
        return pref switch
        {
            OptimumDynamicUpscalerPreference.Fsr => "Fsr",
            OptimumDynamicUpscalerPreference.Bilinear => "Bilinear",
            _ => "Auto"
        };
    }

    /// <summary>
    /// Computes the active 3D offscreen viewport scale while preserving supersampling
    /// (SSAA &gt; 1.0, mega-screenshots) and manual static resolution overrides.
    /// </summary>
    public static float ResolveActiveViewportScale(
        float clientSsaa,
        float staticEffectiveRenderScale,
        float currentOptimumRenderScale,
        bool dynamicEnabled)
    {
        if (clientSsaa > 1.0f || clientSsaa < 0.5f)
        {
            return clientSsaa;
        }
        if (dynamicEnabled && (clientSsaa >= 1.0f || staticEffectiveRenderScale < 1.0f))
        {
            return Math.Clamp(currentOptimumRenderScale, 0.5f, 1.0f);
        }
        if (staticEffectiveRenderScale < 1.0f)
        {
            return staticEffectiveRenderScale;
        }
        return clientSsaa;
    }

    public void Configure(
        int width,
        int height,
        float manualCeilingScale,
        float configuredMinScale,
        int targetFps,
        OptimumDynamicUpscalerPreference upscalerPreference)
    {
        int clampedFps = Math.Clamp(targetFps, MinTargetFps, MaxTargetFps);
        targetBudgetMs = 1000.0 / clampedFps;

        float snappedCeiling = SnapToQuantizedTier(Math.Clamp(manualCeilingScale, 0.5f, 1.0f));
        float snappedMin = SnapToQuantizedTier(Math.Clamp(configuredMinScale, 0.5f, 1.0f));
        if (snappedMin > snappedCeiling)
        {
            snappedMin = snappedCeiling;
        }

        bool resolutionChanged = width != nativeWidth || height != nativeHeight;
        bool preferenceChanged = upscalerPreference != preference;

        nativeWidth = Math.Max(1, width);
        nativeHeight = Math.Max(1, height);
        ceilingScale = snappedCeiling;
        minScale = snappedMin;
        preference = upscalerPreference;

        if (resolutionChanged || preferenceChanged)
        {
            baselineNativeBlitMs = 0.0;
            hasBaselineBlit = false;
            calibratedFsrMs = 0.0;
            hasCalibratedFsr = false;
            fsrDemotedToBilinear = false;
            probingFsrEffectiveness = false;
            zeroGainLockoutSamples = 0;
        }

        int minTierIdx = FindTierIndex(minScale);
        int maxTierIdx = FindTierIndex(ceilingScale);
        if (currentTierIndex < maxTierIdx || currentTierIndex > minTierIdx)
        {
            currentTierIndex = maxTierIdx;
        }
    }

    public void ResetToBaseline(float manualCeilingScale, int width, int height, float configuredMinScale, int targetFps, OptimumDynamicUpscalerPreference upscalerPreference)
    {
        Configure(width, height, manualCeilingScale, configuredMinScale, targetFps, upscalerPreference);
        currentTierIndex = FindTierIndex(ceilingScale);
        hasSamples = false;
        smoothedSceneMs = 0.0;
        smoothedUpscaleMs = 0.0;
        smoothedTotalMs = 0.0;
        samplesAtCurrentTier = 0;
        overBudgetStreak = 0;
        underBudgetStreak = 0;
        downscaleCooldown = 0;
        upscaleLockout = 0;
        probingFsrEffectiveness = false;
        zeroGainLockoutSamples = 0;
        warmupSamplesRemaining = 3;
    }

    public OptimumDynamicScaleDecision RecordSample(double sceneGpuMs, double upscaleGpuMs)
    {
        if (!IsValidSample(sceneGpuMs) || !IsValidNonNegativeSample(upscaleGpuMs))
        {
            return new OptimumDynamicScaleDecision(CurrentScale, ActiveUpscaler, false, false);
        }

        double totalMs = sceneGpuMs + upscaleGpuMs;
        if (!IsValidSample(totalMs))
        {
            return new OptimumDynamicScaleDecision(CurrentScale, ActiveUpscaler, false, false);
        }

        if (warmupSamplesRemaining > 0)
        {
            warmupSamplesRemaining--;
            return new OptimumDynamicScaleDecision(CurrentScale, ActiveUpscaler, false, false);
        }

        if (!hasSamples)
        {
            smoothedSceneMs = sceneGpuMs;
            smoothedUpscaleMs = upscaleGpuMs;
            smoothedTotalMs = totalMs;
            hasSamples = true;
        }
        else
        {
            double alpha = totalMs > smoothedTotalMs ? 0.35 : 0.15;
            smoothedSceneMs += alpha * (sceneGpuMs - smoothedSceneMs);
            smoothedUpscaleMs += alpha * (upscaleGpuMs - smoothedUpscaleMs);
            smoothedTotalMs += alpha * (totalMs - smoothedTotalMs);
        }

        samplesAtCurrentTier++;
        if (downscaleCooldown > 0) downscaleCooldown--;
        if (upscaleLockout > 0) upscaleLockout--;
        if (zeroGainLockoutSamples > 0) zeroGainLockoutSamples--;

        if (CurrentScale >= 1.0f)
        {
            baselineNativeBlitMs = hasBaselineBlit
                ? baselineNativeBlitMs + 0.2 * (upscaleGpuMs - baselineNativeBlitMs)
                : upscaleGpuMs;
            hasBaselineBlit = true;
        }
        else if (ActiveUpscaler == OptimumDynamicUpscalerKind.Fsr)
        {
            calibratedFsrMs = hasCalibratedFsr
                ? calibratedFsrMs + 0.25 * (upscaleGpuMs - calibratedFsrMs)
                : upscaleGpuMs;
            hasCalibratedFsr = true;
        }

        bool demotedNow = false;
        if (CurrentScale < 1.0f && preference == OptimumDynamicUpscalerPreference.Auto && !fsrDemotedToBilinear && samplesAtCurrentTier >= CalibrationWindowSamples)
        {
            double fsrExtraCost = calibratedFsrMs - (hasBaselineBlit ? baselineNativeBlitMs : 0.0);
            bool fsrTooExpensiveForBudget = calibratedFsrMs > targetBudgetMs * MaxFsrBudgetFraction;
            bool stepDidNotHelp = probingFsrEffectiveness && preStepTotalMs > 0.0 && smoothedTotalMs >= preStepTotalMs * MinStepImprovementRatio;
            if (fsrTooExpensiveForBudget || stepDidNotHelp || (probingFsrEffectiveness && fsrExtraCost > Math.Max(0.5, (preStepSceneMs - smoothedSceneMs) * 0.85)))
            {
                fsrDemotedToBilinear = true;
                probingFsrEffectiveness = false;
                demotedNow = true;
                // Re-anchor smoothing on Bilinear baseline next frames cleanly.
                smoothedUpscaleMs = hasBaselineBlit ? baselineNativeBlitMs : Math.Min(smoothedUpscaleMs, 3.0);
                smoothedTotalMs = smoothedSceneMs + smoothedUpscaleMs;
                samplesAtCurrentTier = 0;
                overBudgetStreak = 0;
                underBudgetStreak = 0;
                return new OptimumDynamicScaleDecision(CurrentScale, ActiveUpscaler, false, true);
            }
            probingFsrEffectiveness = false;
        }

        int ceilingTierIdx = FindTierIndex(ceilingScale);
        int floorTierIdx = FindTierIndex(minScale);
        if (ceilingTierIdx >= floorTierIdx)
        {
            bool forcedSync = currentTierIndex != ceilingTierIdx;
            currentTierIndex = ceilingTierIdx;
            return new OptimumDynamicScaleDecision(CurrentScale, ActiveUpscaler, forcedSync, demotedNow);
        }

        // If prior Bilinear step produced virtually zero scene-time relief (CPU or vertex bound),
        // avoid chasing even blurrier lower tiers and return toward ceiling scale.
        if (CurrentScale < ceilingScale
            && (!probingFsrEffectiveness || ActiveUpscaler == OptimumDynamicUpscalerKind.Bilinear)
            && preStepSceneMs > 0.0
            && samplesAtCurrentTier >= CalibrationWindowSamples + 2
            && smoothedSceneMs >= preStepSceneMs * 0.97)
        {
            zeroGainLockoutSamples = 300;
            int nextUp = Math.Max(ceilingTierIdx, currentTierIndex - 1);
            if (nextUp != currentTierIndex)
            {
                currentTierIndex = nextUp;
                ResetStepTracking();
                return new OptimumDynamicScaleDecision(CurrentScale, ActiveUpscaler, true, demotedNow);
            }
        }

        if (smoothedTotalMs > targetBudgetMs * DownscaleOverBudgetRatio)
        {
            overBudgetStreak++;
            underBudgetStreak = 0;
        }
        else if (smoothedTotalMs < targetBudgetMs * UpscaleUnderBudgetRatio)
        {
            underBudgetStreak++;
            overBudgetStreak = 0;
        }
        else
        {
            overBudgetStreak = 0;
            underBudgetStreak = 0;
        }

        bool spikeOverBudget = totalMs > targetBudgetMs * SpikeDownscaleRatio && smoothedTotalMs > targetBudgetMs;
        if (currentTierIndex < floorTierIdx
            && downscaleCooldown == 0
            && zeroGainLockoutSamples == 0
            && (overBudgetStreak >= DownscaleConsecutiveSamples || spikeOverBudget))
        {
            int nextIdx = currentTierIndex + 1;
            float nextScale = QuantizedTiers[nextIdx];
            bool enteringLowerFromNative = currentTierIndex == 0 && nextIdx > 0;
            bool fsrAlreadyKnownUnviable = preference == OptimumDynamicUpscalerPreference.Auto
                && hasCalibratedFsr
                && calibratedFsrMs > targetBudgetMs * MaxFsrBudgetFraction;
            if (fsrAlreadyKnownUnviable)
            {
                fsrDemotedToBilinear = true;
            }

            preStepTotalMs = smoothedTotalMs;
            preStepSceneMs = smoothedSceneMs;
            probingFsrEffectiveness = enteringLowerFromNative
                && preference == OptimumDynamicUpscalerPreference.Auto
                && !fsrDemotedToBilinear;

            currentTierIndex = nextIdx;
            downscaleCooldown = DownscaleCooldownSamples;
            upscaleLockout = PostDownscaleUpscaleLockoutSamples;
            ResetStepTracking();
            // Do not keep stale higher-res EMA after switching down.
            double expectedRatio = (double)(nextScale * nextScale) / (QuantizedTiers[nextIdx - 1] * QuantizedTiers[nextIdx - 1]);
            smoothedSceneMs *= expectedRatio;
            smoothedTotalMs = smoothedSceneMs + smoothedUpscaleMs;
            return new OptimumDynamicScaleDecision(CurrentScale, ActiveUpscaler, true, demotedNow);
        }

        if (currentTierIndex > ceilingTierIdx
            && upscaleLockout == 0
            && underBudgetStreak >= UpscaleStableSamples)
        {
            int nextIdx = currentTierIndex - 1;
            float nextScale = QuantizedTiers[nextIdx];
            currentTierIndex = nextIdx;
            preStepTotalMs = 0.0;
            preStepSceneMs = 0.0;
            ResetStepTracking();
            double expectedRatio = (double)(nextScale * nextScale) / (QuantizedTiers[nextIdx + 1] * QuantizedTiers[nextIdx + 1]);
            smoothedSceneMs *= expectedRatio;
            if (nextIdx == 0 && hasBaselineBlit)
            {
                smoothedUpscaleMs = baselineNativeBlitMs;
            }
            smoothedTotalMs = smoothedSceneMs + smoothedUpscaleMs;
            return new OptimumDynamicScaleDecision(CurrentScale, ActiveUpscaler, true, demotedNow);
        }

        return new OptimumDynamicScaleDecision(CurrentScale, ActiveUpscaler, false, demotedNow);
    }

    private void ResetStepTracking()
    {
        samplesAtCurrentTier = 0;
        overBudgetStreak = 0;
        underBudgetStreak = 0;
    }

    private static bool IsValidSample(double valueMs)
    {
        return !double.IsNaN(valueMs) && !double.IsInfinity(valueMs) && valueMs > 0.0 && valueMs <= MaxValidGpuSampleMs;
    }

    private static bool IsValidNonNegativeSample(double valueMs)
    {
        return !double.IsNaN(valueMs) && !double.IsInfinity(valueMs) && valueMs >= 0.0 && valueMs <= MaxValidGpuSampleMs;
    }
}
