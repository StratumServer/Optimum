using System;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Builds text paths from the selected Skia typeface, preserving Linux device-grid hinting.</summary>
	internal static class SkiaTextPath
	{
		/// <summary>Appends the outlines of a string laid out from the current point, then moves the current point past it.</summary>
		internal static void AppendText(ManagedGraphicsState state, string text)
		{
			if (state.Status != Status.Success) return;
			state.EnsureScaledFontOwner ();
			if (state.Status != Status.Success) return;
			if (string.IsNullOrEmpty(text)) return;
			state.TryGetCurrentPoint(out double originX, out double originY);
			using FontOptions options = state.GetFontOptions();
			using SKFont font = SkiaGuiFont.Create(state, options);
			using SkiaHintedFontOutlines hinted = SkiaHintedFontOutlines.Create(font, state.Matrix, state.FontFileSource?.LoadOptions ?? 0);
			SKFont advanceFont = SkiaTextMetrics.UsesNormalHintedAdvances(font, options, text) ? SkiaTextMetrics.CreateAdvanceFont(font) : null;
			try {
				SKFont measure = advanceFont ?? font;
				ushort[] glyphs = measure.GetGlyphs(text);
				float[] widths = new float[glyphs.Length];
				measure.GetGlyphWidths(glyphs, widths, null);
				double x = originX;
				for (int i = 0; i < glyphs.Length; i++) {
					AppendGlyph(state, font, hinted, glyphs[i], x, originY);
					x += state.FontFileSource != null && hinted != null ? hinted.GetGlyphAdvance(glyphs[i], options.HintMetrics != HintMetrics.Off) : widths[i];
				}
				state.MoveTo(x, originY);
			} finally { advanceFont?.Dispose(); }
		}

		/// <summary>Appends the outlines of explicitly positioned glyphs.</summary>
		internal static void AppendGlyphs(ManagedGraphicsState state, Glyph[] glyphs)
		{
			if (state.Status != Status.Success) return;
			if (glyphs == null || glyphs.Length == 0) return;
			state.EnsureScaledFontOwner ();
			if (state.Status != Status.Success) return;
			using FontOptions options = state.GetFontOptions();
			using SKFont font = SkiaGuiFont.Create(state, options);
			using SkiaHintedFontOutlines hinted = SkiaHintedFontOutlines.Create(font, state.Matrix, state.FontFileSource?.LoadOptions ?? 0);
			foreach (Glyph glyph in glyphs) {
				if (glyph.Index < 0 || glyph.Index > ushort.MaxValue)
					throw new InvalidOperationException("Skia glyph indices are 16-bit; glyph " + glyph.Index + " has no outline.");
				AppendGlyph(state, font, hinted, (ushort)glyph.Index, glyph.X, glyph.Y);
			}
		}

		static void AppendGlyph(ManagedGraphicsState state, SKFont font, SkiaHintedFontOutlines hinted, ushort glyph, double x, double y)
		{
			using SKPath outline = hinted?.GetGlyphPath(glyph) ?? font.GetGlyphPath(glyph);
			if (outline == null || outline.PointCount == 0) return;
			using SKPath.RawIterator iterator = outline.CreateRawIterator();
			var points = new SKPoint[4];
			SKPoint current = default;
			SKPathVerb verb;
			while ((verb = iterator.Next(points)) != SKPathVerb.Done) {
				switch (verb) {
				case SKPathVerb.Move:
					state.MoveTo(x + points[0].X, y + points[0].Y);
					current = points[0];
					break;
				case SKPathVerb.Line:
					state.LineTo(x + points[1].X, y + points[1].Y);
					current = points[1];
					break;
				case SKPathVerb.Quad:
					QuadTo(state, x, y, points[0], points[1], points[2]);
					current = points[2];
					break;
				case SKPathVerb.Conic: {
					// Rare: TrueType and CFF outlines contain no conics. Approximate with four quadratics.
					SKPoint[] quads = SKPath.ConvertConicToQuads(points[0], points[1], points[2], iterator.ConicWeight(), 2);
					for (int i = 0; i + 2 < quads.Length; i += 2) QuadTo(state, x, y, quads[i], quads[i + 1], quads[i + 2]);
					current = points[2];
					break;
				}
				case SKPathVerb.Cubic:
					state.CurveTo(x + points[1].X, y + points[1].Y, x + points[2].X, y + points[2].Y, x + points[3].X, y + points[3].Y);
					current = points[3];
					break;
				case SKPathVerb.Close:
					state.ClosePath();
					break;
				}
			}
		}

		// Degree elevation: exact cubic form of a quadratic Bezier.
		static void QuadTo(ManagedGraphicsState state, double x, double y, SKPoint p0, SKPoint control, SKPoint p2)
		{
			double c1x = p0.X + 2.0 / 3.0 * (control.X - p0.X), c1y = p0.Y + 2.0 / 3.0 * (control.Y - p0.Y);
			double c2x = p2.X + 2.0 / 3.0 * (control.X - p2.X), c2y = p2.Y + 2.0 / 3.0 * (control.Y - p2.Y);
			state.CurveTo(x + c1x, y + c1y, x + c2x, y + c2y, x + p2.X, y + p2.Y);
		}
	}
}
