using System;
using System.Linq;
using Cairo;
using SkiaSharp;
using Xunit;

namespace Optimum.Tests;

public sealed class SkiaWindowsFontParityTests
{
    [Theory]
    [InlineData(16, "9,9,7,4,9,4,9,5,9,7,9,9,9")]
    [InlineData(24, "14,13,11,7,13,7,14,8,13,11,13,14,13")]
    [InlineData(32, "19,17,14,9,17,9,17,11,17,15,17,17,17")]
    public void GenericSansSerifMatchesWindowsCairoGlyphAdvances(int size, string expectedAdvancesCsv)
    {
        if (!OperatingSystem.IsWindows()) return;

        using SKTypeface typeface = SkiaFontResolver.Resolve("sans-serif", SKFontStyleWeight.Normal,
            SKFontStyleSlant.Upright, out _, out _, out _);
        Assert.Equal("Arial", typeface.FamilyName);

        using var font = new SKFont(typeface, size) { ScaleX = 1f, Hinting = SKFontHinting.Normal, Subpixel = true };
        ushort[] glyphs = font.GetGlyphs("Texto gravado");
        Assert.True(SkiaFontResolver.TryGetWindowsGlyphAdvances(font, glyphs, out float[] advances));

        int[] expected = expectedAdvancesCsv.Split(',').Select(int.Parse).ToArray();
        Assert.Equal(expected, advances.Select(value => (int)value).ToArray());
		Assert.Equal((float)expected.Sum(), advances.Sum());
    }
}
