using System;
using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;

namespace Vintagestory.API.Config;

/// <summary>
/// Thread-local scratch backing for <see cref="NewNormalizedSimplexFractalNoise.ColumnNoise"/>
/// with a per-thread generation guard so stale or cross-thread column instances fail fast
/// instead of silently aliasing overwritten octave data.
/// </summary>
public sealed class OptimumColumnNoiseBuffer
{
    private const int InitialCapacity = 48;
    private const double ValueMultiplier = 1.2 * SimplexNoiseOctave.MAX_VALUE_3D;
    private const double ThresholdRescaleOldToNew = 1.0 / SimplexNoiseOctave.MAX_VALUE_3D;
    private const double AmpAndFreqToThresholdSmoothing = 3.5;

    private struct OctaveEntry
    {
        public long Seed;
        public double XrBase;
        public double YrBase;
        public double ZrBase;
        public double FrequencyY;
        public double Amplitude;
        public double Threshold;
        public double SmoothingFactor;
        public double StopBound;
    }

    private struct PastEvaluation
    {
        public double Value;
        public double Y;
    }

    [ThreadStatic]
    private static OptimumColumnNoiseBuffer? threadBuffer;

    private OctaveEntry[] octaves;
    private PastEvaluation[] pastEvaluations;
    private double[] maxValues;
    private int[] order;
    private uint generation;

    private OptimumColumnNoiseBuffer(int capacity)
    {
        int initialSize = Math.Max(InitialCapacity, capacity);
        octaves = new OctaveEntry[initialSize];
        pastEvaluations = new PastEvaluation[initialSize];
        maxValues = new double[initialSize];
        order = new int[initialSize];
    }

    public static OptimumColumnNoiseBuffer Initialize(
        NewNormalizedSimplexFractalNoise terrainNoise,
        double relativeYFrequency,
        double[] amplitudes,
        double[] thresholds,
        double noiseX,
        double noiseZ,
        out double uncurvedBound,
        out double boundMin,
        out double boundMax,
        out int usedOctaves,
        out uint generation)
    {
        int availableOctaves = terrainNoise.frequencies.Length;
        OptimumColumnNoiseBuffer buffer = threadBuffer ??= new OptimumColumnNoiseBuffer(availableOctaves);

        // Grow only the setup scratch arrays first so an invalid-argument exception in Loop 1
        // leaves any earlier active ColumnNoise's octave buffer and generation intact.
        if (buffer.maxValues.Length < availableOctaves)
        {
            int newSize = Math.Max(InitialCapacity, availableOctaves);
            buffer.maxValues = new double[newSize];
            buffer.order = new int[newSize];
        }

        double[] maxValues = buffer.maxValues;
        int[] order = buffer.order;

        int usedOctavesLocal = 0;
        double bound = 0;
        for (int i = availableOctaves - 1; i >= 0; i--)
        {
            maxValues[i] = Math.Max(0, Math.Abs(amplitudes[i]) - thresholds[i]) * ValueMultiplier;
            bound += maxValues[i];

            if (maxValues[i] == 0)
            {
                continue;
            }

            // Descending order: biggest octaves first so layers can be ruled out sooner.
            order[usedOctavesLocal] = i;
            for (int j = usedOctavesLocal - 1; j >= 0; j--)
            {
                if (maxValues[order[j + 1]] > maxValues[order[j]])
                {
                    int temp = order[j];
                    order[j] = order[j + 1];
                    order[j + 1] = temp;
                }
            }
            usedOctavesLocal++;
        }

        uncurvedBound = bound;
        boundMin = NewNormalizedSimplexFractalNoise.NoiseValueCurve(-bound);
        boundMax = NewNormalizedSimplexFractalNoise.NoiseValueCurve(bound);
        usedOctaves = usedOctavesLocal;

        uint nextGeneration = unchecked(++buffer.generation);
        if (nextGeneration == 0)
        {
            nextGeneration = ++buffer.generation;
        }
        generation = nextGeneration;

        if (buffer.octaves.Length < availableOctaves)
        {
            int newSize = Math.Max(InitialCapacity, availableOctaves);
            buffer.octaves = new OctaveEntry[newSize];
            buffer.pastEvaluations = new PastEvaluation[newSize];
        }

        OctaveEntry[] octaves = buffer.octaves;
        PastEvaluation[] pastEvaluations = buffer.pastEvaluations;
        double uncertaintySum = 0;
        for (int j = usedOctavesLocal - 1; j >= 0; j--)
        {
            int i = order[j];
            uncertaintySum += maxValues[i];
            double thisOctaveFrequency = terrainNoise.frequencies[i];
            double x = noiseX * thisOctaveFrequency;
            double z = noiseZ * thisOctaveFrequency;
            double xz = x + z;
            double s2 = xz * OptimumSimplexNoise.Rotate3Orthogonalizer;
            octaves[j] = new OctaveEntry
            {
                Seed = terrainNoise.octaveSeeds[i],
                XrBase = x + s2,
                YrBase = xz * -OptimumSimplexNoise.Root3Over3,
                ZrBase = z + s2,
                FrequencyY = thisOctaveFrequency * relativeYFrequency,
                Amplitude = amplitudes[i] * ValueMultiplier,
                Threshold = thresholds[i] * (ValueMultiplier * ThresholdRescaleOldToNew),
                SmoothingFactor = amplitudes[i] * thisOctaveFrequency * AmpAndFreqToThresholdSmoothing,
                StopBound = uncertaintySum,
            };
            pastEvaluations[j] = new PastEvaluation
            {
                Value = 0.0,
                Y = double.NaN,
            };
        }

        return buffer;
    }

    public static double NoiseSign(
        OptimumColumnNoiseBuffer? buffer,
        int usedOctaves,
        uint generation,
        double y,
        double inverseCurvedThresholder)
    {
        if (buffer == null)
        {
            throw new NullReferenceException();
        }
        if (buffer.generation != generation || !ReferenceEquals(buffer, threadBuffer))
        {
            ThrowStaleColumnNoise();
        }

        ReadOnlySpan<OctaveEntry> orderedOctaveEntries = buffer.octaves.AsSpan(0, usedOctaves);
        Span<PastEvaluation> pastEvaluations = buffer.pastEvaluations.AsSpan(0, usedOctaves);

        double value = inverseCurvedThresholder;

        const double maxYSlope = NewSimplexNoiseLayer.MaxYSlope_ImprovedXZ;
        double valueTempMin = inverseCurvedThresholder;
        double valueTempMax = inverseCurvedThresholder;
        for (int j = 0; j < orderedOctaveEntries.Length; j++)
        {
            if (!(valueTempMax <= 0) && !(valueTempMin >= 0))
            {
                break;
            }

            ref readonly OctaveEntry octaveEntry = ref orderedOctaveEntries[j];

            if (valueTempMin >= octaveEntry.StopBound)
            {
                return valueTempMin;
            }
            if (valueTempMax <= -octaveEntry.StopBound)
            {
                return valueTempMax;
            }

            ref PastEvaluation pastEval = ref pastEvaluations[j];
            double pastY = pastEval.Y;
            if (double.IsNaN(pastY))
            {
                // Vanilla computes NaN into valueTempMin/valueTempMax here and breaks on the next iteration.
                break;
            }

            double evalY = y * octaveEntry.FrequencyY;
            double deltaY = Math.Abs(pastY - evalY);
            double slopeDelta = deltaY * maxYSlope;
            double pastValue = pastEval.Value;
            valueTempMin += ApplyThresholding(
                Math.Max(-1, pastValue - slopeDelta) * octaveEntry.Amplitude,
                octaveEntry.Threshold,
                octaveEntry.SmoothingFactor);
            valueTempMax += ApplyThresholding(
                Math.Min(1, pastValue + slopeDelta) * octaveEntry.Amplitude,
                octaveEntry.Threshold,
                octaveEntry.SmoothingFactor);
        }

        for (int j = 0; j < orderedOctaveEntries.Length; j++)
        {
            ref readonly OctaveEntry octaveEntry = ref orderedOctaveEntries[j];

            if (value >= octaveEntry.StopBound || value <= -octaveEntry.StopBound)
            {
                break;
            }

            double evalY = y * octaveEntry.FrequencyY;
            double noiseValue = OptimumSimplexNoise.EvaluatePreRotated(
                octaveEntry.Seed,
                octaveEntry.XrBase,
                octaveEntry.YrBase,
                octaveEntry.ZrBase,
                evalY);
            ref PastEvaluation pastEval = ref pastEvaluations[j];
            pastEval.Value = noiseValue;
            pastEval.Y = evalY;
            value += ApplyThresholding(
                noiseValue * octaveEntry.Amplitude,
                octaveEntry.Threshold,
                octaveEntry.SmoothingFactor);
        }

        return value;
    }

    public static double Noise(
        OptimumColumnNoiseBuffer? buffer,
        int usedOctaves,
        uint generation,
        double y)
    {
        if (buffer == null)
        {
            throw new NullReferenceException();
        }
        if (buffer.generation != generation || !ReferenceEquals(buffer, threadBuffer))
        {
            ThrowStaleColumnNoise();
        }

        ReadOnlySpan<OctaveEntry> orderedOctaveEntries = buffer.octaves.AsSpan(0, usedOctaves);

        double value = 0.0;
        for (int j = 0; j < orderedOctaveEntries.Length; j++)
        {
            ref readonly OctaveEntry octaveEntry = ref orderedOctaveEntries[j];

            double noiseValue = OptimumSimplexNoise.EvaluatePreRotated(
                octaveEntry.Seed,
                octaveEntry.XrBase,
                octaveEntry.YrBase,
                octaveEntry.ZrBase,
                y * octaveEntry.FrequencyY) * octaveEntry.Amplitude;
            value += ApplyThresholding(noiseValue, octaveEntry.Threshold, octaveEntry.SmoothingFactor);
        }

        return NewNormalizedSimplexFractalNoise.NoiseValueCurve(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ApplyThresholding(double value, double threshold, double smoothingFactor)
    {
        return SmoothMaxZero(value - threshold, smoothingFactor)
            + SmoothMinZero(value + threshold, smoothingFactor);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SmoothMaxZero(double value, double smoothingFactor)
    {
        double diff = smoothingFactor - Math.Abs(value);
        if (diff <= 0.0 && smoothingFactor > 0.0)
        {
            return Math.Max(0.0, value);
        }

        double falloff = Math.Max(diff, 0.0) / smoothingFactor;
        return Math.Max(0.0, value) + falloff * falloff * smoothingFactor * 0.25;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SmoothMinZero(double value, double smoothingFactor)
    {
        double diff = smoothingFactor - Math.Abs(value);
        if (diff <= 0.0 && smoothingFactor > 0.0)
        {
            return Math.Min(0.0, value);
        }

        double falloff = Math.Max(diff, 0.0) / smoothingFactor;
        return Math.Min(0.0, value) - falloff * falloff * smoothingFactor * 0.25;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStaleColumnNoise()
    {
        throw new InvalidOperationException(
            "ColumnNoise reuses a single active per-thread buffer per thread. " +
            "Finish using one ForColumn(...) instance before calling ForColumn(...) again or passing it across threads.");
    }
}
