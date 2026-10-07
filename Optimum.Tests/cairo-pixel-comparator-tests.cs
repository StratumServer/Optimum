using System;
using Cairo.ParityHarness;
using Xunit;

namespace Optimum.Tests;

public class CairoPixelComparatorTests
{
    [Fact]
    public void IdenticalImagesHaveZeroErrorAndNoDifferenceBounds()
    {
        var image = Image(2, 2, new byte[16]);

        var result = PixelComparator.Compare(image, image);

        Assert.Equal(0, result.MaxChannelDelta);
        Assert.Equal(0, result.RmsChannelError);
        Assert.Equal(0, result.PixelsOverThreshold);
        Assert.True(result.WithinThreshold);
        Assert.Null(result.MinX);
        Assert.Null(result.MaxY);
    }

    [Fact]
    public void ReportsLocalizedFillBlendTranslationAndAlphaCounterexamples()
    {
        var expected = new byte[3 * 2 * 4];
        var filled = (byte[])expected.Clone();
        filled[0] = 128; // Wrong color/fill at (0,0).
        AssertDifference(PixelComparator.Compare(Image(3, 2, expected), Image(3, 2, filled)), 0, 0, 0, 0);

        var blended = (byte[])expected.Clone();
        blended[3] = 128; // Wrong blend/alpha at (0,0).
        var blendResult = PixelComparator.Compare(Image(3, 2, expected), Image(3, 2, blended));
        Assert.Equal(128, blendResult.MaxChannelDelta);
        Assert.Equal(128 / Math.Sqrt(6), blendResult.RmsAlphaError, 10);

        var premultiplied = new byte[4];
        premultiplied[0] = 64;
        premultiplied[3] = 128;
        var accidentallyUnpremultiplied = new byte[] { 128, 0, 0, 128 };
        Assert.Equal(64, PixelComparator.Compare(Image(1, 1, premultiplied), Image(1, 1, accidentallyUnpremultiplied)).MaxChannelDelta);

        var translated = (byte[])expected.Clone();
        translated[(1 * 3 + 2) * 4 + 2] = 255; // Translation error localized at (2,1).
        AssertDifference(PixelComparator.Compare(Image(3, 2, expected), Image(3, 2, translated)), 2, 1, 2, 1);
    }

    [Fact]
    public void RejectsMismatchedDimensionsStrideAndBufferLength()
    {
        var image = Image(2, 2, new byte[16]);
        Assert.Throws<ArgumentException>(() => PixelComparator.Compare(image, Image(1, 2, new byte[8])));
        Assert.Throws<ArgumentException>(() => PixelComparator.Compare(image, new PixelImage(2, 2, 12, PixelLayout.Bgra8888Premultiplied, new byte[24])));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelComparator.Compare(image, new PixelImage(2, 2, 8, (PixelLayout)1, new byte[16])));
        Assert.Throws<ArgumentException>(() => new PixelImage(2, 2, 8, PixelLayout.Bgra8888Premultiplied, new byte[15]).Validate());
    }

    [Fact]
    public void ThresholdIsAppliedPerPixelAndBoundsOnlyIncludeFailingPixels()
    {
        var expected = new byte[8];
        var actual = (byte[])expected.Clone();
        actual[0] = 2;
        actual[4] = 4;

        var result = PixelComparator.Compare(Image(2, 1, expected), Image(2, 1, actual), threshold: 2);

        Assert.Equal(1, result.PixelsOverThreshold);
        Assert.Equal(1, result.MinX);
        Assert.Equal(1, result.MaxX);
    }

    private static PixelImage Image(int width, int height, byte[] pixels) =>
        new(width, height, width * 4, PixelLayout.Bgra8888Premultiplied, pixels);

    private static void AssertDifference(PixelDifference result, int minX, int minY, int maxX, int maxY)
    {
        Assert.False(result.WithinThreshold);
        Assert.Equal(1, result.PixelsOverThreshold);
        Assert.Equal(minX, result.MinX);
        Assert.Equal(minY, result.MinY);
        Assert.Equal(maxX, result.MaxX);
        Assert.Equal(maxY, result.MaxY);
    }
}
