using System;
using System.Reflection;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Cairo
{
	/// <summary>
	/// GUI GPU recording eligibility as seen from cairo-sharp, late-bound to
	/// OptimumConfig.GuiGpuRecordingEligible (cairo-sharp cannot reference the API
	/// contracts assembly). A missing type keeps vanilla behaviour.
	/// </summary>
	internal static class CairoGpuPolicy
	{
		static Func<bool> eligible;
		static volatile bool resolved;
		/// <summary>Per-thread test hook for surface auto-recording; null uses the process value.</summary>
		[ThreadStatic] internal static bool? AutoRecordOverride;
		/// <summary>Process-wide test hook for surface auto-recording; null uses eligibility.</summary>
		internal static volatile object AutoRecordProcessOverride;

		internal static bool RecordingEligible {
			get {
				if (!resolved) Resolve ();
				try { return eligible != null && eligible (); }
				catch { return false; }
			}
		}

		/// <summary>True when new ARGB32 image surfaces start as display lists.</summary>
		internal static bool AutoRecording {
			get {
				bool? value = AutoRecordOverride;
				if (value.HasValue) return value.Value;
				if (AutoRecordProcessOverride is bool process) return process;
				return RecordingEligible;
			}
		}

		static void Resolve ()
		{
			try {
				Type config = Type.GetType ("Vintagestory.API.Config.OptimumConfig, Optimum.Api.Contracts", false);
				PropertyInfo property = config?.GetProperty ("GuiGpuRecordingEligible", BindingFlags.Public | BindingFlags.Static);
				MethodInfo getter = property?.GetGetMethod ();
				if (getter != null && getter.ReturnType == typeof (bool))
					eligible = (Func<bool>)Delegate.CreateDelegate (typeof (Func<bool>), getter);
			} catch { eligible = null; }
			resolved = true;
		}
	}

	/// <summary>
	/// ShowText / ShowGlyphs with Cairo layout captured for Skia. Gray text uses Cairo's hinted
	/// outlines; certified Subpixel font cases use Skia LCD glyph runs. Native materialization
	/// replays the original call for transforms, unsupported fonts and other unsupported cases.
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
		[DllImport (cairo, CallingConvention = CallingConvention.Cdecl)]
		static extern IntPtr cairo_scaled_font_get_font_face (IntPtr scaledFont);
		[DllImport (cairo, CallingConvention = CallingConvention.Cdecl)]
		static extern IntPtr cairo_toy_font_face_get_family (IntPtr fontFace);
		[DllImport (cairo, CallingConvention = CallingConvention.Cdecl)]
		static extern FontSlant cairo_toy_font_face_get_slant (IntPtr fontFace);
		[DllImport (cairo, CallingConvention = CallingConvention.Cdecl)]
		static extern FontWeight cairo_toy_font_face_get_weight (IntPtr fontFace);
		[DllImport ("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)]
		static extern IntPtr FcPatternCreate ();
		[DllImport ("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)]
		static extern int FcPatternAddString (IntPtr pattern, [MarshalAs (UnmanagedType.LPUTF8Str)] string objectName, [MarshalAs (UnmanagedType.LPUTF8Str)] string value);
		[DllImport ("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)]
		static extern int FcPatternAddInteger (IntPtr pattern, [MarshalAs (UnmanagedType.LPUTF8Str)] string objectName, int value);
		[DllImport ("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)]
		static extern void FcConfigSubstitute (IntPtr config, IntPtr pattern, int kind);
		[DllImport ("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)]
		static extern void FcDefaultSubstitute (IntPtr pattern);
		[DllImport ("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)]
		static extern IntPtr FcFontMatch (IntPtr config, IntPtr pattern, out int result);
		[DllImport ("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)]
		static extern int FcPatternGetString (IntPtr pattern, [MarshalAs (UnmanagedType.LPUTF8Str)] string objectName, int index, out IntPtr value);
		[DllImport ("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)]
		static extern int FcPatternGetInteger (IntPtr pattern, [MarshalAs (UnmanagedType.LPUTF8Str)] string objectName, int index, out int value);
		[DllImport ("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)]
		static extern void FcPatternDestroy (IntPtr pattern);

		/// <summary>Records <c>cairo_show_text</c>. Returns null without failure when Cairo draws
		/// nothing (error state, no glyphs); <paramref name="advance"/> is the current point Cairo
		/// moves to, if any.</summary>
		internal static RecordedDrawing CaptureText (IntPtr cr, byte[] utf8, RecordedClip[] clips, Matrix sourceCtm, SurfaceRecorder target,
			out string failure, out PointD? advance)
		{
			failure = null; advance = null;
			if (NativeMethods.cairo_status (cr) != Status.Success) return null;
			if (!CheckFont (cr, out IntPtr scaledFont, out bool subpixel, out failure)) return null;
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
				RecordedDrawing drawing = CaptureGlyphs (cr, scaledFont, glyphs, count, clips, sourceCtm, target, subpixel, out failure);
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
			if (!CheckFont (cr, out IntPtr scaledFont, out bool subpixel, out failure)) return null;
			var copy = new byte[count * GlyphSize];
			Marshal.Copy (glyphs, copy, 0, copy.Length);
			RecordedDrawing drawing = CaptureGlyphs (cr, scaledFont, glyphs, count, clips, sourceCtm, target, subpixel, out failure);
			if (drawing != null) drawing.TextGlyphs = copy;
			return drawing;
		}

		static bool CheckFont (IntPtr cr, out IntPtr scaledFont, out bool subpixel, out string failure)
		{
			failure = null; subpixel = false;
			scaledFont = NativeMethods.cairo_get_scaled_font (cr);
			if (scaledFont == IntPtr.Zero || cairo_scaled_font_status (scaledFont) != Status.Success) { failure = "text-scaled-font"; return false; }
			IntPtr options = NativeMethods.cairo_font_options_create ();
			Antialias antialias;
			try {
				NativeMethods.cairo_get_font_options (cr, options);
				antialias = NativeMethods.cairo_font_options_get_antialias (options);
			} finally { NativeMethods.cairo_font_options_destroy (options); }
			if (antialias != Antialias.Gray && antialias != Antialias.Subpixel) { failure = "text-antialias-" + antialias; return false; }
			subpixel = antialias == Antialias.Subpixel;
			var ctm = new Matrix ();
			NativeMethods.cairo_get_matrix (cr, ctm);
			if (ctm.Xy != 0 || ctm.Yx != 0 || ctm.Xx <= 0 || ctm.Yy <= 0) { failure = "text-transform"; return false; }
			var font = new Matrix ();
			cairo_get_font_matrix (cr, font);
			double size = Math.Max (Math.Abs (font.Xx * ctm.Xx) + Math.Abs (font.Xy * ctm.Xx), Math.Abs (font.Yy * ctm.Yy) + Math.Abs (font.Yx * ctm.Yy));
			if (!(size > 0) || size > MaxDevicePixelSize) { failure = "text-size"; return false; }
			return true;
		}

		static RecordedDrawing CaptureGlyphs (IntPtr cr, IntPtr scaledFont, IntPtr glyphs, int count, RecordedClip[] clips, Matrix sourceCtm, SurfaceRecorder target, bool subpixel, out string failure)
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
			IntPtr rounded = subpixel ? IntPtr.Zero : Marshal.AllocHGlobal (count * GlyphSize);
			IntPtr scratchSurface = subpixel ? IntPtr.Zero : NativeMethods.cairo_image_surface_create (Format.Argb32, 1, 1);
			IntPtr scratch = subpixel ? IntPtr.Zero : NativeMethods.cairo_create (scratchSurface);
			try {
				var buffer = new byte[count * GlyphSize];
				Marshal.Copy (glyphs, buffer, 0, buffer.Length);
				if (!subpixel) Marshal.Copy (buffer, 0, rounded, buffer.Length);
				var skiaGlyphs = subpixel ? new ushort[count] : null;
				var skiaPositions = subpixel ? new SKPoint[count] : null;
				for (int i = 0; i < count; i++) {
					ulong glyphIndex = unchecked ((ulong)Marshal.ReadInt64 (IntPtr.Add (glyphs, i * GlyphSize)));
					if (subpixel && glyphIndex > ushort.MaxValue) { failure = "text-glyph-index"; return null; }
					double gx = Marshal.PtrToStructure<double> (IntPtr.Add (glyphs, i * GlyphSize + 8));
					double gy = Marshal.PtrToStructure<double> (IntPtr.Add (glyphs, i * GlyphSize + 16));
					double dx = QuantizeOrigin (ctm.Xx * gx + ctm.X0, steps);
					double dy = QuantizeOrigin (ctm.Yy * gy + ctm.Y0, steps);
					if (!subpixel) {
						Marshal.StructureToPtr ((dx - ctm.X0) / ctm.Xx, IntPtr.Add (rounded, i * GlyphSize + 8), false);
						Marshal.StructureToPtr ((dy - ctm.Y0) / ctm.Yy, IntPtr.Add (rounded, i * GlyphSize + 16), false);
					}
					if (subpixel) { skiaGlyphs[i] = (ushort)glyphIndex; skiaPositions[i] = new SKPoint ((float)dx, (float)dy); }
				}
				IntPtr pathContext = IntPtr.Zero;
				if (!subpixel) {
					// Gray mode still uses Cairo's hinted outline until Skia passes visual parity.
					NativeMethods.cairo_set_matrix (scratch, ctm);
					NativeMethods.cairo_set_scaled_font (scratch, scaledFont);
					NativeMethods.cairo_glyph_path (scratch, rounded, count);
					if (NativeMethods.cairo_status (scratch) != Status.Success) { failure = "text-glyph-path"; return null; }
					pathContext = scratch;
				}
				RecordedDrawing drawing = RecordedDrawing.Capture (cr, RecordedDrawKind.Fill, 1, clips, sourceCtm, target, out failure,
					pathContext: pathContext, textDrawing: subpixel, capturePath: !subpixel);
				if (drawing == null) return null;
				drawing.FillRule = FillRule.Winding;
				drawing.ScaledFont = new ScaledFont (scaledFont, false);
				if (subpixel && !TryCreateSubpixelTextBlob (cr, scaledFont, skiaGlyphs, skiaPositions, drawing, out _)) {
					// Keep the established per-surface Subpixel fallback category accepted by
					// the hardware certification gate.
					failure = "text-antialias-Subpixel";
					drawing.Dispose ();
					return null;
				}
				return drawing;
			} finally {
				if (rounded != IntPtr.Zero) Marshal.FreeHGlobal (rounded);
				if (scratch != IntPtr.Zero) NativeMethods.cairo_destroy (scratch);
				if (scratchSurface != IntPtr.Zero) NativeMethods.cairo_surface_destroy (scratchSurface);
			}
		}

		static bool TryCreateSubpixelTextBlob (IntPtr cr, IntPtr scaledFont, ushort[] glyphs, SKPoint[] positions, RecordedDrawing drawing, out string failure)
		{
			failure = null;
			IntPtr face = cairo_scaled_font_get_font_face (scaledFont);
			if (face == IntPtr.Zero || NativeMethods.cairo_font_face_get_type (face) != FontType.Toy) { failure = "text-font-face"; return false; }
			IntPtr familyPointer = cairo_toy_font_face_get_family (face);
			string family = familyPointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi (familyPointer);
			if (string.IsNullOrWhiteSpace (family)) { failure = "text-font-family"; return false; }

			var ctm = new Matrix ();
			var fontMatrix = new Matrix ();
			NativeMethods.cairo_get_matrix (cr, ctm);
			cairo_get_font_matrix (cr, fontMatrix);
			// Positioned glyphs are sent to Skia in device space. Until nonuniform font/CTM
			// transforms have dedicated parity fixtures, reject them instead of distorting text.
			if (Math.Abs (ctm.Xx - ctm.Yy) > 1e-6 || Math.Abs (fontMatrix.Xx - fontMatrix.Yy) > 1e-6 ||
				fontMatrix.Xy != 0 || fontMatrix.Yx != 0) { failure = "text-transform"; return false; }
			double deviceSize = fontMatrix.Xx * ctm.Xx;
			if (!(deviceSize > 0) || deviceSize > MaxDevicePixelSize) { failure = "text-size"; return false; }

			FontSlant slant = cairo_toy_font_face_get_slant (face);
			FontWeight weight = cairo_toy_font_face_get_weight (face);
			bool supportedFace =
				(string.Equals (family, "sans-serif", StringComparison.OrdinalIgnoreCase) && weight == FontWeight.Normal) ||
				(string.Equals (family, "Montserrat", StringComparison.OrdinalIgnoreCase) && weight == FontWeight.Normal) ||
				(string.Equals (family, "Lora", StringComparison.OrdinalIgnoreCase) && (weight == FontWeight.Normal || weight == FontWeight.Bold)) ||
				(string.Equals (family, "Almendra", StringComparison.OrdinalIgnoreCase) && weight == FontWeight.Normal);
			if (!supportedFace) { failure = "text-subpixel-font-parity"; return false; }
			using (SKTypeface typeface = MatchCairoTypeface (family, slant, weight)) {
				if (typeface == null) { failure = "text-font-unavailable"; return false; }
				using (var font = new SKFont (typeface, (float)deviceSize)) {
					font.Edging = SKFontEdging.SubpixelAntialias;
					font.Subpixel = true;
					// Slight hinting passes the local LCD policy for these faces; Full did not.
					font.Hinting = SKFontHinting.Slight;
					using (var builder = new SKTextBlobBuilder ()) {
						builder.AddPositionedRun (glyphs, font, positions);
						drawing.SubpixelTextBlob = builder.Build ();
					}
				}
			}
			if (drawing.SubpixelTextBlob == null) { failure = "text-skia-blob"; return false; }
			return true;
		}

		static SKTypeface MatchCairoTypeface (string family, FontSlant slant, FontWeight weight)
		{
			// Cairo's toy font backend resolves names through Fontconfig. Ask that same
			// configuration for the file and face index, then give the exact face to Skia;
			// Skia's independent font-manager cache can miss fonts registered at runtime.
			IntPtr request = IntPtr.Zero, match = IntPtr.Zero;
			try {
				request = FcPatternCreate ();
				if (request != IntPtr.Zero &&
					FcPatternAddString (request, "family", family) != 0 &&
					FcPatternAddInteger (request, "weight", weight == FontWeight.Bold ? 200 : 80) != 0 &&
					FcPatternAddInteger (request, "slant", slant == FontSlant.Italic ? 100 : slant == FontSlant.Oblique ? 110 : 0) != 0) {
					FcConfigSubstitute (IntPtr.Zero, request, 0);
					FcDefaultSubstitute (request);
					match = FcFontMatch (IntPtr.Zero, request, out _);
					if (match != IntPtr.Zero && FcPatternGetString (match, "file", 0, out IntPtr file) == 0 &&
						FcPatternGetInteger (match, "index", 0, out int index) == 0) {
						string path = Marshal.PtrToStringAnsi (file);
						if (!string.IsNullOrEmpty (path)) return SKTypeface.FromFile (path, index);
					}
				}
			} catch (DllNotFoundException) {
				// Non-Fontconfig platforms use Skia's native family matcher below.
			} catch (EntryPointNotFoundException) {
				// Older Fontconfig builds may not expose the required matching API.
			} finally {
				if (match != IntPtr.Zero) FcPatternDestroy (match);
				if (request != IntPtr.Zero) FcPatternDestroy (request);
			}
			SKFontStyleSlant skiaSlant = slant == FontSlant.Italic ? SKFontStyleSlant.Italic :
				slant == FontSlant.Oblique ? SKFontStyleSlant.Oblique : SKFontStyleSlant.Upright;
			var style = new SKFontStyle (weight == FontWeight.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
				SKFontStyleWidth.Normal, skiaSlant);
			return SKFontManager.Default.MatchFamily (family, style);
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
