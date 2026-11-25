using System;
using System.Buffers.Binary;
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

    [Theory]
    [InlineData("", "Arial", 0u)]
    [InlineData("   ", "Arial", 0u)]
    [InlineData("Sans", "Arial", 0u)]
    [InlineData("sans-serif", "Arial", 0u)]
    [InlineData("SERIF", "Times New Roman", 0u)]
    [InlineData("monospace", "Courier New", 0u)]
    [InlineData("cursive", null, 0x40u)]
    [InlineData("fantasy", null, 0x50u)]
    public void GenericWindowsAliasesMapLikeCairo(string family, string? expectedFixed, uint expectedPitchAndFamily)
    {
        bool matched = SkiaFontResolver.TryResolveWindowsGenericFamily(family, out string? fixedFamily, out uint pitchAndFamily, out _);
        Assert.True(matched);
        Assert.Equal(expectedFixed, fixedFamily);
        Assert.Equal(expectedPitchAndFamily, pitchAndFamily);
    }

    [Theory]
    [InlineData("First Order Plain")]
    [InlineData("Calibri Light")]
    [InlineData("Segoe UI Variable")]
    public void ConcreteFamiliesAreNotTreatedAsGenericAliases(string family)
    {
        Assert.False(SkiaFontResolver.TryResolveWindowsGenericFamily(family, out _, out _, out _));
    }

    [Theory]
    [InlineData("Segoe UI Variable", "Arial", true)]
    [InlineData("Sitka", "Arial", true)]
    [InlineData("Source Sans 3", "Arial", true)]
    [InlineData("Arial Black", "Arial Black", false)]
    [InlineData("Arial Black", "arial black", false)]
    [InlineData("sans-serif", "Arial", false)]
    [InlineData("serif", "Times New Roman", false)]
    [InlineData("monospace", "Courier New", false)]
    [InlineData("cursive", "Comic Sans MS", false)]
    [InlineData("fantasy", "Impact", false)]
    [InlineData("", "Arial", false)]
    public void DirectWriteFallbackRunsOnlyWhenGdiSubstitutedConcreteFamily(string requested, string gdiFace, bool expected)
    {
        Assert.Equal(expected, SkiaFontResolver.ShouldTryDirectWriteRequestedFamily(requested, gdiFace));
    }

    [Theory]
    [InlineData("Bahnschrift Condensed", SKFontStyleWidth.Condensed)]
    [InlineData("Bahnschrift SemiCondensed", SKFontStyleWidth.SemiCondensed)]
    [InlineData("Bahnschrift Semi Condensed", SKFontStyleWidth.SemiCondensed)]
    [InlineData("Noto Sans ExtraCondensed", SKFontStyleWidth.ExtraCondensed)]
    [InlineData("Roboto UltraCondensed", SKFontStyleWidth.UltraCondensed)]
    [InlineData("DejaVu Sans Expanded", SKFontStyleWidth.Expanded)]
    [InlineData("Inter SemiExpanded", SKFontStyleWidth.SemiExpanded)]
    [InlineData("Bahnschrift Light", SKFontStyleWidth.Normal)]
    [InlineData("", SKFontStyleWidth.Normal)]
    public void WidthInferenceMatchesNamedInstanceKeywords(string faceName, SKFontStyleWidth expected)
    {
        Assert.Equal(expected, SkiaFontResolver.InferFontStyleWidth(faceName));
    }

    [Fact]
    public void TtcFaceIndexMatchesExactCollectionMemberAndRejectsMalformedHeaders()
    {
        byte[] face0 = BuildSfntDirectory(0x00010000u, (0x68656164u, 0x11111111u, 100u, 54u), (0x6E616D65u, 0x22222222u, 200u, 80u));
        byte[] face1 = BuildSfntDirectory(0x00010000u, (0x68656164u, 0x33333333u, 300u, 54u), (0x66766172u, 0x44444444u, 400u, 64u));
        byte[] face2 = BuildSfntDirectory(0x4F54544Fu, (0x43464620u, 0x55555555u, 500u, 96u));
        byte[] ttc = BuildTtc(face0, face1, face2);

        Assert.True(SkiaFontResolver.TryFindTtcFaceIndex(ttc, face0, out int index0));
        Assert.Equal(0, index0);
        Assert.True(SkiaFontResolver.TryFindTtcFaceIndex(ttc, face1, out int index1));
        Assert.Equal(1, index1);
        Assert.True(SkiaFontResolver.TryFindTtcFaceIndex(ttc, face2, out int index2));
        Assert.Equal(2, index2);

        byte[] unknownFace = BuildSfntDirectory(0x00010000u, (0x68656164u, 0x99999999u, 900u, 54u));
        Assert.False(SkiaFontResolver.TryFindTtcFaceIndex(ttc, unknownFace, out _));

        byte[] badMagic = (byte[])ttc.Clone();
        badMagic[0] = (byte)'x';
        Assert.False(SkiaFontResolver.TryFindTtcFaceIndex(badMagic, face0, out _));

        Assert.False(SkiaFontResolver.TryFindTtcFaceIndex(ttc.AsSpan(0, 8), face0, out _));

        Assert.False(SkiaFontResolver.HasSfntTable(face0, 0x66766172u));
        Assert.True(SkiaFontResolver.HasSfntTable(face1, 0x66766172u));
        Assert.True(SkiaFontResolver.TryGetSfntDirectoryLength(face2, out int dir2Len));
        Assert.Equal(face2.Length, dir2Len);
        Assert.False(SkiaFontResolver.TryGetSfntDirectoryLength(new byte[12], out _));
    }

    [Fact]
    public void WindowsItalicOnSingleWeightGdiFaceRetainsFaceAndSyntheticSlant()
    {
        if (!OperatingSystem.IsWindows()) return;

        using SKTypeface resolved = SkiaFontResolver.Resolve("Arial Black", SKFontStyleWeight.Normal, SKFontStyleSlant.Italic, out _, out _, out _);
        Assert.Equal("Arial Black", SkiaFontResolver.GdiFaceNameOf(resolved));
        Assert.True(resolved.FontWeight >= (int)SKFontStyleWeight.ExtraBold);
        Assert.True(SkiaFontResolver.NeedsSyntheticStyle(resolved, out bool embolden, out bool oblique));
        Assert.False(embolden);
        Assert.True(oblique);
        SkiaFontResolver.GetGdiFaceStyle(resolved, out string gdiFamily, out int gdiWeight, out byte gdiItalic);
        Assert.Equal("Arial Black", gdiFamily);
        Assert.Equal(400, gdiWeight);
        Assert.Equal((byte)1, gdiItalic);
    }

    [Fact]
    public void WindowsGenericSerifAndMonospaceDoNotCollapseToArial()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal("Times New Roman", SkiaFontResolver.ResolveWindowsFamilyName("serif"));
        Assert.Equal("Courier New", SkiaFontResolver.ResolveWindowsFamilyName("monospace"));
        using SKTypeface serif = SkiaFontResolver.Resolve("serif", SKFontStyleWeight.Normal, SKFontStyleSlant.Upright, out _, out _, out _);
        using SKTypeface mono = SkiaFontResolver.Resolve("monospace", SKFontStyleWeight.Normal, SKFontStyleSlant.Upright, out _, out _, out _);
        Assert.Equal("Times New Roman", serif.FamilyName);
        Assert.Equal("Courier New", mono.FamilyName);
    }

    [Fact]
    public void WindowsTtcVariableAndSubfamilyFontsResolveWithoutSegoeUiFallback()
    {
        if (!OperatingSystem.IsWindows()) return;

        // DirectWrite typographic family where GDI only registers optical subfamilies.
        using SKTypeface? directSitka = SKTypeface.FromFamilyName("Sitka", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        if (directSitka != null && string.Equals(directSitka.FamilyName, "Sitka", StringComparison.OrdinalIgnoreCase))
        {
            using SKTypeface resolvedSitka = SkiaFontResolver.Resolve("Sitka", SKFontStyleWeight.Normal, SKFontStyleSlant.Upright, out _, out _, out _);
            Assert.Equal("Sitka", resolvedSitka.FamilyName);
        }

        // GDI-only subfamily face requested in Bold (previously discarded and replaced with Segoe UI).
        if (string.Equals(SkiaFontResolver.ResolveWindowsFamilyName("Calibri Light"), "Calibri Light", StringComparison.OrdinalIgnoreCase))
        {
            using SKTypeface resolvedLightBold = SkiaFontResolver.Resolve("Calibri Light", SKFontStyleWeight.Bold, SKFontStyleSlant.Upright, out _, out _, out _);
            Assert.Equal("Calibri Light", SkiaFontResolver.GdiFaceNameOf(resolvedLightBold));
            Assert.Equal("Calibri", resolvedLightBold.FamilyName);
        }

        // TTC collection member whose GDI name is not a DirectWrite family.
        if (string.Equals(SkiaFontResolver.ResolveWindowsFamilyName("Yu Gothic Light"), "Yu Gothic Light", StringComparison.OrdinalIgnoreCase))
        {
            using SKTypeface resolvedTtc = SkiaFontResolver.Resolve("Yu Gothic Light", SKFontStyleWeight.Normal, SKFontStyleSlant.Upright, out _, out _, out _);
            Assert.Equal("Yu Gothic Light", SkiaFontResolver.GdiFaceNameOf(resolvedTtc));
            Assert.NotEqual("Segoe UI", resolvedTtc.FamilyName);
        }

        // Variable font named instance: retain light weight instead of falling back to Segoe UI or default 400.
        if (string.Equals(SkiaFontResolver.ResolveWindowsFamilyName("Bahnschrift Light"), "Bahnschrift Light", StringComparison.OrdinalIgnoreCase))
        {
            using SKTypeface resolvedVar = SkiaFontResolver.Resolve("Bahnschrift Light", SKFontStyleWeight.Normal, SKFontStyleSlant.Upright, out _, out _, out _);
            Assert.Equal("Bahnschrift Light", SkiaFontResolver.GdiFaceNameOf(resolvedVar));
            Assert.Equal("Bahnschrift", resolvedVar.FamilyName);
            Assert.True(resolvedVar.FontWeight <= (int)SKFontStyleWeight.Light, $"Expected light weight for Bahnschrift Light, got {resolvedVar.FontWeight}");
        }
    }

    static byte[] BuildSfntDirectory(uint version, params (uint Tag, uint Checksum, uint Offset, uint Length)[] tables)
    {
        var bytes = new byte[12 + tables.Length * 16];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), version);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), (ushort)tables.Length);
        for (int i = 0; i < tables.Length; i++)
        {
            int pos = 12 + i * 16;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(pos, 4), tables[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(pos + 4, 4), tables[i].Checksum);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(pos + 8, 4), tables[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(pos + 12, 4), tables[i].Length);
        }
        return bytes;
    }

    static byte[] BuildTtc(params byte[][] faces)
    {
        int headerSize = 12 + faces.Length * 4;
        int totalSize = headerSize + faces.Sum(face => face.Length);
        var bytes = new byte[totalSize];
        bytes[0] = (byte)'t';
        bytes[1] = (byte)'t';
        bytes[2] = (byte)'c';
        bytes[3] = (byte)'f';
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 0x00010000u);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), (uint)faces.Length);
        int cursor = headerSize;
        for (int i = 0; i < faces.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12 + i * 4, 4), (uint)cursor);
            faces[i].CopyTo(bytes, cursor);
            cursor += faces[i].Length;
        }
        return bytes;
    }
}
