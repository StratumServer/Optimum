using System;
using System.IO;
using Path = System.IO.Path;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

public sealed class CairoImageSurfaceFormatTests
{
    [Theory]
    [InlineData(-2, 10, 10, Status.InvalidFormat)]
    [InlineData(-1, 10, 10, Status.InvalidFormat)]
    [InlineData(0, 10, 10, Status.Success)]
    [InlineData(1, 10, 10, Status.Success)]
    [InlineData(2, 10, 10, Status.Success)]
    [InlineData(3, 10, 10, Status.Success)]
    [InlineData(4, 10, 10, Status.Success)]
    [InlineData(5, 10, 10, Status.Success)]
    [InlineData(6, 10, 10, Status.Success)]
    [InlineData(7, 10, 10, Status.Success)]
    [InlineData(8, 10, 10, Status.InvalidFormat)]
    [InlineData(9, 10, 10, Status.InvalidFormat)]
    [InlineData(99, 10, 10, Status.InvalidFormat)]
    public void ImageSurfaceConstructorMatchesNativeFormatContract(int formatInt, int width, int height, Status expectedStatus)
    {
        var format = (Format)formatInt;
        using var surface = new ImageSurface(format, width, height);

        Assert.Equal(expectedStatus, surface.Status);
        Assert.Equal(SurfaceType.Image, surface.SurfaceType);

        if (expectedStatus == Status.Success)
        {
            Assert.Equal(width, surface.Width);
            Assert.Equal(height, surface.Height);
            Assert.Equal(format, surface.Format);
            Assert.Equal(Surface.StrideForWidth(format, width), surface.Stride);
            Assert.Equal(1u, surface.ReferenceCount);

            Content expectedContent = formatInt switch
            {
                0 or 7 => Content.ColorAlpha,
                2 or 3 => Content.Alpha,
                _ => Content.Color
            };
            Assert.Equal(expectedContent, surface.Content);

            using var context = new Context(surface);
            Assert.Equal(Status.Success, context.Status);
        }
        else
        {
            Assert.Equal(0, surface.Width);
            Assert.Equal(0, surface.Height);
            Assert.Equal(0, surface.Stride);
            Assert.Equal((Format)(-1), surface.Format);
            Assert.Equal(Content.Color, surface.Content);
            Assert.Equal(0u, surface.ReferenceCount);
            Assert.Null(surface.Data);
            Assert.Equal(IntPtr.Zero, surface.DataPtr);

            using var context = new Context(surface);
            Assert.Equal(expectedStatus, context.Status);
        }

        AssertNoNativeLoad();

        if (CairoNativeGuard.NativeAllowed)
        {
            IntPtr native = NativeMethods.cairo_image_surface_create(format, width, height);
            try
            {
                Assert.Equal(NativeMethods.cairo_surface_status(native), surface.Status);
                Assert.Equal(NativeMethods.cairo_image_surface_get_width(native), surface.Width);
                Assert.Equal(NativeMethods.cairo_image_surface_get_height(native), surface.Height);
                Assert.Equal(NativeMethods.cairo_image_surface_get_stride(native), surface.Stride);
                Assert.Equal(NativeMethods.cairo_image_surface_get_format(native), surface.Format);
                Assert.Equal(NativeMethods.cairo_surface_get_content(native), surface.Content);
                Assert.Equal(NativeMethods.cairo_surface_get_type(native), surface.SurfaceType);
                Assert.Equal(NativeMethods.cairo_surface_get_reference_count(native), surface.ReferenceCount);
            }
            finally
            {
                NativeMethods.cairo_surface_destroy(native);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void DataSurfaceConstructorSupportsFormats0To7(int formatInt)
    {
        var format = (Format)formatInt;
        int width = 8;
        int height = 4;
        int stride = Surface.StrideForWidth(format, width);
        byte[] data = new byte[stride * height];

        using var surface = new ImageSurface(data, format, width, height, stride);
        Assert.Equal(Status.Success, surface.Status);
        Assert.Equal(width, surface.Width);
        Assert.Equal(height, surface.Height);
        Assert.Equal(stride, surface.Stride);
        Assert.Equal(format, surface.Format);
        AssertNoNativeLoad();
    }

    [Theory]
    [InlineData(5, -1, Status.InvalidStride)]
    [InlineData(5, 31, Status.InvalidStride)]
    [InlineData(6, 3, Status.InvalidStride)]
    [InlineData(7, 10, Status.InvalidStride)]
    [InlineData(-1, 32, Status.InvalidFormat)]
    [InlineData(8, 32, Status.InvalidFormat)]
    public void DataSurfaceConstructorValidatesStrideAndFormat(int formatInt, int stride, Status expectedStatus)
    {
        var format = (Format)formatInt;
        int width = 8;
        int height = 4;
        byte[] data = new byte[1024];

        using var surface = new ImageSurface(data, format, width, height, stride);
        Assert.Equal(expectedStatus, surface.Status);
    }

    [Fact]
    public void Format5Rgb30PixelDrawingAndBitPackingParity()
    {
        var format = (Format)5;
        int width = 4;
        int height = 4;

        using var surface = new ImageSurface(format, width, height);
        using (var context = new Context(surface))
        {
            context.SetSourceRGB(1.0, 0.5, 0.25);
            context.Paint();
        }

        byte[] pixels = surface.Data;
        Assert.NotNull(pixels);
        Assert.Equal(Surface.StrideForWidth(format, width) * height, pixels.Length);

        // Check each 32-bit pixel: B=0..9 (approx 256), G=10..19 (approx 512), R=20..29 (1023), 30..31=0
        for (int i = 0; i < width * height; i++)
        {
            uint val = BitConverter.ToUInt32(pixels, i * 4);
            uint b = val & 0x3ff;
            uint g = (val >> 10) & 0x3ff;
            uint r = (val >> 20) & 0x3ff;
            uint unused = (val >> 30) & 0x3;

            Assert.Equal(0u, unused);
            Assert.True(r >= 1020 && r <= 1023, $"Red component out of expected range: {r}");
            Assert.True(g >= 510 && g <= 514, $"Green component out of expected range: {g}");
            Assert.True(b >= 254 && b <= 258, $"Blue component out of expected range: {b}");
        }

        AssertNoNativeLoad();
    }

    [Fact]
    public void Format6Rgb96FPixelDrawingParity()
    {
        var format = (Format)6;
        int width = 2;
        int height = 2;

        using var surface = new ImageSurface(format, width, height);
        using (var context = new Context(surface))
        {
            context.SetSourceRGB(1.0, 0.5, 0.25);
            context.Paint();
        }

        byte[] pixels = surface.Data;
        Assert.NotNull(pixels);
        Assert.Equal(Surface.StrideForWidth(format, width) * height, pixels.Length);

        for (int i = 0; i < width * height; i++)
        {
            float r = BitConverter.ToSingle(pixels, i * 12);
            float g = BitConverter.ToSingle(pixels, i * 12 + 4);
            float b = BitConverter.ToSingle(pixels, i * 12 + 8);

            Assert.Equal(1.0f, r, 0.01f);
            Assert.Equal(0.5f, g, 0.01f);
            Assert.Equal(0.25f, b, 0.01f);
        }

        AssertNoNativeLoad();
    }

    [Fact]
    public void Format7Rgba128FPixelDrawingParity()
    {
        var format = (Format)7;
        int width = 2;
        int height = 2;

        using var surface = new ImageSurface(format, width, height);
        using (var context = new Context(surface))
        {
            context.SetSourceRGBA(1.0, 0.5, 0.25, 0.8);
            context.Paint();
        }

        byte[] pixels = surface.Data;
        Assert.NotNull(pixels);
        Assert.Equal(Surface.StrideForWidth(format, width) * height, pixels.Length);

        // In Cairo and Skia, RGBA128F color components are premultiplied by alpha
        for (int i = 0; i < width * height; i++)
        {
            float r = BitConverter.ToSingle(pixels, i * 16);
            float g = BitConverter.ToSingle(pixels, i * 16 + 4);
            float b = BitConverter.ToSingle(pixels, i * 16 + 8);
            float a = BitConverter.ToSingle(pixels, i * 16 + 12);

            Assert.Equal(0.8f, a, 0.01f);
            Assert.Equal(0.8f * 1.0f, r, 0.01f);
            Assert.Equal(0.8f * 0.5f, g, 0.01f);
            Assert.Equal(0.8f * 0.25f, b, 0.01f);
        }

        AssertNoNativeLoad();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void WriteToPngSucceedsForFormats0To7(int formatInt)
    {
        var format = (Format)formatInt;
        using var surface = new ImageSurface(format, 8, 8);
        using (var context = new Context(surface))
        {
            context.SetSourceRGB(0.2, 0.6, 0.9);
            context.Paint();
        }

        string tempFile = Path.Combine(Path.GetTempPath(), $"cairo-format-test-{formatInt}-{Guid.NewGuid():N}.png");
        try
        {
            surface.WriteToPng(tempFile);
            Assert.True(File.Exists(tempFile));
            var fileInfo = new FileInfo(tempFile);
            Assert.True(fileInfo.Length > 0, $"PNG file for format {formatInt} was empty.");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }

        AssertNoNativeLoad();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Formats567CanBeUsedAsSourcePattern(int formatInt)
    {
        var format = (Format)formatInt;
        using var source = new ImageSurface(format, 4, 4);
        using (var sourceContext = new Context(source))
        {
            sourceContext.SetSourceRGB(1.0, 0.0, 0.0);
            sourceContext.Paint();
        }

        using var dest = new ImageSurface(Format.Argb32, 4, 4);
        using (var destContext = new Context(dest))
        {
            destContext.SetSourceSurface(source, 0, 0);
            destContext.Paint();
        }

        byte[] destPixels = dest.Data;
        Assert.NotNull(destPixels);
        for (int i = 0; i < 4 * 4; i++)
        {
            // BGRA in little-endian
            byte b = destPixels[i * 4];
            byte g = destPixels[i * 4 + 1];
            byte r = destPixels[i * 4 + 2];
            byte a = destPixels[i * 4 + 3];

            Assert.Equal(255, a);
            Assert.True(r >= 250, $"Red was {r}, expected ~255");
            Assert.True(g <= 5, $"Green was {g}, expected ~0");
            Assert.True(b <= 5, $"Blue was {b}, expected ~0");
        }

        AssertNoNativeLoad();
    }

    static void AssertNoNativeLoad()
    {
        if (!CairoNativeGuard.NativeAllowed && CairoNativeGuard.LoadedLibcairoMappings() is string[] mappings)
            Assert.Empty(mappings);
    }
}
