using System;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Captures system font coverage for component-alpha operators without native Cairo.</summary>
	internal static class RecordedFreeTypeText
	{
		internal static SKImage Capture(ManagedGraphicsState state, SKFont font, FontOptions options, string text,
			RecordedGuiClip[] clips, int width, int height)
		{
			Antialias antialias = options.Antialias;
			SubpixelOrder order = options.SubpixelOrder;
			if (state.FontFileSource == null && (antialias == Antialias.Default || order == SubpixelOrder.Default)) {
				var weight = state.FontWeight == FontWeight.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
				var slant = state.FontSlant == FontSlant.Italic ? SKFontStyleSlant.Italic : state.FontSlant == FontSlant.Oblique ? SKFontStyleSlant.Oblique : SKFontStyleSlant.Upright;
				using var resolved = SkiaFontResolver.Resolve(state.FontFamily, weight, slant, out _, out Antialias configuredAntialias, out SubpixelOrder configuredOrder);
				if (antialias == Antialias.Default) antialias = configuredAntialias;
				if (order == SubpixelOrder.Default) order = configuredOrder;
			}
			using var outlines = SkiaHintedFontOutlines.Create(font, state.Matrix, state.FontFileSource?.LoadOptions ?? 0);
			if (outlines == null) return null;
			using var mask = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
			mask.Canvas.Clear(SKColors.Black);
			foreach (var clip in clips) mask.Canvas.ClipPath(clip.Path, SKClipOperation.Intersect, clip.Antialias);
			state.TryGetCurrentPoint(out double x, out double y);
			Matrix matrix = state.Matrix;
			ushort[] glyphs = font.GetGlyphs(text);
			double position = x, minX = double.PositiveInfinity, minY = double.PositiveInfinity;
			double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
			bool overlap = false;
			// Cairo's image surface rounds each glyph origin before unioning its
			// device-space metric box, then rounds the union outward (24.8 fixed).
			foreach (ushort glyph in glyphs) {
				SKRect ink = outlines.GetGlyphBounds(glyph, options.HintMetrics != HintMetrics.Off);
				if (ink.Width > 0 && ink.Height > 0) {
					double px = Math.Floor(matrix.Xx * position + matrix.X0 + .5);
					double py = Math.Floor(matrix.Yy * y + matrix.Y0 + .5);
					double left = px + Math.Round(matrix.Xx * ink.Left * 256) / 256;
					double top = py + Math.Round(matrix.Yy * ink.Top * 256) / 256;
					double right = px + Math.Round(matrix.Xx * ink.Right * 256) / 256;
					double bottom = py + Math.Round(matrix.Yy * ink.Bottom * 256) / 256;
					overlap |= right > minX && left < maxX && bottom > minY && top < maxY;
					minX = Math.Min(minX, left); minY = Math.Min(minY, top);
					maxX = Math.Max(maxX, right); maxY = Math.Max(maxY, bottom);
				}
				position += outlines.GetGlyphAdvance(glyph, options.HintMetrics != HintMetrics.Off);
			}
			// Cairo clips its accumulated mask only for overlapping metrics.
			// Disjoint glyphs go straight to Pixman, retaining LCD fringe pixels.
			if (overlap) mask.Canvas.ClipRect(new SKRect((float)Math.Floor(minX), (float)Math.Floor(minY),
				(float)Math.Ceiling(maxX), (float)Math.Ceiling(maxY)), SKClipOperation.Intersect, false);
			using var paint = new SKPaint { BlendMode = overlap ? SKBlendMode.Plus : SKBlendMode.Screen, IsAntialias = false };
			foreach (ushort glyph in glyphs) {
				double px = x, py = y; matrix.TransformPoint(ref px, ref py);
				// Cairo caches glyph masks in quarter-pixel phases.
				using var coverage = outlines.GetGlyphCoverage(glyph, antialias, order, out int left, out int top, Math.Floor((px - Math.Floor(px)) * 4 + .5) / 4, Math.Floor((py - Math.Floor(py)) * 4 + .5) / 4);
				if (coverage != null) {
					mask.Canvas.DrawBitmap(coverage, (float)Math.Floor(px) + left, (float)Math.Floor(py) - top, paint);
				}
				x += outlines.GetGlyphAdvance(glyph, options.HintMetrics != HintMetrics.Off);
			}
			return mask.Snapshot();
		}
	}
}
