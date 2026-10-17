using System;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

[CollectionDefinition("OptimumConfig", DisableParallelization = true)]
public sealed class OptimumConfigCollectionDefinition { }

[Collection("OptimumConfig")]
public class OptimumGuiMetricsTests
{
    [Fact]
    public void CandidateFallbackReasonsAreCountedBoundedAndReset()
    {
        OptimumGuiMetrics.Reset();
        try
        {
            OptimumGuiMetrics.RecordGpuCandidateTexture(false, "surface-not-recording:Native");
            OptimumGuiMetrics.RecordGpuCandidateTexture(false, "surface-not-recording:Native");
            OptimumGuiMetrics.RecordGpuCandidateTexture(true, null);
            OptimumGuiMetrics.RecordGpuCandidateTexture(false, "candidate-render-exception:InvalidOperationException:size 640x480");
            OptimumGuiMetrics.RecordGpuCandidateTexture(false, "candidate-render-exception:InvalidOperationException:size 12x9");
            var reasons = OptimumGuiMetrics.GetGpuCandidateFallbackDiagnostics();
            Assert.Equal(2, reasons["surface-not-recording:Native"]);
            Assert.Equal(2, reasons["candidate-render-exception:InvalidOperationException:size #x#"]);
            for (int i = 0; i < 40; i++) OptimumGuiMetrics.RecordGpuCandidateTexture(false, "reason-" + (char)('a' + i % 26) + (char)('a' + i / 26));
            reasons = OptimumGuiMetrics.GetGpuCandidateFallbackDiagnostics();
            Assert.Equal(33, reasons.Count); // 32 kinds plus the overflow bucket
            Assert.Equal(10, reasons["other-fallback-reasons"]);
            Assert.True(OptimumGuiMetrics.NormalizeFallbackReason(new string('x', 500)).Length <= 160);
        }
        finally { OptimumGuiMetrics.Reset(); }
        Assert.Empty(OptimumGuiMetrics.GetGpuCandidateFallbackDiagnostics());
    }

    [Fact]
    public void SuccessfulSubpixelFallbackKeepsSkiaCertificationEvenWhenMetricsAreDisabled()
    {
        bool originalRequest = OptimumConfig.GuiGpuRenderEnabled;
        bool originalMetrics = OptimumGuiMetrics.Enabled;
        try
        {
            OptimumConfig.GuiGpuRenderEnabled = true;
            OptimumGuiMetrics.Enabled = false;
            OptimumConfig.SetGuiGpuCertificationPassed(true, "vendor", "renderer", "4.6", 4, 6, 2, 0);
            Assert.True(OptimumConfig.GuiGpuRecordingEligible);

            OptimumGuiMetrics.RecordMaterialization(OptimumGuiMetrics.SupportedGuiNativeFallback, succeeded: true);

            Assert.True(OptimumConfig.GuiGpuRecordingEligible);
            Assert.True(OptimumConfig.GuiGpuDeviceMatches("vendor", "renderer", "4.6", 4, 6, 2, 0));
        }
        finally
        {
            OptimumConfig.RevokeGuiGpuCertification();
            OptimumGuiMetrics.Enabled = originalMetrics;
            OptimumConfig.GuiGpuRenderEnabled = originalRequest;
        }
    }

    [Theory]
    [InlineData("Context.SelectFontFace", true)]
    [InlineData("Recorder.ShowText.Unsupported:text-antialias-Subpixel", false)]
    public void UnsupportedOrFailedNativeMaterializationRevokesSkiaCertification(string trigger, bool succeeded)
    {
        bool originalRequest = OptimumConfig.GuiGpuRenderEnabled;
        bool originalMetrics = OptimumGuiMetrics.Enabled;
        try
        {
            OptimumConfig.GuiGpuRenderEnabled = true;
            OptimumGuiMetrics.Enabled = false;
            OptimumConfig.SetGuiGpuCertificationPassed(true, "vendor", "renderer", "4.6", 4, 6, 2, 0);

            OptimumGuiMetrics.RecordMaterialization(trigger, succeeded);

            Assert.False(OptimumConfig.GuiGpuRecordingEligible);
            Assert.False(OptimumConfig.GuiGpuDeviceMatches("vendor", "renderer", "4.6", 4, 6, 2, 0));
        }
        finally
        {
            OptimumConfig.RevokeGuiGpuCertification();
            OptimumGuiMetrics.Enabled = originalMetrics;
            OptimumConfig.GuiGpuRenderEnabled = originalRequest;
        }
    }

    [Fact]
    public void NativeMaterializationRevokesApprovalWhileGpuRequestIsTemporarilyDisabled()
    {
        bool originalRequest = OptimumConfig.GuiGpuRenderEnabled;
        bool originalMetrics = OptimumGuiMetrics.Enabled;
        try
        {
            OptimumConfig.GuiGpuRenderEnabled = false;
            OptimumGuiMetrics.Enabled = false;
            OptimumConfig.SetGuiGpuCertificationPassed(true, "vendor", "renderer", "4.6", 4, 6, 2, 0);

            OptimumGuiMetrics.RecordMaterialization("Context.SelectFontFace", succeeded: true);

            OptimumConfig.GuiGpuRenderEnabled = true;
            Assert.False(OptimumConfig.GuiGpuCertificationPassed);
            Assert.False(OptimumConfig.GuiGpuRecordingEligible);
        }
        finally
        {
            OptimumConfig.RevokeGuiGpuCertification();
            OptimumGuiMetrics.Enabled = originalMetrics;
            OptimumConfig.GuiGpuRenderEnabled = originalRequest;
        }
    }

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
