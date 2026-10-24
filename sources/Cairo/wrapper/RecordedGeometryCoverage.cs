using System;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Supersamples stroke coverage for narrow gaps and zero-length capped dashes.</summary>
	internal sealed class RecordedGeometryCoverage : IDisposable
	{
		internal readonly SKImage Image;
		internal readonly SKRect Bounds;
		internal RecordedGeometryCoverage(SKImage image, SKRect bounds) { Image = image; Bounds = bounds; }

		internal static bool Needed(float[] intervals, Matrix matrix, SKStrokeCap cap)
		{
			if (intervals == null) return false;
			if (cap != SKStrokeCap.Butt) for (int i = 0; i < intervals.Length; i += 2) if (intervals[i] == 0) return true;
			double scale = Math.Max(Math.Abs(matrix.Xx) + Math.Abs(matrix.Xy), Math.Abs(matrix.Yx) + Math.Abs(matrix.Yy));
			for (int i = 1; i < intervals.Length; i += 2)
				if (intervals[i] > 0 && intervals[i] * scale < 1) return true;
			return false;
		}

		internal static RecordedGeometryCoverage Capture(SKPath path, Matrix matrix, int width, int height)
		{
			using var device = new SKPath(path); device.Transform(RecordedGuiDrawing.ToSkia(matrix));
			SKRect bounds = device.Bounds;
			int left = (int)Math.Max(0, Math.Floor(bounds.Left)), top = (int)Math.Max(0, Math.Floor(bounds.Top));
			int right = (int)Math.Min(width, Math.Ceiling(bounds.Right)), bottom = (int)Math.Min(height, Math.Ceiling(bounds.Bottom));
			if (right <= left || bottom <= top) return null;
			using var mask = new SKBitmap(right - left, bottom - top, SKColorType.Bgra8888, SKAlphaType.Premul);
			using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
			const int scale = 4, tileSize = 128;
			for (int ty = top; ty < bottom; ty += tileSize) for (int tx = left; tx < right; tx += tileSize) {
				int w = Math.Min(tileSize, right - tx), h = Math.Min(tileSize, bottom - ty);
				using var tile = new SKBitmap(w * scale, h * scale, SKColorType.Bgra8888, SKAlphaType.Premul);
				using (var canvas = new SKCanvas(tile)) {
					canvas.Clear(SKColors.Transparent); canvas.Scale(scale); canvas.Translate(-tx, -ty);
					canvas.DrawPath(device, paint);
				}
				for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
					int sum = 0;
					for (int sy = 0; sy < scale; sy++) for (int sx = 0; sx < scale; sx++) sum += tile.GetPixel(x * scale + sx, y * scale + sy).Alpha;
					byte alpha = (byte)((sum + scale * scale / 2) / (scale * scale));
					mask.SetPixel(tx - left + x, ty - top + y, new SKColor(255, 255, 255, alpha));
				}
			}
			return new RecordedGeometryCoverage(SKImage.FromBitmap(mask), new SKRect(left, top, right, bottom));
		}
		public void Dispose() => Image.Dispose();
	}
}
