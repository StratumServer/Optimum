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
			ScaleX = font.ScaleX,
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
			if (state.Status != Status.Success) return default;
			state.EnsureScaledFontOwner ();
			if (state.Status != Status.Success) return default;
			if (state.IsZeroFontMatrix) return default;
			using FontOptions options = state.GetFontOptions();
			using SKFont font = SkiaGuiFont.Create(state, options);
			TextExtents result;
			if (state.FontFileSource != null && !state.HasAffineFontMatrix) {
				using var outlined = SkiaHintedFontOutlines.Create(font, state.Matrix, state.FontFileSource.LoadOptions);
				result = outlined != null ? OutlineTextExtents(font, options, text, outlined) : TextExtents(font, options, text);
			} else if (state.HasAffineFontMatrix) {
				result = AffineTextExtents(font, options, text, state.FontMatrix, state.EffectiveFontSize);
			} else {
				result = TextExtents(font, options, text);
			}
			PointD trans = state.FontTranslation;
			if ((trans.X != 0 || trans.Y != 0) && (result.Width > 0 || result.Height > 0)) {
				result.XBearing += trans.X;
				result.YBearing += trans.Y;
			}
			return result;
		}

		internal static TextExtents OutlineTextExtents(SKFont font, FontOptions options, string text, SkiaHintedFontOutlines outlined)
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

		internal static TextExtents AffineTextExtents(SKFont font, FontOptions options, string text, Matrix matrix, double S)
		{
			var result = new TextExtents();
			if (string.IsNullOrEmpty(text)) return result;
			SKFont advanceFont = UsesNormalHintedAdvances(font, options, text) ? CreateAdvanceFont(font) : null;
			try {
				SKFont measure = advanceFont ?? font;
				ushort[] glyphs = measure.GetGlyphs(text);
				float[] widths = new float[glyphs.Length];
				measure.GetGlyphWidths(glyphs, widths, null);
				SKRect[] bounds = new SKRect[glyphs.Length];
				if (advanceFont != null) font.GetGlyphWidths(glyphs, new float[glyphs.Length], bounds);
				else measure.GetGlyphWidths(glyphs, widths, bounds);
				// Cairo measures unhinted metrics at the face's EM size. Raster
				// bounds enclose integer pixels and inflate again under a shear.
				if (options.HintMetrics == HintMetrics.Off) {
					for (int i = 0; i < glyphs.Length; i++) {
						using var outline = font.GetGlyphPath(glyphs[i]);
						bounds[i] = outline?.Bounds ?? SKRect.Empty;
					}
				}

				double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
				double xPos = 0;
				bool hasInk = false;
				double invS = 1.0 / S;
				double mXx = matrix.Xx * invS, mXy = matrix.Xy * invS;
				double mYx = matrix.Yx * invS, mYy = matrix.Yy * invS;

				for (int i = 0; i < glyphs.Length; i++) {
					SKRect ink = bounds[i];
					if (ink.Width > 0 && ink.Height > 0) {
						hasInk = true;
						double x1 = xPos + ink.Left, x2 = xPos + ink.Right;
						double y1 = ink.Top, y2 = ink.Bottom;
						double cx1 = mXx * x1 + mXy * y1, cy1 = mYx * x1 + mYy * y1;
						double cx2 = mXx * x2 + mXy * y1, cy2 = mYx * x2 + mYy * y1;
						double cx3 = mXx * x1 + mXy * y2, cy3 = mYx * x1 + mYy * y2;
						double cx4 = mXx * x2 + mXy * y2, cy4 = mYx * x2 + mYy * y2;
						minX = Math.Min(minX, Math.Min(Math.Min(cx1, cx2), Math.Min(cx3, cx4)));
						maxX = Math.Max(maxX, Math.Max(Math.Max(cx1, cx2), Math.Max(cx3, cx4)));
						minY = Math.Min(minY, Math.Min(Math.Min(cy1, cy2), Math.Min(cy3, cy4)));
						maxY = Math.Max(maxY, Math.Max(Math.Max(cy1, cy2), Math.Max(cy3, cy4)));
					}
					xPos += widths[i];
				}
				result.XAdvance = mXx * xPos;
				result.YAdvance = mYx * xPos;
				if (hasInk && maxX > minX && maxY > minY) {
					result.XBearing = minX;
					result.YBearing = minY;
					result.Width = maxX - minX;
					result.Height = maxY - minY;
				}
				return result;
			} finally { advanceFont?.Dispose(); }
		}

		/// <summary>cairo_glyph_extents: the same box and advance, with explicit glyph positions.</summary>
		internal static TextExtents GlyphExtents(ManagedGraphicsState state, Glyph[] glyphs)
		{
			if (state.Status != Status.Success) return default;
			var result = new TextExtents();
			if (glyphs == null || glyphs.Length == 0) return result;
			state.EnsureScaledFontOwner ();
			if (state.Status != Status.Success) return default;
			if (state.IsZeroFontMatrix) return default;
			using FontOptions options = state.GetFontOptions();
			using SKFont font = SkiaGuiFont.Create(state, options);
			if (state.HasAffineFontMatrix)
				return AffineGlyphExtents(state, glyphs, font, options, state.FontMatrix, state.EffectiveFontSize);
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
			PointD trans = state.FontTranslation;
			if ((trans.X != 0 || trans.Y != 0) && (result.Width > 0 || result.Height > 0)) {
				result.XBearing += trans.X;
				result.YBearing += trans.Y;
			}
			return result;
		}

		internal static TextExtents AffineGlyphExtents(ManagedGraphicsState state, Glyph[] glyphs, SKFont font, FontOptions options, Matrix matrix, double S)
		{
			var result = new TextExtents();
			if (glyphs == null || glyphs.Length == 0) return result;
			var indices = new ushort[glyphs.Length];
			for (int i = 0; i < glyphs.Length; i++) {
				if (glyphs[i].Index < 0 || glyphs[i].Index > ushort.MaxValue)
					throw new InvalidOperationException("Skia glyph indices are 16-bit; glyph " + glyphs[i].Index + " cannot be measured.");
				indices[i] = (ushort)glyphs[i].Index;
			}
			float[] widths = new float[indices.Length];
			SKRect[] bounds = new SKRect[indices.Length];
			font.GetGlyphWidths(indices, widths, bounds);
			if (options.HintMetrics == HintMetrics.Off) {
				for (int i = 0; i < indices.Length; i++) {
					using var outline = font.GetGlyphPath(indices[i]);
					bounds[i] = outline?.Bounds ?? SKRect.Empty;
				}
			}

			double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
			bool hasInk = false;
			double invS = 1.0 / S;
			double mXx = matrix.Xx * invS, mXy = matrix.Xy * invS;
			double mYx = matrix.Yx * invS, mYy = matrix.Yy * invS;

			for (int i = 0; i < glyphs.Length; i++) {
				SKRect ink = bounds[i];
				if (ink.Width > 0 && ink.Height > 0) {
					hasInk = true;
					double x1 = ink.Left, x2 = ink.Right;
					double y1 = ink.Top, y2 = ink.Bottom;
					double cx1 = glyphs[i].X + mXx * x1 + mXy * y1, cy1 = glyphs[i].Y + mYx * x1 + mYy * y1;
					double cx2 = glyphs[i].X + mXx * x2 + mXy * y1, cy2 = glyphs[i].Y + mYx * x2 + mYy * y1;
					double cx3 = glyphs[i].X + mXx * x1 + mXy * y2, cy3 = glyphs[i].Y + mYx * x1 + mYy * y2;
					double cx4 = glyphs[i].X + mXx * x2 + mXy * y2, cy4 = glyphs[i].Y + mYx * x2 + mYy * y2;
					minX = Math.Min(minX, Math.Min(Math.Min(cx1, cx2), Math.Min(cx3, cx4)));
					maxX = Math.Max(maxX, Math.Max(Math.Max(cx1, cx2), Math.Max(cx3, cx4)));
					minY = Math.Min(minY, Math.Min(Math.Min(cy1, cy2), Math.Min(cy3, cy4)));
					maxY = Math.Max(maxY, Math.Max(Math.Max(cy1, cy2), Math.Max(cy3, cy4)));
				}
			}
			if (hasInk && maxX > minX && maxY > minY) {
				result.XBearing = minX - glyphs[0].X;
				result.YBearing = minY - glyphs[0].Y;
				result.Width = maxX - minX;
				result.Height = maxY - minY;
			}
			int last = glyphs.Length - 1;
			result.XAdvance = glyphs[last].X + mXx * widths[last] - glyphs[0].X;
			result.YAdvance = glyphs[last].Y + mYx * widths[last] - glyphs[0].Y;
			PointD trans = state.FontTranslation;
			if ((trans.X != 0 || trans.Y != 0) && (result.Width > 0 || result.Height > 0)) {
				result.XBearing += trans.X;
				result.YBearing += trans.Y;
			}
			return result;
		}

		/// <summary>
		/// cairo_font_extents. With metrics hinting on (the image-surface default), FreeType rounds the scaled
		/// ascender and descender outward and the line height to the nearest pixel; the widest advance comes
		/// from the hhea table.
		/// </summary>
		internal static FontExtents FontExtents(ManagedGraphicsState state)
		{
			if (state.Status != Status.Success) return default;
			state.EnsureScaledFontOwner ();
			if (state.Status != Status.Success) return default;
			if (state.IsZeroFontMatrix) return default;
			using FontOptions options = state.GetFontOptions();
			using SKFont font = SkiaGuiFont.Create(state, options);
			FontExtents result = FontExtents(font, options);
			if (state.HasAffineFontMatrix) {
				Matrix m = state.FontMatrix;
				double Lx = Math.Sqrt (m.Xx * m.Xx + m.Yx * m.Yx);
				double Ly = Math.Sqrt (m.Xy * m.Xy + m.Yy * m.Yy);
				if (Ly > 0 && Lx > 0) {
					double maxAdv = MaxAdvance(font) * (Lx / (font.Size * font.ScaleX));
					if (options.HintMetrics != HintMetrics.Off) maxAdv = Math.Floor(maxAdv + 0.5);
					result.MaxXAdvance = maxAdv;
				}
			}
			return result;
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
				return ((hhea[10] << 8) | hhea[11]) * (double)(font.Size * font.ScaleX) / typeface.UnitsPerEm;
			SKFontMetrics metrics = font.Metrics;
			return (metrics.XMax - metrics.XMin) * font.ScaleX;
		}
	}
}
