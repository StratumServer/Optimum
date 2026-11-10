using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

public class ColorUtilNonAllocatingOverloadTests
{
    [Fact]
    public void OptimumApiBridge_IncandescenceAndColorFloats_MatchExpectedValues()
    {
        OptimumApiBridge.GetIncandescenceColorFloats(800, out float r, out float g, out float b);
        Assert.True(r > 0f);
        Assert.Equal(0f, g);
        Assert.Equal(0f, b);

        OptimumApiBridge.GetIncandescenceColorFloats(800, out float r4, out float g4, out float b4, out float a4);
        Assert.Equal(r, r4);
        Assert.True(a4 > 0f);

        OptimumApiBridge.ToRGBAFloats(unchecked((int)0xFF112233), out float cr, out float cg, out float cb, out float ca);
        Assert.Equal(0x11 / 255f, cr, 4);
        Assert.Equal(0x22 / 255f, cg, 4);
        Assert.Equal(0x33 / 255f, cb, 4);
        Assert.Equal(1f, ca, 4);
    }
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
