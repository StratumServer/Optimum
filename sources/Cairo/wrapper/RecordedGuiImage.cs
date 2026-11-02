using System;
using SkiaSharp;

namespace Cairo
{
	// Immutable asset pixels; resize and the game's straight-alpha overlay execute
	// on the render context. Native replay is reserved for an explicit fallback.
	internal sealed class RecordedGuiImage : IDisposable
	{
		internal readonly SKBitmap Bitmap;
		internal readonly SKImage Image;
		internal readonly int X, Y, Width, Height;
		internal RecordedGuiImage(SKBitmap bitmap, int x, int y, int width, int height)
		{
			Bitmap = bitmap.Copy() ?? throw new InvalidOperationException("Could not snapshot GUI image pixels.");
			Image = SKImage.FromBitmap(Bitmap);
			X = x; Y = y; Width = width; Height = height;
		}
		internal void Replay(IntPtr target)
		{
			using (var surface = new ImageSurface(target, false)) surface.Image(Bitmap, X, Y, Width, Height);
		}
		public void Dispose() { Image.Dispose(); Bitmap.Dispose(); }
	}
}
