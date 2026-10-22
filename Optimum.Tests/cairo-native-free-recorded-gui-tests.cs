using System;
using Cairo;
using SkiaSharp;
using Xunit;

public sealed class CairoNativeFreeRecordedGuiTests
{
    [Fact]
    public void ManagedGuiSurfaceRecordsAndRendersWithoutCairo()
    {
        using var surface = ImageSurface.CreateForRecordedGui(Format.Argb32, 12, 8);
        using (var context = new Context(surface))
        {
            context.SetSourceRGBA(.2, .6, .9, 1);
            context.Rectangle(2, 1, 7, 5);
            context.Fill();
        }

        using var target = new SKBitmap(new SKImageInfo(12, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target)) Assert.True(surface.TryDrawRecordedCommands(canvas));
        Assert.Equal(new SKColor(51, 153, 230, 255), target.GetPixel(4, 3));
        Assert.Equal((byte)0, target.GetPixel(0, 0).Alpha);
        Assert.Equal(SurfaceRecordingState.Sealed, surface.RecordingState);

        bool automaticRecording = Surface.AutomaticRecordingEnabled;
        try
        {
            Surface.ConfigureAutomaticRecording(true);
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _ = surface.Handle);
            Assert.Contains("Cairo materialization is forbidden", exception.Message);
            Assert.Equal(SurfaceRecordingState.Sealed, surface.RecordingState);
        }
        finally
        {
            Surface.ConfigureAutomaticRecording(automaticRecording);
        }
    }

    [Fact]
    public void ManagedGuiSurfaceCompositesImageAndPictureOverlaysWithoutCairo()
    {
        using var surface = ImageSurface.CreateForRecordedGui(Format.Argb32, 10, 6);
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Lime);
        Assert.True(surface.RecordImage(bitmap, 1, 1, 2, 2));

        using var pictureRecorder = new SKPictureRecorder();
        SKCanvas pictureCanvas = pictureRecorder.BeginRecording(SKRect.Create(3, 3));
        pictureCanvas.Clear(SKColors.Transparent);
        pictureCanvas.DrawColor(SKColors.Magenta);
        using SKPicture picture = pictureRecorder.EndRecording();
        Assert.True(surface.RecordPicture(picture, 5, 1, 3, 3, null,
            _ => throw new Xunit.Sdk.XunitException("Skia overlay replay invoked the native Cairo callback.")));

        using var target = new SKBitmap(new SKImageInfo(10, 6, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target)) Assert.True(surface.TryDrawRecordedCommands(canvas));

        Assert.Equal(SKColors.Lime, target.GetPixel(1, 1));
        Assert.Equal(SKColors.Magenta, target.GetPixel(6, 2));
        Assert.Equal((byte)0, target.GetPixel(0, 0).Alpha);
        Assert.Equal(SurfaceRecordingState.Sealed, surface.RecordingState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinearAndRadialGradientsRecordAndRender(bool radial)
    {
        using var surface = ImageSurface.CreateForRecordedGui(Format.Argb32, 12, 6);
        using Pattern gradient = radial
            ? new RadialGradient(6, 3, 0, 6, 3, 6)
            : new LinearGradient(0, 3, 12, 3);
        Assert.Equal(Status.Success, ((Gradient)gradient).AddColorStop(0, new Color(1, 0, 0, 1)));
        Assert.Equal(Status.Success, ((Gradient)gradient).AddColorStop(1, new Color(0, 0, 1, 1)));
        gradient.Extend = Extend.Pad;
        gradient.Matrix = new Matrix();
        Assert.Equal(radial ? PatternType.Radial : PatternType.Linear, gradient.PatternType);
        Assert.Equal(2, ((Gradient)gradient).ColorStopCount);
        Assert.True(gradient.Matrix.IsIdentity());
        Assert.Equal(CairoNativeGuard.Mode == "deny", !gradient.HandleValid);

        using (var context = new Context(surface))
        {
            context.SetSource(gradient);
            if (CairoNativeGuard.Mode == "deny")
            {
                ((Gradient)gradient).AddColorStop(.5, new Color(0, 1, 0, 1));
                gradient.Matrix = new Matrix(1, 0, 0, 1, 1000, 1000);
            }
            context.Rectangle(1, 1, 10, 4);
            context.Fill();
        }

        if (CairoNativeGuard.Mode == "deny")
            Assert.Throws<InvalidOperationException>(() => _ = gradient.Handle);
        gradient.Dispose();

        using var target = new SKBitmap(new SKImageInfo(12, 6, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target)) Assert.True(surface.TryDrawRecordedCommands(canvas));

        SKColor first = target.GetPixel(radial ? 6 : 2, 3);
        SKColor last = target.GetPixel(radial ? 10 : 9, 3);
        Assert.True(first.Red > first.Blue, $"Expected the first gradient sample to be red-dominant, got {first}.");
        Assert.True(last.Blue > last.Red, $"Expected the last gradient sample to be blue-dominant, got {last}.");
        Assert.Equal((byte)0, target.GetPixel(0, 0).Alpha);
        Assert.Equal(SurfaceRecordingState.Sealed, surface.RecordingState);
    }
}
