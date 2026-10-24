using System;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Cairo-shaped text and font extents computed with Skia, so recorded contexts need no native scaled font.</summary>
	internal static class SkiaTextMetrics
	{
		/// <summary>
		/// FreeType's LIGHT target changes raster coverage but uses linear advances, so a Slight
		/// font is measured with a Normal-hinted twin. Returns the font to measure advances with.
		/// </summary>
		internal static bool UsesNormalHintedAdvances(SKFont font, FontOptions options, string text)
			=> OperatingSystem.IsLinux() && options.HintMetrics != HintMetrics.Off && font.Hinting == SKFontHinting.Slight && text.Length > 0 && !ContainsSurrogate(text);

		internal static SKFont CreateAdvanceFont(SKFont font) => new SKFont(font.Typeface, font.Size) {
			Edging = font.Edging,
			Hinting = SKFontHinting.Normal,
			LinearMetrics = false,
			ForceAutoHinting = font.ForceAutoHinting,
			EmbeddedBitmaps = font.EmbeddedBitmaps,
			Subpixel = false
		};

		internal static bool ContainsSurrogate(string text)
		{
			foreach (char value in text) if (char.IsSurrogate(value)) return true;
			return false;
		}

		/// <summary>cairo_text_extents for the managed font selection: ink box relative to the origin plus the pen advance.</summary>
		internal static TextExtents TextExtents(ManagedGraphicsState state, string text)
		{
			using FontOptions options = state.GetFontOptions();
			using SKFont font = SkiaGuiFont.Create(state, options);
			if (state.FontFileSource != null) {
				using var outlined = SkiaHintedFontOutlines.Create(font, state.Matrix, state.FontFileSource.LoadOptions);
				if (outlined != null) return FileTextExtents(font, options, text, outlined);
			}
			return TextExtents(font, options, text);
		}

		static TextExtents FileTextExtents(SKFont font, FontOptions options, string text, SkiaHintedFontOutlines outlined)
		{
			var result = new TextExtents();
			if (string.IsNullOrEmpty(text)) return result;
			bool round = options.HintMetrics != HintMetrics.Off;
			double x = 0, left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
			foreach (ushort glyph in font.GetGlyphs(text)) {
				SKRect bounds = outlined.GetGlyphBounds(glyph, round);
				if (bounds.Width > 0 && bounds.Height > 0) {
					left = Math.Min(left, x + bounds.Left); right = Math.Max(right, x + bounds.Right);
					top = Math.Min(top, bounds.Top); bottom = Math.Max(bottom, bounds.Bottom);
				}
				x += outlined.GetGlyphAdvance(glyph, round);
			}
			result.XAdvance = x;
			if (right > left && bottom > top) {
				result.XBearing = left; result.YBearing = top; result.Width = right - left; result.Height = bottom - top;
			}
			return result;
		}

		internal static TextExtents TextExtents(SKFont font, FontOptions options, string text)
		{
			var result = new TextExtents();
			if (string.IsNullOrEmpty(text)) return result;
			SKFont advanceFont = UsesNormalHintedAdvances(font, options, text) ? CreateAdvanceFont(font) : null;
			try {
				SKFont measure = advanceFont ?? font;
				ushort[] glyphs = measure.GetGlyphs(text);
				float[] widths = new float[glyphs.Length];
				measure.GetGlyphWidths(glyphs, widths, null);
				// Advances come from the metrics-hinted twin, but the ink box is the one the drawing font rasterizes.
				SKRect[] bounds = new SKRect[glyphs.Length];
				if (advanceFont != null) font.GetGlyphWidths(glyphs, new float[glyphs.Length], bounds);
				else measure.GetGlyphWidths(glyphs, widths, bounds);
				float x = 0;
				float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
				for (int i = 0; i < glyphs.Length; i++) {
					SKRect ink = bounds[i];
					if (ink.Width > 0 && ink.Height > 0) {
						left = Math.Min(left, x + ink.Left); right = Math.Max(right, x + ink.Right);
						top = Math.Min(top, ink.Top); bottom = Math.Max(bottom, ink.Bottom);
					}
					x += widths[i];
				}
				result.XAdvance = x;
				if (right > left && bottom > top) {
					result.XBearing = left; result.YBearing = top;
					result.Width = right - left; result.Height = bottom - top;
				}
				return result;
			} finally { advanceFont?.Dispose(); }
		}

		/// <summary>cairo_glyph_extents: the same box and advance, with explicit glyph positions.</summary>
		internal static TextExtents GlyphExtents(ManagedGraphicsState state, Glyph[] glyphs)
		{
			var result = new TextExtents();
			if (glyphs == null || glyphs.Length == 0) return result;
			using FontOptions options = state.GetFontOptions();
			using SKFont font = SkiaGuiFont.Create(state, options);
			var indices = new ushort[glyphs.Length];
			for (int i = 0; i < glyphs.Length; i++) {
				if (glyphs[i].Index < 0 || glyphs[i].Index > ushort.MaxValue)
					throw new InvalidOperationException("Skia glyph indices are 16-bit; glyph " + glyphs[i].Index + " cannot be measured.");
				indices[i] = (ushort)glyphs[i].Index;
			}
			float[] widths = new float[indices.Length];
			SKRect[] bounds = new SKRect[indices.Length];
			font.GetGlyphWidths(indices, widths, bounds);
			using var outlined = state.FontFileSource == null ? null : SkiaHintedFontOutlines.Create(font, state.Matrix, state.FontFileSource.LoadOptions);
			if (outlined != null) {
				for (int i = 0; i < indices.Length; i++) {
					widths[i] = (float)outlined.GetGlyphAdvance(indices[i], options.HintMetrics != HintMetrics.Off);
					bounds[i] = outlined.GetGlyphBounds(indices[i], options.HintMetrics != HintMetrics.Off);
				}
			}
			double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
			for (int i = 0; i < glyphs.Length; i++) {
				SKRect ink = bounds[i];
				if (ink.Width <= 0 || ink.Height <= 0) continue;
				left = Math.Min(left, glyphs[i].X + ink.Left); right = Math.Max(right, glyphs[i].X + ink.Right);
				top = Math.Min(top, glyphs[i].Y + ink.Top); bottom = Math.Max(bottom, glyphs[i].Y + ink.Bottom);
			}
			if (right > left && bottom > top) {
				result.XBearing = left - glyphs[0].X; result.YBearing = top - glyphs[0].Y;
				result.Width = right - left; result.Height = bottom - top;
			}
			result.XAdvance = glyphs[glyphs.Length - 1].X + widths[widths.Length - 1] - glyphs[0].X;
			result.YAdvance = 0;
			return result;
		}

		/// <summary>
		/// cairo_font_extents. With metrics hinting on (the image-surface default), FreeType rounds the scaled
		/// ascender and descender outward and the line height to the nearest pixel; the widest advance comes
		/// from the hhea table.
		/// </summary>
		internal static FontExtents FontExtents(ManagedGraphicsState state)
		{
			using FontOptions options = state.GetFontOptions();
			using SKFont font = SkiaGuiFont.Create(state, options);
			return FontExtents(font, options);
		}

		internal static FontExtents FontExtents(SKFont font, FontOptions options)
		{
			SKFontMetrics metrics = font.Metrics;
			double ascent = -metrics.Ascent, descent = metrics.Descent, height = ascent + descent + metrics.Leading;
			double maxAdvance = MaxAdvance(font);
			if (options.HintMetrics != HintMetrics.Off) {
				ascent = Math.Ceiling(ascent); descent = Math.Ceiling(descent);
				height = Math.Floor(height + 0.5); maxAdvance = Math.Floor(maxAdvance + 0.5);
			}
			var result = new FontExtents();
			result.Ascent = ascent; result.Descent = descent; result.Height = height;
			result.MaxXAdvance = maxAdvance; result.MaxYAdvance = 0;
			return result;
		}

		static double MaxAdvance(SKFont font)
		{
			SKTypeface typeface = font.Typeface;
			byte[] hhea = typeface.GetTableData(0x68686561u);
			if (hhea != null && hhea.Length >= 12 && typeface.UnitsPerEm > 0)
				return ((hhea[10] << 8) | hhea[11]) * (double)font.Size / typeface.UnitsPerEm;
			SKFontMetrics metrics = font.Metrics;
			return metrics.XMax - metrics.XMin;
		}
	}
}
