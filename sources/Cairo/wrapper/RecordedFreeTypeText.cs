using System;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Captures system font coverage for component-alpha operators without native Cairo.</summary>
	internal static class RecordedFreeTypeText
	{
		internal static SKImage Capture(ManagedGraphicsState state, SKFont font, FontOptions options, string text,
			RecordedGuiClip[] clips, int width, int height, out PointD origin)
		{
			origin = default;
			Antialias antialias = options.Antialias;
			SubpixelOrder order = options.SubpixelOrder;
			if (state.FontFileSource == null && (antialias == Antialias.Default || order == SubpixelOrder.Default)) {
				var weight = state.FontWeight == FontWeight.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
				var slant = state.FontSlant == FontSlant.Italic ? SKFontStyleSlant.Italic : state.FontSlant == FontSlant.Oblique ? SKFontStyleSlant.Oblique : SKFontStyleSlant.Upright;
				using var resolved = SkiaFontResolver.Resolve(state.FontFamily, weight, slant, out _, out Antialias configuredAntialias, out SubpixelOrder configuredOrder);
				if (antialias == Antialias.Default) antialias = configuredAntialias;
				if (order == SubpixelOrder.Default) order = configuredOrder;
			}
			bool alphaClip = RequiresAlphaClip(clips);
			using var outlines = SkiaHintedFontOutlines.Create(font, state.Matrix, state.FontFileSource?.LoadOptions ?? 0);
			if (outlines == null) return null;
			state.TryGetCurrentPoint(out double x, out double y);
			Matrix matrix = state.Matrix;
			ushort[] glyphs = font.GetGlyphs(text);
			double position = x, minX = double.PositiveInfinity, minY = double.PositiveInfinity;
			double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
			bool overlap = false;
			using var metricFont = options.HintMetrics == HintMetrics.Off ? new SKFont(font.Typeface, font.Size) {
				ScaleX = font.ScaleX, SkewX = font.SkewX, Hinting = SKFontHinting.None, Subpixel = true } : null;
			// Cairo's image surface rounds each glyph origin before unioning its
			// device-space metric box, then rounds the union outward (24.8 fixed).
			foreach (ushort glyph in glyphs) {
				using var metricPath = metricFont?.GetGlyphPath(glyph);
				SKRect ink = metricFont != null ? metricPath?.Bounds ?? default : outlines.GetGlyphBounds(glyph, true);
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
			var coverages = new (SKBitmap Bitmap, float X, float Y)[glyphs.Length];
			int leftBound = width, topBound = height, rightBound = 0, bottomBound = 0;
			try {
				for (int i = 0; i < glyphs.Length; i++) {
					double px = x, py = y; matrix.TransformPoint(ref px, ref py);
					// Quantize in original device coordinates, including LCD filter fringes.
					var coverage = outlines.GetGlyphCoverage(glyphs[i], antialias, order, out int left, out int top,
						Math.Floor((px - Math.Floor(px)) * 4 + .5) / 4, Math.Floor((py - Math.Floor(py)) * 4 + .5) / 4);
					float gx = (float)Math.Floor(px) + left, gy = (float)Math.Floor(py) - top;
					coverages[i] = (coverage, gx, gy);
					if (coverage != null) {
						leftBound = Math.Min(leftBound, (int)Math.Clamp(gx, 0, width));
						topBound = Math.Min(topBound, (int)Math.Clamp(gy, 0, height));
						rightBound = Math.Max(rightBound, (int)Math.Clamp(gx + coverage.Width, 0, width));
						bottomBound = Math.Max(bottomBound, (int)Math.Clamp(gy + coverage.Height, 0, height));
					}
					x += outlines.GetGlyphAdvance(glyphs[i], options.HintMetrics != HintMetrics.Off);
				}
				// Retain ink-sized rasters, not a full GUI-sized raster for every label.
				// Empty/off-surface text keeps an explicit black (zero coverage) sentinel.
				if (rightBound <= leftBound || bottomBound <= topBound) {
					leftBound = topBound = 0; rightBound = bottomBound = 1;
				}
				origin = new PointD(leftBound, topBound);
				using var mask = SKSurface.Create(new SKImageInfo(rightBound - leftBound, bottomBound - topBound, SKColorType.Rgba8888, SKAlphaType.Opaque));
				mask.Canvas.Clear(SKColors.Black);
				mask.Canvas.Translate(-leftBound, -topBound);
				foreach (var clip in clips) {
					if (alphaClip && !clip.Antialias) {
						// BW scan conversion uses the original device bounds, not the ink crop.
						using var ownerBounds = new SKRegion(new SKRectI(0, 0, width, height));
						using var region = new SKRegion();
						region.SetPath(clip.Path, ownerBounds); region.Translate(-leftBound, -topBound);
						mask.Canvas.ClipRegion(region, SKClipOperation.Intersect);
					} else mask.Canvas.ClipPath(clip.Path, SKClipOperation.Intersect, clip.Antialias);
				}
				// Cairo clips its accumulated mask to the glyph metric box when the clip
				// does not fully contain it (need_bounded_clip: mask vs bounded extents).
				// Disjoint glyphs otherwise go straight to Pixman, retaining LCD fringe pixels.
				bool boundedClip = false;
				foreach (var clip in clips) {
					var cb = clip.Path.Bounds;
					if (cb.Left > Math.Floor(minX) || cb.Top > Math.Floor(minY)
						|| cb.Right < Math.Ceiling(maxX) || cb.Bottom < Math.Ceiling(maxY))
						boundedClip = true;
				}
				if (overlap || alphaClip || boundedClip) mask.Canvas.ClipRect(new SKRect((float)Math.Floor(minX), (float)Math.Floor(minY),
					(float)Math.Ceiling(maxX), (float)Math.Ceiling(maxY)), SKClipOperation.Intersect, false);
				// Cairo's complex-clip glyph compositor uses A8: LCD alpha is green.
				using var alphaFilter = alphaClip ? SKColorFilter.CreateColorMatrix(new float[] {
					0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0 }) : null;
				using var paint = new SKPaint { BlendMode = overlap || alphaClip ? SKBlendMode.Plus : SKBlendMode.Screen,
					IsAntialias = false, ColorFilter = alphaFilter };
				foreach (var coverage in coverages)
					if (coverage.Bitmap != null) mask.Canvas.DrawBitmap(coverage.Bitmap, coverage.X, coverage.Y, paint);
				return mask.Snapshot();
			} finally {
				foreach (var coverage in coverages) coverage.Bitmap?.Dispose();
			}
		}

		static bool RequiresAlphaClip(RecordedGuiClip[] clips)
		{
			static bool Diagonal(SKPoint a, SKPoint b) =>
				Math.Round(a.X * 256d) != Math.Round(b.X * 256d) && Math.Round(a.Y * 256d) != Math.Round(b.Y * 256d);
			var points = new SKPoint[4];
			foreach (var clip in clips) {
				using var iterator = clip.Path.CreateRawIterator();
				SKPoint cursor = default, start = default;
				bool open = false;
				SKPathVerb verb;
				while ((verb = iterator.Next(points)) != SKPathVerb.Done) {
					if (verb == SKPathVerb.Move) {
						if (open && Diagonal(cursor, start)) return true;
						cursor = start = points[0]; open = true;
					} else if (verb == SKPathVerb.Line) {
						if (Diagonal(cursor, points[1])) return true;
						cursor = points[1];
					} else if (verb == SKPathVerb.Close) {
						if (open && Diagonal(cursor, start)) return true;
						cursor = start; open = false;
					} else return true;
				}
				if (open && Diagonal(cursor, start)) return true;
			}
			return false;
		}
	}
}
