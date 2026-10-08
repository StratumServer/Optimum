using System;
using System.Diagnostics;
using System.Threading;

namespace Vintagestory.API.Config
{
    /// <summary>
    /// Optional, in-memory measurements for static GUI composition. Disabled by default.
    /// No text, dialog names, or other UI content is retained.
    /// </summary>
    public static class OptimumGuiMetrics
    {
        /// <summary>Ticks per second for the duration fields in <see cref="OptimumGuiMetricsSnapshot"/>.</summary>
        public static long TimestampFrequency => Stopwatch.Frequency;

        private static int enabled;
        private static long compositionCount;
        private static long compositionTicks;
        private static long maxCompositionTicks;
        private static long composedSurfaceBytes;
        private static long lastSurfaceWidth;
        private static long lastSurfaceHeight;
        private static long initialCompositionCount;
        private static long recompositionCount;
        private static long uploadCount;
        private static long uploadTicks;
        private static long uploadBytes;
        private static long textureAllocationCount;
        private static long textureUpdateCount;
        private static long lastCompositionThreadId;
        private static long textExtentMeasurementCount;
        private static long textExtentMeasurementTicks;
        private static long fontExtentMeasurementCount;
        private static long fontExtentMeasurementTicks;
        private static long lastTextMeasurementThreadId;

        /// <summary>Enables or disables measurements. Disabled is the default.</summary>
        public static bool Enabled
        {
            get => Volatile.Read(ref enabled) != 0;
            set => Volatile.Write(ref enabled, value ? 1 : 0);
        }

        /// <summary>Returns a consistent-enough snapshot of the cumulative counters.</summary>
        public static OptimumGuiMetricsSnapshot Snapshot()
        {
            return new OptimumGuiMetricsSnapshot(
                Interlocked.Read(ref compositionCount),
                Interlocked.Read(ref compositionTicks),
                Interlocked.Read(ref maxCompositionTicks),
                Interlocked.Read(ref composedSurfaceBytes),
                Interlocked.Read(ref lastSurfaceWidth),
                Interlocked.Read(ref lastSurfaceHeight),
                Interlocked.Read(ref initialCompositionCount),
                Interlocked.Read(ref recompositionCount),
                Interlocked.Read(ref uploadCount),
                Interlocked.Read(ref uploadTicks),
                Interlocked.Read(ref uploadBytes),
                Interlocked.Read(ref textureAllocationCount),
                Interlocked.Read(ref textureUpdateCount),
                Interlocked.Read(ref lastCompositionThreadId),
                Interlocked.Read(ref textExtentMeasurementCount),
                Interlocked.Read(ref textExtentMeasurementTicks),
                Interlocked.Read(ref fontExtentMeasurementCount),
                Interlocked.Read(ref fontExtentMeasurementTicks),
                Interlocked.Read(ref lastTextMeasurementThreadId));
        }

        /// <summary>Resets all measurements without changing the enabled state.</summary>
        public static void Reset()
        {
            Interlocked.Exchange(ref compositionCount, 0);
            Interlocked.Exchange(ref compositionTicks, 0);
            Interlocked.Exchange(ref maxCompositionTicks, 0);
            Interlocked.Exchange(ref composedSurfaceBytes, 0);
            Interlocked.Exchange(ref lastSurfaceWidth, 0);
            Interlocked.Exchange(ref lastSurfaceHeight, 0);
            Interlocked.Exchange(ref initialCompositionCount, 0);
            Interlocked.Exchange(ref recompositionCount, 0);
            Interlocked.Exchange(ref uploadCount, 0);
            Interlocked.Exchange(ref uploadTicks, 0);
            Interlocked.Exchange(ref uploadBytes, 0);
            Interlocked.Exchange(ref textureAllocationCount, 0);
            Interlocked.Exchange(ref textureUpdateCount, 0);
            Interlocked.Exchange(ref lastCompositionThreadId, 0);
            Interlocked.Exchange(ref textExtentMeasurementCount, 0);
            Interlocked.Exchange(ref textExtentMeasurementTicks, 0);
            Interlocked.Exchange(ref fontExtentMeasurementCount, 0);
            Interlocked.Exchange(ref fontExtentMeasurementTicks, 0);
            Interlocked.Exchange(ref lastTextMeasurementThreadId, 0);
        }

        internal static long StartTimestamp() => Stopwatch.GetTimestamp();

        internal static void RecordComposition(long startTimestamp, int surfaceWidth, int surfaceHeight, int stride, bool wasRecomposition)
        {
            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            Interlocked.Increment(ref compositionCount);
            Interlocked.Add(ref compositionTicks, elapsed);
            Interlocked.Add(ref composedSurfaceBytes, (long)stride * surfaceHeight);
            Interlocked.Exchange(ref lastSurfaceWidth, surfaceWidth);
            Interlocked.Exchange(ref lastSurfaceHeight, surfaceHeight);
            Interlocked.Exchange(ref lastCompositionThreadId, Environment.CurrentManagedThreadId);
            if (wasRecomposition) Interlocked.Increment(ref recompositionCount);
            else Interlocked.Increment(ref initialCompositionCount);

            long previousMax;
            do
            {
                previousMax = Interlocked.Read(ref maxCompositionTicks);
                if (elapsed <= previousMax) break;
            }
            while (Interlocked.CompareExchange(ref maxCompositionTicks, elapsed, previousMax) != previousMax);
        }

        internal static void RecordUpload(long startTimestamp, int width, int height, bool allocatesTexture)
        {
            Interlocked.Increment(ref uploadCount);
            Interlocked.Add(ref uploadTicks, Stopwatch.GetTimestamp() - startTimestamp);
            Interlocked.Add(ref uploadBytes, (long)width * height * 4);
            if (allocatesTexture) Interlocked.Increment(ref textureAllocationCount);
            else Interlocked.Increment(ref textureUpdateCount);
        }

        internal static void RecordTextExtentMeasurement(long startTimestamp)
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
            Interlocked.Increment(ref textExtentMeasurementCount);
            Interlocked.Add(ref textExtentMeasurementTicks, elapsedTicks);
            Interlocked.Exchange(ref lastTextMeasurementThreadId, Environment.CurrentManagedThreadId);
        }

        internal static void RecordFontExtentMeasurement(long startTimestamp)
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
            Interlocked.Increment(ref fontExtentMeasurementCount);
            Interlocked.Add(ref fontExtentMeasurementTicks, elapsedTicks);
            Interlocked.Exchange(ref lastTextMeasurementThreadId, Environment.CurrentManagedThreadId);
        }
    }

    public readonly struct OptimumGuiMetricsSnapshot
    {
        public long CompositionCount { get; }
        public long CompositionTicks { get; }
        public long MaxCompositionTicks { get; }
        public long ComposedSurfaceBytes { get; }
        public long LastSurfaceWidth { get; }
        public long LastSurfaceHeight { get; }
        public long InitialCompositionCount { get; }
        public long RecompositionCount { get; }
        public long UploadCount { get; }
        public long UploadTicks { get; }
        public long UploadBytes { get; }
        public long TextureAllocationCount { get; }
        public long TextureUpdateCount { get; }
        public long LastCompositionThreadId { get; }
        public long TextExtentMeasurementCount { get; }
        public long TextExtentMeasurementTicks { get; }
        public long FontExtentMeasurementCount { get; }
        public long FontExtentMeasurementTicks { get; }
        public long LastTextMeasurementThreadId { get; }

        internal OptimumGuiMetricsSnapshot(long compositionCount, long compositionTicks, long maxCompositionTicks,
            long composedSurfaceBytes, long lastSurfaceWidth, long lastSurfaceHeight, long initialCompositionCount, long recompositionCount, long uploadCount,
            long uploadTicks, long uploadBytes, long textureAllocationCount, long textureUpdateCount, long lastCompositionThreadId,
            long textExtentMeasurementCount, long textExtentMeasurementTicks, long fontExtentMeasurementCount, long fontExtentMeasurementTicks, long lastTextMeasurementThreadId)
        {
            CompositionCount = compositionCount;
            CompositionTicks = compositionTicks;
            MaxCompositionTicks = maxCompositionTicks;
            ComposedSurfaceBytes = composedSurfaceBytes;
            LastSurfaceWidth = lastSurfaceWidth;
            LastSurfaceHeight = lastSurfaceHeight;
            InitialCompositionCount = initialCompositionCount;
            RecompositionCount = recompositionCount;
            UploadCount = uploadCount;
            UploadTicks = uploadTicks;
            UploadBytes = uploadBytes;
            TextureAllocationCount = textureAllocationCount;
            TextureUpdateCount = textureUpdateCount;
            LastCompositionThreadId = lastCompositionThreadId;
            TextExtentMeasurementCount = textExtentMeasurementCount;
            TextExtentMeasurementTicks = textExtentMeasurementTicks;
            FontExtentMeasurementCount = fontExtentMeasurementCount;
            FontExtentMeasurementTicks = fontExtentMeasurementTicks;
            LastTextMeasurementThreadId = lastTextMeasurementThreadId;
        }
    }
}
