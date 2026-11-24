using System;

namespace Cairo
{
	/// <summary>A recorded SurfaceTransformBlur call, replayed on pixels with the game's original algorithm.</summary>
	internal sealed class RecordedBlur
	{
		readonly double range;
		readonly int edge, x1, y1, x2, y2;
		readonly bool full;

		internal bool Full => full;
		internal int Edge => edge;
		internal int X1 => x1;
		internal int Y1 => y1;
		internal int X2 => x2;
		internal int Y2 => y2;

		internal int Radius (int pass)
			=> GaussianBlur.ComputeBoxRadius(range, pass, int.MaxValue, int.MaxValue);

		internal int Radius (int pass, int rectWidth, int rectHeight)
			=> GaussianBlur.ComputeBoxRadius(range, pass, rectWidth, rectHeight);

		internal RecordedBlur (double range, int edge, int x1, int y1, int x2, int y2, bool full)
		{
			this.range = range; this.edge = edge; this.x1 = x1; this.y1 = y1; this.x2 = x2; this.y2 = y2; this.full = full;
		}

		/// <summary>Applies the blur to tightly packed 32-bit pixels, as <see cref="SurfaceTransformBlur"/> does.</summary>
		internal unsafe void ApplyToPixels (IntPtr data, int width, int height)
		{
			if (data == IntPtr.Zero || width <= 0 || height <= 0) return;
			var blur = new GaussianBlur ((int*)data.ToPointer (), width, height);
			if (full) blur.ProcessFull (range); else blur.ProcessPartial (range, edge, x1, y1, x2, y2);
		}

		internal void ApplyToSurface (IntPtr surface)
		{
			NativeMethods.cairo_surface_flush (surface);
			ApplyToPixels (NativeMethods.cairo_image_surface_get_data (surface),
				NativeMethods.cairo_image_surface_get_width (surface), NativeMethods.cairo_image_surface_get_height (surface));
			NativeMethods.cairo_surface_mark_dirty (surface);
		}
	}
}
