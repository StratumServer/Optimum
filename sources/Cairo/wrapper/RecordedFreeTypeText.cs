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
			TextExtents ink = SkiaTextMetrics.OutlineTextExtents(font, options, text, outlines);
			mask.Canvas.ClipRect(new SKRect((float)Math.Floor(matrix.Xx * (x + ink.XBearing) + matrix.X0 + .5),
				(float)Math.Floor(matrix.Yy * (y + ink.YBearing) + matrix.Y0 + .5),
				(float)Math.Floor(matrix.Xx * (x + ink.XBearing + ink.Width) + matrix.X0 + .5),
				(float)Math.Floor(matrix.Yy * (y + ink.YBearing + ink.Height) + matrix.Y0 + .5)), SKClipOperation.Intersect, false);
			using var paint = new SKPaint { BlendMode = SKBlendMode.Screen, IsAntialias = false };
			foreach (ushort glyph in font.GetGlyphs(text)) {
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
