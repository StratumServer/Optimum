using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Cairo
{
	/// <summary>
	/// Text policy for the GPU GUI path. While GUI GPU recording is eligible, a Subpixel
	/// font-options request is applied as Gray, for measurement and drawing alike, because
	/// LCD (component-alpha) glyphs cannot be reproduced on the Skia path. When GPU
	/// recording is not eligible Cairo receives exactly the requested options.
	/// </summary>
	internal static class CairoTextPolicy
	{
		static Func<bool> eligible;
		static bool resolved;
		/// <summary>Per-thread test hook; null uses OptimumConfig.GuiGpuRecordingEligible.</summary>
		[ThreadStatic] internal static bool? Override;

		internal static bool SubpixelAsGray {
			get {
				bool? value = Override;
				if (value.HasValue) return value.Value;
				if (!resolved) Resolve ();
				try { return eligible != null && eligible (); }
				catch { return false; }
			}
		}

		static void Resolve ()
		{
			resolved = true;
			try {
				// Late-bound: cairo-sharp cannot reference the API contracts assembly. A missing
				// type keeps vanilla behaviour.
				Type config = Type.GetType ("Vintagestory.API.Config.OptimumConfig, Optimum.Api.Contracts", false);
				PropertyInfo property = config?.GetProperty ("GuiGpuRecordingEligible", BindingFlags.Public | BindingFlags.Static);
				MethodInfo getter = property?.GetGetMethod ();
				if (getter != null && getter.ReturnType == typeof (bool))
					eligible = (Func<bool>)Delegate.CreateDelegate (typeof (Func<bool>), getter);
			} catch { eligible = null; }
		}

		/// <summary>Returns a Gray copy of <paramref name="options"/> to apply instead, or
		/// IntPtr.Zero when the options are applied unchanged.</summary>
		internal static IntPtr MapOptions (IntPtr options)
		{
			if (options == IntPtr.Zero || NativeMethods.cairo_font_options_get_antialias (options) != Antialias.Subpixel || !SubpixelAsGray) return IntPtr.Zero;
			IntPtr copy = NativeMethods.cairo_font_options_copy (options);
			NativeMethods.cairo_font_options_set_antialias (copy, Antialias.Gray);
			return copy;
		}
	}

	/// <summary>
	/// ShowText / ShowGlyphs as a recorded fill of the glyph outlines. Cairo stays the layout
	/// and glyph authority: glyph indices and positions come from cairo_scaled_font_text_to_glyphs,
	/// positions are rounded to device pixels like Cairo's image compositor, and the outlines
	/// come from cairo_glyph_path with the same scaled font. Native replay shows the text again.
	/// </summary>
	internal static class RecordedText
	{
		const string cairo = "libcairo-2";
		internal const int MaxGlyphs = 4096;
		internal const double MaxDevicePixelSize = 128;
		const int GlyphSize = 24; // cairo_glyph_t: unsigned long index (padded), double x, double y

		[DllImport (cairo, CallingConvention = CallingConvention.Cdecl)]
		static extern Status cairo_scaled_font_text_to_glyphs (IntPtr scaledFont, double x, double y, byte[] utf8, int utf8Length,
			ref IntPtr glyphs, out int numGlyphs, IntPtr clusters, IntPtr numClusters, IntPtr clusterFlags);
		[DllImport (cairo, CallingConvention = CallingConvention.Cdecl)]
		static extern void cairo_glyph_free (IntPtr glyphs);
		[DllImport (cairo, CallingConvention = CallingConvention.Cdecl)]
		static extern Status cairo_scaled_font_status (IntPtr scaledFont);
		[DllImport (cairo, CallingConvention = CallingConvention.Cdecl)]
		static extern void cairo_get_font_matrix (IntPtr cr, [Out] Matrix matrix);

		/// <summary>Records <c>cairo_show_text</c>. Returns null without failure when Cairo draws
		/// nothing (error state, no glyphs); <paramref name="advance"/> is the current point Cairo
		/// moves to, if any.</summary>
		internal static RecordedDrawing CaptureText (IntPtr cr, byte[] utf8, RecordedClip[] clips, Matrix sourceCtm, SurfaceRecorder target,
			out string failure, out PointD? advance)
		{
			failure = null; advance = null;
			if (NativeMethods.cairo_status (cr) != Status.Success) return null;
			if (!CheckFont (cr, out IntPtr scaledFont, out failure)) return null;
			double x = 0, y = 0;
			if (NativeMethods.cairo_has_current_point (cr)) NativeMethods.cairo_get_current_point (cr, out x, out y);
			IntPtr glyphs = IntPtr.Zero;
			Status status = cairo_scaled_font_text_to_glyphs (scaledFont, x, y, utf8, -1, ref glyphs, out int count, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			try {
				if (status != Status.Success) { failure = "text-to-glyphs-" + status; return null; }
				if (count == 0) return null;
				if (count > MaxGlyphs) { failure = "text-glyph-count"; return null; }
				// cairo_show_text moves to the last glyph's origin plus its advance.
				NativeMethods.cairo_glyph_extents (cr, IntPtr.Add (glyphs, (count - 1) * GlyphSize), 1, out TextExtents last);
				double lastX = Marshal.PtrToStructure<double> (IntPtr.Add (glyphs, (count - 1) * GlyphSize + 8));
				double lastY = Marshal.PtrToStructure<double> (IntPtr.Add (glyphs, (count - 1) * GlyphSize + 16));
				advance = new PointD (lastX + last.XAdvance, lastY + last.YAdvance);
				RecordedDrawing drawing = CaptureGlyphs (cr, scaledFont, glyphs, count, clips, sourceCtm, target, out failure);
				if (drawing != null) {
					drawing.TextUtf8 = (byte[])utf8.Clone ();
					drawing.TextOrigin = new PointD (x, y);
				}
				return drawing;
			} finally {
				if (glyphs != IntPtr.Zero) cairo_glyph_free (glyphs);
			}
		}

		/// <summary>Records <c>cairo_show_glyphs</c> (the current point does not move).</summary>
		internal static RecordedDrawing CaptureShowGlyphs (IntPtr cr, IntPtr glyphs, int count, RecordedClip[] clips, Matrix sourceCtm, SurfaceRecorder target, out string failure)
		{
			failure = null;
			if (NativeMethods.cairo_status (cr) != Status.Success || count <= 0) return null;
			if (count > MaxGlyphs) { failure = "text-glyph-count"; return null; }
			if (!CheckFont (cr, out IntPtr scaledFont, out failure)) return null;
			var copy = new byte[count * GlyphSize];
			Marshal.Copy (glyphs, copy, 0, copy.Length);
			RecordedDrawing drawing = CaptureGlyphs (cr, scaledFont, glyphs, count, clips, sourceCtm, target, out failure);
			if (drawing != null) drawing.TextGlyphs = copy;
			return drawing;
		}

		static bool CheckFont (IntPtr cr, out IntPtr scaledFont, out string failure)
		{
			failure = null;
			scaledFont = NativeMethods.cairo_get_scaled_font (cr);
			if (scaledFont == IntPtr.Zero || cairo_scaled_font_status (scaledFont) != Status.Success) { failure = "text-scaled-font"; return false; }
			IntPtr options = NativeMethods.cairo_font_options_create ();
			Antialias antialias;
			try {
				NativeMethods.cairo_get_font_options (cr, options);
				antialias = NativeMethods.cairo_font_options_get_antialias (options);
			} finally { NativeMethods.cairo_font_options_destroy (options); }
			// Only explicit grayscale glyphs are reproduced. Default lets fontconfig choose
			// (often LCD), Subpixel is LCD, None is binary.
			if (antialias != Antialias.Gray) { failure = "text-antialias-" + antialias; return false; }
			var ctm = new Matrix ();
			NativeMethods.cairo_get_matrix (cr, ctm);
			if (ctm.Xy != 0 || ctm.Yx != 0 || ctm.Xx <= 0 || ctm.Yy <= 0) { failure = "text-transform"; return false; }
			var font = new Matrix ();
			cairo_get_font_matrix (cr, font);
			double size = Math.Max (Math.Abs (font.Xx * ctm.Xx) + Math.Abs (font.Xy * ctm.Xx), Math.Abs (font.Yy * ctm.Yy) + Math.Abs (font.Yx * ctm.Yy));
			if (!(size > 0) || size > MaxDevicePixelSize) { failure = "text-size"; return false; }
			return true;
		}

		static RecordedDrawing CaptureGlyphs (IntPtr cr, IntPtr scaledFont, IntPtr glyphs, int count, RecordedClip[] clips, Matrix sourceCtm, SurfaceRecorder target, out string failure)
		{
			failure = null;
			var ctm = new Matrix ();
			NativeMethods.cairo_get_matrix (cr, ctm);
			// Cairo's image compositor draws each glyph mask at a quantized device origin:
			// whole pixels before 1.17, quarter-pixel phases from 1.18. Place the outlines at
			// the same origins. Development versions in between are not reproduced.
			int version = NativeMethods.cairo_version ();
			double steps;
			if (version >= 11800) steps = 4;
			else if (version < 11700) steps = 1;
			else { failure = "text-cairo-version-" + version; return null; }
			IntPtr rounded = Marshal.AllocHGlobal (count * GlyphSize);
			IntPtr scratchSurface = NativeMethods.cairo_image_surface_create (Format.Argb32, 1, 1);
			IntPtr scratch = NativeMethods.cairo_create (scratchSurface);
			try {
				var buffer = new byte[count * GlyphSize];
				Marshal.Copy (glyphs, buffer, 0, buffer.Length);
				Marshal.Copy (buffer, 0, rounded, buffer.Length);
				for (int i = 0; i < count; i++) {
					double gx = Marshal.PtrToStructure<double> (IntPtr.Add (glyphs, i * GlyphSize + 8));
					double gy = Marshal.PtrToStructure<double> (IntPtr.Add (glyphs, i * GlyphSize + 16));
					double dx = QuantizeOrigin (ctm.Xx * gx + ctm.X0, steps);
					double dy = QuantizeOrigin (ctm.Yy * gy + ctm.Y0, steps);
					Marshal.StructureToPtr ((dx - ctm.X0) / ctm.Xx, IntPtr.Add (rounded, i * GlyphSize + 8), false);
					Marshal.StructureToPtr ((dy - ctm.Y0) / ctm.Yy, IntPtr.Add (rounded, i * GlyphSize + 16), false);
				}
				// Build the outline on a scratch context so the live path and current point stay untouched.
				NativeMethods.cairo_set_matrix (scratch, ctm);
				NativeMethods.cairo_set_scaled_font (scratch, scaledFont);
				NativeMethods.cairo_glyph_path (scratch, rounded, count);
				if (NativeMethods.cairo_status (scratch) != Status.Success) { failure = "text-glyph-path"; return null; }
				RecordedDrawing drawing = RecordedDrawing.Capture (cr, RecordedDrawKind.Fill, 1, clips, sourceCtm, target, out failure, pathContext: scratch);
				if (drawing == null) return null;
				drawing.FillRule = FillRule.Winding;
				drawing.ScaledFont = new ScaledFont (scaledFont, false);
				return drawing;
			} finally {
				Marshal.FreeHGlobal (rounded);
				NativeMethods.cairo_destroy (scratch);
				NativeMethods.cairo_surface_destroy (scratchSurface);
			}
		}

		/// <summary>Glyph origin as composited by Cairo. 1.18 rounds the 24.8 fixed position to
		/// a quarter pixel (half up); older versions use the nearest whole pixel.</summary>
		internal static double QuantizeOrigin (double device, double steps)
		{
			if (steps == 1) return Math.Round (device, MidpointRounding.AwayFromZero);
			double fixedPoint = Math.Round (device * 256.0, MidpointRounding.ToEven) / 256.0;
			return Math.Floor (fixedPoint * steps + 0.5) / steps;
		}

		/// <summary>Native replay of the original text call on a prepared context.</summary>
		internal static void ReplayNative (IntPtr cr, RecordedDrawing drawing)
		{
			NativeMethods.cairo_set_scaled_font (cr, drawing.ScaledFont.Handle);
			if (drawing.TextUtf8 != null) {
				NativeMethods.cairo_move_to (cr, drawing.TextOrigin.X, drawing.TextOrigin.Y);
				NativeMethods.cairo_show_text (cr, drawing.TextUtf8);
			} else {
				IntPtr glyphs = Marshal.AllocHGlobal (drawing.TextGlyphs.Length);
				try {
					Marshal.Copy (drawing.TextGlyphs, 0, glyphs, drawing.TextGlyphs.Length);
					NativeMethods.cairo_show_glyphs (cr, glyphs, drawing.TextGlyphs.Length / GlyphSize);
				} finally { Marshal.FreeHGlobal (glyphs); }
			}
		}
	}
}
