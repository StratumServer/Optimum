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
}
