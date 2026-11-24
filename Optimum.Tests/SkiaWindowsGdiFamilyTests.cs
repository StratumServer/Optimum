using System;
using System.IO;
using Cairo;
using SkiaSharp;
using Xunit;

namespace Optimum.Tests;

// Issue #729: vanilla resolves a toy font family through the GDI font mapper, so
// "Arial Black" keeps its heavy face. The Skia route asks DirectWrite for the same
// name, which may not list that family and then falls back to regular Arial.
public sealed class SkiaWindowsGdiFamilyTests
{
    [Theory]
    [InlineData(SKFontStyleWeight.Normal)]
    [InlineData(SKFontStyleWeight.Bold)]
    public void GdiOnlyFamilyKeepsTheFaceGdiSelects(SKFontStyleWeight weight)
    {
        if (!OperatingSystem.IsWindows()) return;

        string gdiFace = SkiaFontResolver.ResolveWindowsFamilyName("Arial Black");
        bool fontFileExists = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "ariblk.ttf"));
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
