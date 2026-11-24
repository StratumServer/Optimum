using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

namespace Optimum.Tests;

// Issue #729: vanilla resolves a toy font family through the GDI font mapper, so
// "Arial Black" keeps its heavy face. The Skia route asks DirectWrite for the same
// name, which may not list that family and then falls back to regular Arial.
public sealed class SkiaWindowsGdiFamilyTests
{
    // The font data of Arial Black reports the family "Arial", so glyph advances must
    // be read from GDI under the face name, not the family name, or the text is laid
    // out with Arial's widths.
    [Fact]
    public void GdiLoadedFaceMeasuresWithItsOwnGdiAdvances()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string text = "Craftable";
        const int size = 24;
        int[] gdi = GdiAdvances("Arial Black", size, text);
        int[] black = SkiaAdvances("Arial Black", size, text);
        int[] regular = SkiaAdvances("Arial", size, text);
        string diagnostics = $"gdi={string.Join(",", gdi)} skia={string.Join(",", black)} arial={string.Join(",", regular)}";
        Console.WriteLine("#729 advances " + diagnostics);

        Assert.True(gdi.Sum() > 0, diagnostics);
        Assert.Equal(gdi, black);
        Assert.NotEqual(regular, black);
    }

    static int[] SkiaAdvances(string family, int size, string text)
    {
        using SKTypeface typeface = SkiaFontResolver.Resolve(family, SKFontStyleWeight.Normal, SKFontStyleSlant.Upright, out _, out _, out _);
        using var font = new SKFont(typeface, size) { ScaleX = 1f, Hinting = SKFontHinting.Normal, Subpixel = true };
        Assert.True(SkiaFontResolver.TryGetWindowsGlyphAdvances(font, font.GetGlyphs(text), out float[] advances));
        return advances.Select(advance => (int)advance).ToArray();
    }

    static int[] GdiAdvances(string face, int size, string text)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr font = CreateFontW(-size, 0, 0, 0, 400, 0, 0, 0, 1, 4, 0, 5, 0, face);
        IntPtr previous = SelectObject(dc, font);
        try
        {
            return text.Select(character =>
            {
                int[] width = new int[1];
                Assert.True(GetCharWidth32W(dc, character, character, width));
                return width[0];
            }).ToArray();
        }
        finally
        {
            SelectObject(dc, previous);
            DeleteObject(font);
            DeleteDC(dc);
        }
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCharWidth32W(IntPtr dc, uint first, uint last, [Out] int[] widths);

    [Theory]
    [InlineData("Arial Black", "Arial", true)]
    [InlineData("Arial Black", null, true)]
    [InlineData("Arial", "arial", false)]
    [InlineData("Segoe UI", "Segoe UI", false)]
    public void GdiFaceDataIsUsedOnlyWhenDirectWriteReturnsAnotherFamily(string gdiFace, string? directWriteFamily, bool expected)
    {
        Assert.Equal(expected, SkiaFontResolver.NeedsGdiFaceData(gdiFace, directWriteFamily!));
    }

    [Theory]
    [InlineData(SKFontStyleWeight.Normal)]
    [InlineData(SKFontStyleWeight.Bold)]
    public void GdiOnlyFamilyKeepsTheFaceGdiSelects(SKFontStyleWeight weight)
    {
        if (!OperatingSystem.IsWindows()) return;

        string gdiFace = SkiaFontResolver.ResolveWindowsFamilyName("Arial Black");
        bool fontFileExists = File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "ariblk.ttf"));
        using SKTypeface? matched = SKFontManager.Default.MatchFamily("Arial Black");
        using SKTypeface? raw = SKTypeface.FromFamilyName(gdiFace, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        using SKTypeface resolved = SkiaFontResolver.Resolve("Arial Black", weight, SKFontStyleSlant.Upright, out _, out _, out _);
        string diagnostics = $"requested={weight} gdiFace='{gdiFace}' ariblk.ttf={fontFileExists} " +
            $"dwriteMatchFamily={(matched == null ? "null" : matched.FamilyName + "/" + matched.FontWeight)} " +
            $"fromFamilyName='{raw?.FamilyName}'/{raw?.FontWeight} resolved='{resolved.FamilyName}'/{resolved.FontWeight}";
        Console.WriteLine("#729 " + diagnostics);

        Assert.True(fontFileExists, diagnostics);
        Assert.Equal("Arial Black", gdiFace);
        Assert.True(resolved.FontWeight >= (int)SKFontStyleWeight.ExtraBold, diagnostics);
    }
}
