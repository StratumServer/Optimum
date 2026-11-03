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

    [Theory]
    [InlineData("the update")]
    [InlineData("jÁ ll ffi")]
    public void TextExtentsRetainGdiAdvancesWhenReadingSkiaInkBounds(string text)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var options = new FontOptions { Antialias = Antialias.Subpixel, HintMetrics = HintMetrics.On };
        using var font = SkiaGuiFont.Create("sans-serif", FontSlant.Normal, FontWeight.Normal, 14, options);
        Assert.Equal("Arial", font.Typeface.FamilyName);
        ushort[] glyphs = font.GetGlyphs(text);
        Assert.True(SkiaFontResolver.TryGetWindowsGlyphAdvances(font, glyphs, out float[] advances));
        using var outlined = SkiaHintedFontOutlines.Create(font, new Matrix(1, 0, 0, 1, 0, 0), 0, false);
        Assert.NotNull(outlined);

        float x = 0, left = float.MaxValue, right = float.MinValue;
        for (int i = 0; i < glyphs.Length; i++)
        {
            SKRect bounds = outlined!.GetGlyphBounds(glyphs[i], round: true);
            if (bounds.Width > 0)
            {
                left = Math.Min(left, x + bounds.Left);
                right = Math.Max(right, x + bounds.Right);
            }
            x += advances[i];
        }

        TextExtents actual = SkiaTextMetrics.TextExtents(font, options, text);
        Assert.Equal(x, actual.XAdvance);
        Assert.Equal(left, actual.XBearing);
        Assert.Equal(right - left, actual.Width);
    }
}
