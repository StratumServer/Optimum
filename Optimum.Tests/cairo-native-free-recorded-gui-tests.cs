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
}
