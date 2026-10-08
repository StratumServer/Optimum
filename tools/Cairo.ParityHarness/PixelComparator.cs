using System.Runtime.InteropServices;

namespace Cairo.ParityHarness;

public enum PixelLayout
{
    Bgra8888Premultiplied
}

public sealed record PixelImage(int Width, int Height, int Stride, PixelLayout Layout, byte[] Pixels)
{
    public void Validate()
    {
        if (Width <= 0 || Height <= 0) throw new ArgumentOutOfRangeException(nameof(Width), "Image dimensions must be positive.");
        var minimumStride = checked(Width * 4);
        if (Stride < minimumStride) throw new ArgumentException($"Stride {Stride} is smaller than {minimumStride} bytes.", nameof(Stride));
        if (Pixels.Length != checked(Stride * Height)) throw new ArgumentException("Pixel buffer length must equal stride × height.", nameof(Pixels));
        if (Layout != PixelLayout.Bgra8888Premultiplied) throw new ArgumentOutOfRangeException(nameof(Layout));
    }
}

public sealed record PixelDifference(
    int Width,
    int Height,
    int MaxChannelDelta,
    double RmsChannelError,
    long PixelsOverThreshold,
    long PixelsCompared,
    double RmsAlphaError,
    int? MinX,
    int? MinY,
    int? MaxX,
    int? MaxY,
    int Threshold)
{
    public bool WithinThreshold => PixelsOverThreshold == 0;
}

public static class PixelComparator
{
    public static PixelDifference Compare(PixelImage expected, PixelImage actual, int threshold = 0)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (threshold is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(threshold));
        expected.Validate();
        actual.Validate();
        if (expected.Width != actual.Width || expected.Height != actual.Height)
            throw new ArgumentException("Image dimensions differ.");
        if (expected.Stride != actual.Stride) throw new ArgumentException("Image strides differ.");
        if (expected.Layout != actual.Layout) throw new ArgumentException("Pixel layouts differ.");

        long squared = 0, alphaSquared = 0, over = 0, pixels = (long)expected.Width * expected.Height;
        var max = 0;
        int? minX = null, minY = null, maxX = null, maxY = null;
        for (var y = 0; y < expected.Height; y++)
        for (var x = 0; x < expected.Width; x++)
        {
            var offset = y * expected.Stride + x * 4;
            var pixelMax = 0;
            for (var channel = 0; channel < 4; channel++)
            {
                var delta = Math.Abs(expected.Pixels[offset + channel] - actual.Pixels[offset + channel]);
                pixelMax = Math.Max(pixelMax, delta);
                max = Math.Max(max, delta);
                squared += (long)delta * delta;
                if (channel == 3) alphaSquared += (long)delta * delta;
            }
            if (pixelMax <= threshold) continue;
            over++;
            minX = Math.Min(minX ?? x, x); minY = Math.Min(minY ?? y, y);
            maxX = Math.Max(maxX ?? x, x); maxY = Math.Max(maxY ?? y, y);
        }

        return new PixelDifference(expected.Width, expected.Height, max,
            Math.Sqrt((double)squared / (pixels * 4)), over, pixels,
            Math.Sqrt((double)alphaSquared / pixels), minX, minY, maxX, maxY, threshold);
    }

    /// <summary>Copies native Cairo ARGB32 bytes on little-endian hosts into the explicitly supported BGRA premultiplied layout.</summary>
    public static PixelImage FromCairoArgb32(int width, int height, int stride, byte[] data)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Cairo ARGB32 normalization currently supports little-endian hosts only.");
        var image = new PixelImage(width, height, stride, PixelLayout.Bgra8888Premultiplied, data);
        image.Validate();
        if (stride < checked(width * 4)) throw new ArgumentException("Cairo ARGB32 stride is invalid.", nameof(stride));
        return image;
    }

    public static PixelImage FromSkiaBgraPremultiplied(int width, int height, int stride, byte[] data)
    {
        var image = new PixelImage(width, height, stride, PixelLayout.Bgra8888Premultiplied, data);
        image.Validate();
        return image;
    }
}
