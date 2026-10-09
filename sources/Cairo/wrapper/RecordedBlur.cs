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
		{
			double ideal = Math.Sqrt (4 * range * range + 1);
			int lower = (int)Math.Floor (ideal);
			if (lower % 2 == 0) lower--;
			double count = Math.Round ((12 * range * range - 3 * lower * lower - 12 * lower - 9) / (-4.0 * lower - 4));
			return ((pass < count ? lower : lower + 2) - 1) / 2;
		}

		internal RecordedBlur (double range, int edge, int x1, int y1, int x2, int y2, bool full)
		{
			this.range = range; this.edge = edge; this.x1 = x1; this.y1 = y1; this.x2 = x2; this.y2 = y2; this.full = full;
		}

		/// <summary>Applies the blur to tightly packed 32-bit pixels, as <see cref="SurfaceTransformBlur"/> does.</summary>
		internal unsafe void ApplyToPixels (IntPtr data, int width, int height)
		{
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
