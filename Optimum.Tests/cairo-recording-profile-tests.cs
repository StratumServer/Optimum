using System;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

[Collection("GUI metrics")]
public sealed class CairoRecordingProfileTests
{
    static CairoRecordingProfileTests() { _ = CairoAPI.Version; }

    [Fact]
    public void ProfilingPreservesRecordedPixelsAndStateAndIsIndependentlyDisabled()
    {
        bool original = SurfaceRecordingDiagnostics.ProfilingEnabled;
        bool originalReuse = RecordedPreparationCache.Enabled;
        RecordedPreparationCache.Enabled = false;
        try
        {
            SurfaceRecordingDiagnostics.ProfilingEnabled = false;
            SurfaceRecordingDiagnostics.ResetProfiling();
            byte[] disabled = Draw(out var disabledPoint);
            AssertZero();

            SurfaceRecordingDiagnostics.ProfilingEnabled = true;
            SurfaceRecordingDiagnostics.ResetProfiling();
            byte[] enabled = Draw(out var enabledPoint);
            Assert.Equal(disabled, enabled);
            Assert.Equal(disabledPoint.X, enabledPoint.X);
            Assert.Equal(disabledPoint.Y, enabledPoint.Y);
            Assert.Equal(1, SurfaceRecordingDiagnostics.ShadowCreations);
            Assert.True(SurfaceRecordingDiagnostics.ShadowCommands > 0);
            Assert.True(SurfaceRecordingDiagnostics.ShadowAccesses > 0);
            Assert.Equal(2, SurfaceRecordingDiagnostics.DrawingCaptures);
            // One clip, one fill and one stroke. Each copies and converts its path once.
            Assert.Equal(3, SurfaceRecordingDiagnostics.PathCopies);
            Assert.Equal(3, SurfaceRecordingDiagnostics.PathConversions);
            Assert.Equal(1, SurfaceRecordingDiagnostics.StrokeOutlines);
            Assert.True(SurfaceRecordingDiagnostics.ShadowTicks > 0);
            Assert.True(SurfaceRecordingDiagnostics.DrawingCaptureTicks >= SurfaceRecordingDiagnostics.StrokeOutlineTicks);
            Assert.True(SurfaceRecordingDiagnostics.PathCopyTicks >= SurfaceRecordingDiagnostics.PathConversionTicks);

            SurfaceRecordingDiagnostics.ResetProfiling();
            Assert.True(SurfaceRecordingDiagnostics.ProfilingEnabled);
            AssertZero();
            SurfaceRecordingDiagnostics.ProfilingEnabled = false;
            _ = Draw(out _);
            AssertZero();
        }
        finally
        {
            RecordedPreparationCache.Enabled = originalReuse;
            SurfaceRecordingDiagnostics.ProfilingEnabled = original;
            SurfaceRecordingDiagnostics.ResetProfiling();
        }
    }

    [Fact]
    public void PaintWithoutGeometryDoesNotReportAPathCopy()
    {
        bool original = SurfaceRecordingDiagnostics.ProfilingEnabled;
        bool originalReuse = RecordedPreparationCache.Enabled;
        RecordedPreparationCache.Enabled = false;
        try
        {
            SurfaceRecordingDiagnostics.ProfilingEnabled = true;
            SurfaceRecordingDiagnostics.ResetProfiling();
            using var surface = new ImageSurface(Format.Argb32, 8, 8);
            surface.BeginRecording();
            using var context = new Context(surface);
            context.SetSourceRGBA(1, 0, 0, 1);
            context.Paint();
            Assert.Equal(1, SurfaceRecordingDiagnostics.DrawingCaptures);
            Assert.Equal(0, SurfaceRecordingDiagnostics.PathCopies);
            Assert.Equal(0, SurfaceRecordingDiagnostics.PathConversions);
            Assert.Equal(0, SurfaceRecordingDiagnostics.StrokeOutlines);
        }
        finally
        {
            RecordedPreparationCache.Enabled = originalReuse;
            SurfaceRecordingDiagnostics.ProfilingEnabled = original;
            SurfaceRecordingDiagnostics.ResetProfiling();
        }
    }

    static byte[] Draw(out PointD point)
    {
        using var surface = new ImageSurface(Format.Argb32, 32, 24);
        surface.BeginRecording();
        using (var context = new Context(surface))
        {
            context.Antialias = Antialias.None;
            context.Rectangle(0, 0, 24, 24); context.Clip();
            context.SetSourceRGBA(1, 0, 0, 1);
            context.Rectangle(2, 2, 12, 8); context.Fill();
            context.SetSourceRGBA(0, 1, 0, 1); context.LineWidth = 2;
            context.MoveTo(2, 15); context.LineTo(20, 15); context.StrokePreserve();
            point = context.CurrentPoint;
        }
        using var bitmap = new SKBitmap(32, 24, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap)) Assert.True(surface.TryDrawRecordedCommands(canvas));
        Assert.Equal(SurfaceRecordingState.Sealed, surface.RecordingState);
        var pixels = new byte[32 * 24 * 4];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }

    static void AssertZero()
    {
        foreach (long value in SurfaceRecordingDiagnostics.StageDiagnostics.Values)
            Assert.Equal(0, value);
        foreach (long value in new[] {
            SurfaceRecordingDiagnostics.ShadowAccesses, SurfaceRecordingDiagnostics.ShadowTicks,
            SurfaceRecordingDiagnostics.ShadowCreations, SurfaceRecordingDiagnostics.ShadowCommands,
            SurfaceRecordingDiagnostics.DrawingCaptures, SurfaceRecordingDiagnostics.DrawingCaptureTicks,
            SurfaceRecordingDiagnostics.PathCopies, SurfaceRecordingDiagnostics.PathCopyTicks,
            SurfaceRecordingDiagnostics.PathConversions, SurfaceRecordingDiagnostics.PathConversionTicks,
            SurfaceRecordingDiagnostics.StrokeOutlines, SurfaceRecordingDiagnostics.StrokeOutlineTicks })
            Assert.Equal(0, value);
    }

    [Fact]
    public void DetailedTimingsPreserveNativeTextMetricsCurrentPointAndReplayPixels()
    {
        bool original = SurfaceRecordingDiagnostics.ProfilingEnabled;
        bool originalReuse = RecordedPreparationCache.Enabled;
        try
        {
            RecordedPreparationCache.Enabled = true;
            RecordedPreparationCache.Clear();
            SurfaceRecordingDiagnostics.ProfilingEnabled = false;
            SurfaceRecordingDiagnostics.ResetProfiling();
            var expected = DrawText();
            AssertZero();
            RecordedPreparationCache.Clear();
            SurfaceRecordingDiagnostics.ProfilingEnabled = true;
            SurfaceRecordingDiagnostics.ResetProfiling();
            var actual = DrawText();
            Assert.Equal(expected.Pixels, actual.Pixels);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.Font, actual.Font);
            Assert.Equal(expected.Point.X, actual.Point.X);
            Assert.Equal(expected.Point.Y, actual.Point.Y);
            var stages = SurfaceRecordingDiagnostics.StageDiagnostics;
            foreach (string stage in new[] { "CaptureState", "SourceCapture", "TextPreparation", "TextMeasurement", "FontSelection" })
            {
                Assert.True(stages[stage + "Count"] > 0, stage);
                Assert.True(stages[stage + "Ticks"] > 0, stage);
            }
            Assert.Equal(0, stages["GpuBlurCount"]);
            Assert.Equal(0, stages["GpuSubmissionCount"]);
            SurfaceRecordingDiagnostics.ResetProfiling();
            Assert.True(SurfaceRecordingDiagnostics.ProfilingEnabled);
            AssertZero();
        }
        finally
        {
            RecordedPreparationCache.Clear();
            RecordedPreparationCache.Enabled = originalReuse;
            SurfaceRecordingDiagnostics.ProfilingEnabled = original;
            SurfaceRecordingDiagnostics.ResetProfiling();
        }
    }

    static (byte[] Pixels, TextExtents Text, FontExtents Font, PointD Point) DrawText()
    {
        using var surface = new ImageSurface(Format.Argb32, 96, 48);
        surface.BeginRecording();
        using var context = new Context(surface);
        context.SelectFontFace("Arial", FontSlant.Normal, FontWeight.Normal);
        context.SetFontSize(13);
        context.MoveTo(4, 22);
        var text = context.TextExtents("profile Ág");
        var font = context.FontExtents;
        context.SetSourceRGBA(.2, .5, .7, .8);
        context.ShowText("profile Ág");
        var point = context.CurrentPoint;
        return (surface.Data, text, font, point);
    }
}
