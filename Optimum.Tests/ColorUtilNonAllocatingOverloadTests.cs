using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

public class ColorUtilNonAllocatingOverloadTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(37, 180, 92)]
    [InlineData(200, 40, 210)]
    public void RgbToHsvIntsIntoArrayMatchesAllocatingOverload(int r, int g, int b)
    {
        int[] expected = ColorUtil.RgbToHsvInts(r, g, b);

        int[] into = new int[3];
        ColorUtil.RgbToHsvInts(r, g, b, into);

        Assert.Equal(expected, into);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 255, 255)]
    [InlineData(120, 200, 50)]
    [InlineData(43, 128, 200)]
    [InlineData(86, 64, 90)]
    [InlineData(200, 255, 10)]
    public void Hsv2RgbIntsIntoArrayMatchesAllocatingOverload(int h, int s, int v)
    {
        int[] expected = ColorUtil.Hsv2RgbInts(h, s, v);

        int[] into = new int[3];
        ColorUtil.Hsv2RgbInts(h, s, v, into);

        Assert.Equal(expected, into);
    }

    [Fact]
    public void Hsv2RgbIntsIntoArrayReusesBufferAcrossCallsWithoutStaleData()
    {
        // The AmbientManager call site reuses one array across two calls
        // per frame; a leftover value from the first call must not leak
        // into the second.
        int[] scratch = new int[3];

        ColorUtil.Hsv2RgbInts(200, 255, 10, scratch);
        int[] first = (int[])scratch.Clone();

        ColorUtil.Hsv2RgbInts(0, 0, 0, scratch);

        Assert.NotEqual(first, scratch);
        Assert.Equal(new[] { 0, 0, 0 }, scratch);
    }
}
