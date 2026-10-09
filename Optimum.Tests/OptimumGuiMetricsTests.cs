using System;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[CollectionDefinition("GUI metrics", DisableParallelization = true)]
public sealed class GuiMetricsCollection { }

[Collection("GUI metrics")]
public class OptimumGuiMetricsTests
{
    [Fact]
    public void SnapshotRecordsCompositionsAndAllocationsSeparatelyFromUpdates()
    {
        bool wasEnabled = OptimumGuiMetrics.Enabled;
        try
        {
            OptimumGuiMetrics.Enabled = true;
            OptimumGuiMetrics.Reset();

            long compositionStart = OptimumGuiMetrics.StartTimestamp();
            OptimumGuiMetrics.RecordComposition(compositionStart, surfaceWidth: 20, surfaceHeight: 10, stride: 80, wasRecomposition: false);
            OptimumGuiMetrics.RecordComposition(OptimumGuiMetrics.StartTimestamp(), surfaceWidth: 30, surfaceHeight: 12, stride: 120, wasRecomposition: true);
            OptimumGuiMetrics.RecordUpload(OptimumGuiMetrics.StartTimestamp(), width: 20, height: 10, allocatesTexture: true);
            OptimumGuiMetrics.RecordUpload(OptimumGuiMetrics.StartTimestamp(), width: 20, height: 10, allocatesTexture: false);
            OptimumGuiMetrics.RecordGpuCandidateTexture(succeeded: true, fallbackReason: null);
            OptimumGuiMetrics.RecordGpuCandidateTexture(succeeded: false, fallbackReason: "test-fallback");
            OptimumGuiMetrics.RecordGpuRecordingSurfaceStart();
            OptimumGuiMetrics.RecordTextExtentMeasurement(OptimumGuiMetrics.StartTimestamp());
            OptimumGuiMetrics.RecordFontExtentMeasurement(OptimumGuiMetrics.StartTimestamp());

            OptimumGuiMetricsSnapshot snapshot = OptimumGuiMetrics.Snapshot();

            Assert.Equal(2, snapshot.CompositionCount);
            Assert.True(snapshot.CompositionTicks > 0);
            Assert.True(snapshot.MaxCompositionTicks > 0);
            Assert.Equal(20 * 10 * 4 + 30 * 12 * 4, snapshot.ComposedSurfaceBytes);
            Assert.Equal(30, snapshot.LastSurfaceWidth);
            Assert.Equal(12, snapshot.LastSurfaceHeight);
            Assert.Equal(1, snapshot.InitialCompositionCount);
            Assert.Equal(1, snapshot.RecompositionCount);
            Assert.Equal(2, snapshot.UploadCount);
            Assert.True(snapshot.UploadTicks > 0);
            Assert.Equal(20 * 10 * 4 * 2, snapshot.UploadBytes);
            Assert.Equal(1, snapshot.TextureAllocationCount);
            Assert.Equal(1, snapshot.TextureUpdateCount);
            Assert.Equal(2, snapshot.GpuCandidateAttemptCount);
            Assert.Equal(1, snapshot.GpuCandidateSuccessCount);
            Assert.Equal(1, snapshot.GpuCandidateFallbackCount);
            Assert.Equal(1, snapshot.GpuRecordingSurfaceStartCount);
            Assert.Equal("test-fallback", snapshot.LastGpuCandidateFallbackReason);
            Assert.Equal(Environment.CurrentManagedThreadId, snapshot.LastCompositionThreadId);
            Assert.Equal(1, snapshot.TextExtentMeasurementCount);
            Assert.True(snapshot.TextExtentMeasurementTicks > 0);
            Assert.Equal(1, snapshot.FontExtentMeasurementCount);
            Assert.True(snapshot.FontExtentMeasurementTicks > 0);
            Assert.Equal(Environment.CurrentManagedThreadId, snapshot.LastTextMeasurementThreadId);

            OptimumGuiMetrics.Reset();
            Assert.Equal(0, OptimumGuiMetrics.Snapshot().CompositionCount);
            Assert.Equal(0, OptimumGuiMetrics.Snapshot().UploadCount);
            Assert.Equal(0, OptimumGuiMetrics.Snapshot().GpuCandidateAttemptCount);
            Assert.Equal(0, OptimumGuiMetrics.Snapshot().GpuRecordingSurfaceStartCount);
            Assert.Equal(0, OptimumGuiMetrics.Snapshot().TextExtentMeasurementCount);
            Assert.Equal(0, OptimumGuiMetrics.Snapshot().FontExtentMeasurementCount);
        }
        finally
        {
            OptimumGuiMetrics.Reset();
            OptimumGuiMetrics.Enabled = wasEnabled;
        }
    }
}
