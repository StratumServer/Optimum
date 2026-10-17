using System;
using SkiaSharp;

namespace Cairo
{
    /// <summary>
    /// Skia-backed text measurement for GUI layout. CairoFont keeps its public
    /// return types for binary compatibility, while internal GUI sizing no
    /// longer asks Cairo to measure text.
    /// </summary>
    public static class OptimumSkiaTextMetrics
    {
        public static FontExtents GetFontExtents(string family, FontWeight weight, FontSlant slant, double size)
        {
            if (!(size > 0) || double.IsNaN(size) || double.IsInfinity(size)) return default;
            using var font = CreateFont(family, weight, slant, size);
            SKFontMetrics metrics = font.Metrics;
            double ascent = Math.Max(0, -metrics.Ascent);
            double descent = Math.Max(0, metrics.Descent);
            double leading = Math.Max(0, metrics.Leading);

            return new FontExtents(
                ascent,
                descent,
                ascent + descent + leading,
                Math.Max(0, metrics.MaxCharacterWidth),
                0);
        }

        public static TextExtents GetTextExtents(string family, FontWeight weight, FontSlant slant, double size, string text)
        {
            if (string.IsNullOrEmpty(text) || !(size > 0) || double.IsNaN(size) || double.IsInfinity(size)) return default;

            using var font = CreateFont(family, weight, slant, size);
            int count = font.CountGlyphs(text);
            var glyphs = new ushort[count];
            font.GetGlyphs(text.AsSpan(), glyphs.AsSpan());
            if (count <= 0) return default;

            float advance = font.MeasureText(glyphs.AsSpan(0, count), out SKRect bounds, null);
            var extents = new TextExtents
            {
                XBearing = bounds.Left,
                YBearing = bounds.Top,
                Width = Math.Max(0, bounds.Width),
                Height = Math.Max(0, bounds.Height),
                XAdvance = advance,
                YAdvance = 0
            };
            return extents;
        }

        private static SKFont CreateFont(string family, FontWeight weight, FontSlant slant, double size)
        {
            // Fontconfig can gain bundled fonts after startup. Resolve the same
            // face as recorded text on every request instead of caching a fallback
            // from before registration. SKFont retains its own native face reference.
            using var typeface = RecordedText.MatchCairoTypeface(family ?? string.Empty, slant, weight);

            return new SKFont(typeface ?? SKTypeface.Default, (float)size)
            {
                Edging = SKFontEdging.SubpixelAntialias,
                Subpixel = true,
                Hinting = SKFontHinting.Slight
            };
        }
    }
}
