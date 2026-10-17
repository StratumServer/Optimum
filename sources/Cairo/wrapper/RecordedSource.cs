using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Disposable per-draw Skia resources attached to a paint.</summary>
	internal sealed class RecordedSourcePaint : IDisposable
	{
		readonly SKPaint paint;
		readonly IDisposable[] owned;
		internal RecordedSourcePaint (SKPaint paint, params IDisposable[] owned) { this.paint = paint; this.owned = owned; }
		public void Dispose ()
		{
			paint.Shader = null;
			foreach (IDisposable item in owned) item?.Dispose ();
		}
	}

	/// <summary>Immutable snapshot of a Cairo source pattern at draw time.</summary>
	internal abstract class RecordedSource
	{
		internal abstract void ApplyNative (IntPtr cr);
		internal abstract RecordedSourcePaint ConfigurePaint (SKPaint paint, double alpha, RecordedSkiaReplay replay);

		internal static RecordedSource Capture (IntPtr cr, Matrix sourceCtm, SurfaceRecorder target, out string failure)
		{
			failure = null;
			IntPtr pattern = NativeMethods.cairo_get_source (cr);
			if (pattern == IntPtr.Zero || NativeMethods.cairo_pattern_status (pattern) != Status.Success) { failure = "source-status"; return null; }
			PatternType type = NativeMethods.cairo_pattern_get_type (pattern);
			if (type == PatternType.Solid) {
				NativeMethods.cairo_pattern_get_rgba (pattern, out double r, out double g, out double b, out double a);
				return new RecordedSolidSource (r, g, b, a);
			}
			var patternMatrix = new Matrix ();
			NativeMethods.cairo_pattern_get_matrix (pattern, patternMatrix);
			Extend extend = NativeMethods.cairo_pattern_get_extend (pattern);
			Filter filter = NativeMethods.cairo_pattern_get_filter (pattern);
			Matrix ctm = sourceCtm ?? new Matrix ();
			if (type == PatternType.Linear || type == PatternType.Radial)
				return RecordedGradientSource.Capture (pattern, type, patternMatrix, extend, filter, ctm, out failure);
			if (type == PatternType.Surface)
				return RecordedSurfaceSource.Capture (pattern, patternMatrix, extend, filter, ctm, target, out failure);
			failure = "source-type-" + type;
			return null;
		}

		/// <summary>Device-to-pattern transform used by Cairo: pattern matrix times the inverse
		/// of the user space that was current when the source was set.</summary>
		internal static bool TryDeviceToPattern (Matrix patternMatrix, Matrix sourceCtm, out Matrix result)
		{
			result = null;
			var inverse = (Matrix)sourceCtm.Clone ();
			if (NativeMethods.cairo_matrix_invert (inverse) != Status.Success) return false;
			result = new Matrix ();
			NativeMethods.cairo_matrix_multiply (result, inverse, patternMatrix);
			return true;
		}

		// Cairo stores solid sources as 16-bit premultiplied channels and Pixman takes the
		// high byte. Express that byte exactly through Skia's float colour premultiply.
		internal static SKColorF SolidColor (double r, double g, double b, double a)
		{
			byte alpha = PremultipliedByte (a);
			if (alpha == 0) return new SKColorF (0, 0, 0, 0);
			return new SKColorF (PremultipliedByte (Clamp (r) * Clamp (a)) / (float)alpha,
				PremultipliedByte (Clamp (g) * Clamp (a)) / (float)alpha,
				PremultipliedByte (Clamp (b) * Clamp (a)) / (float)alpha, alpha / 255f);
		}
		internal static byte PremultipliedByte (double value) => (byte)((int)(Clamp (value) * 65535 + .5) >> 8);
		internal static double Clamp (double value) => double.IsNaN (value) ? 0 : Math.Max (0, Math.Min (1, value));
		internal static SKShaderTileMode Tile (Extend value) => value == Extend.Repeat ? SKShaderTileMode.Repeat : value == Extend.Reflect ? SKShaderTileMode.Mirror : value == Extend.Pad ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal;
	}

	internal sealed class RecordedSolidSource : RecordedSource
	{
		readonly double r, g, b, a;
		internal RecordedSolidSource (double r, double g, double b, double a) { this.r = r; this.g = g; this.b = b; this.a = a; }
		internal bool IsTransparent => PremultipliedByte (a) == 0;
		internal override void ApplyNative (IntPtr cr) => NativeMethods.cairo_set_source_rgba (cr, r, g, b, a);
		internal override RecordedSourcePaint ConfigurePaint (SKPaint paint, double alpha, RecordedSkiaReplay replay)
		{
			paint.ColorF = SolidColor (r, g, b, a * Clamp (alpha));
			return new RecordedSourcePaint (paint);
		}
	}

	internal sealed class RecordedGradientSource : RecordedSource
	{
		readonly PatternType type;
		readonly double x0, y0, r0, x1, y1, r1;
		readonly double[] stops; // offset, r, g, b, a
		readonly Matrix patternMatrix, sourceCtm;
		readonly Extend extend;
		readonly Filter filter;

		RecordedGradientSource (PatternType type, double x0, double y0, double r0, double x1, double y1, double r1, double[] stops,
			Matrix patternMatrix, Matrix sourceCtm, Extend extend, Filter filter)
		{
			this.type = type; this.x0 = x0; this.y0 = y0; this.r0 = r0; this.x1 = x1; this.y1 = y1; this.r1 = r1;
			this.stops = stops; this.patternMatrix = patternMatrix; this.sourceCtm = sourceCtm; this.extend = extend; this.filter = filter;
		}

		internal static RecordedSource Capture (IntPtr pattern, PatternType type, Matrix patternMatrix, Extend extend, Filter filter, Matrix sourceCtm, out string failure)
		{
			failure = null;
			// Pixman and Skia classify the discontinuous extension boundaries of None and
			// Repeat differently; keep those on Cairo until an equivalent shader exists.
			if (extend != Extend.Pad && extend != Extend.Reflect) { failure = "gradient-extend-" + extend; return null; }
			double x0, y0, x1, y1, r0 = 0, r1 = 0;
			if (type == PatternType.Linear) {
				NativeMethods.cairo_pattern_get_linear_points (pattern, out x0, out y0, out x1, out y1);
				if (x0 == x1 && y0 == y1) { failure = "gradient-degenerate"; return null; }
			} else {
				NativeMethods.cairo_pattern_get_radial_circles (pattern, out x0, out y0, out r0, out x1, out y1, out r1);
				// Only concentric, expanding radial gradients have a matching Skia definition
				// for the whole plane; focal and cone cases differ at the extension boundary.
				if (x0 != x1 || y0 != y1 || r1 <= r0 || r0 < 0) { failure = "gradient-radial-shape"; return null; }
			}
			NativeMethods.cairo_pattern_get_color_stop_count (pattern, out int count);
			var stops = new double[count * 5];
			for (int i = 0; i < count; i++)
				NativeMethods.cairo_pattern_get_color_stop_rgba (pattern, i, out stops[i * 5], out stops[i * 5 + 1], out stops[i * 5 + 2], out stops[i * 5 + 3], out stops[i * 5 + 4]);
			if (!TryDeviceToPattern (patternMatrix, sourceCtm, out _)) { failure = "gradient-singular-matrix"; return null; }
			return new RecordedGradientSource (type, x0, y0, r0, x1, y1, r1, stops, patternMatrix, (Matrix)sourceCtm.Clone (), extend, filter);
		}

		internal override void ApplyNative (IntPtr cr)
		{
			IntPtr pattern = type == PatternType.Linear
				? NativeMethods.cairo_pattern_create_linear (x0, y0, x1, y1)
				: NativeMethods.cairo_pattern_create_radial (x0, y0, r0, x1, y1, r1);
			try {
				for (int i = 0; i < stops.Length; i += 5)
					NativeMethods.cairo_pattern_add_color_stop_rgba (pattern, stops[i], stops[i + 1], stops[i + 2], stops[i + 3], stops[i + 4]);
				NativeMethods.cairo_pattern_set_matrix (pattern, patternMatrix);
				NativeMethods.cairo_pattern_set_extend (pattern, extend);
				NativeMethods.cairo_pattern_set_filter (pattern, filter);
				NativeMethods.cairo_set_matrix (cr, sourceCtm);
				NativeMethods.cairo_set_source (cr, pattern);
			} finally { NativeMethods.cairo_pattern_destroy (pattern); }
		}

		internal override RecordedSourcePaint ConfigurePaint (SKPaint paint, double alpha, RecordedSkiaReplay replay)
		{
			int count = stops.Length / 5;
			if (count == 0) { paint.Color = SKColors.Transparent; return new RecordedSourcePaint (paint); }
			var colors = new SKColorF[Math.Max (2, count)];
			var positions = new float[colors.Length];
			for (int i = 0; i < count; i++) {
				colors[i] = new SKColorF ((float)Clamp (stops[i * 5 + 1]), (float)Clamp (stops[i * 5 + 2]), (float)Clamp (stops[i * 5 + 3]), (float)Clamp (stops[i * 5 + 4]));
				positions[i] = (float)Clamp (stops[i * 5]);
			}
			if (count == 1) { colors[1] = colors[0]; positions[0] = 0; positions[1] = 1; }
			TryDeviceToPattern (patternMatrix, sourceCtm, out Matrix deviceToPattern);
			SKMatrix local = RecordedDrawing.ToSkia (deviceToPattern);
			if (!local.TryInvert (out SKMatrix patternToDevice)) throw new NotSupportedException ("Recorded gradient has a singular matrix.");
			SKShaderTileMode tile = Tile (extend);
			SKShader shader = type == PatternType.Linear
				? SKShader.CreateLinearGradient (new SKPoint ((float)x0, (float)y0), new SKPoint ((float)x1, (float)y1), colors, null, positions, tile, patternToDevice)
				: SKShader.CreateTwoPointConicalGradient (new SKPoint ((float)x0, (float)y0), (float)r0, new SKPoint ((float)x1, (float)y1), (float)r1, colors, null, positions, tile, patternToDevice);
			paint.Shader = shader;
			paint.ColorF = new SKColorF (1, 1, 1, (float)Clamp (alpha));
			return new RecordedSourcePaint (paint, shader);
		}
	}

	/// <summary>A surface pattern. The source is either a pixel copy of a native image surface
	/// or an immutable display-list snapshot of another recording surface.</summary>
	internal sealed class RecordedSurfaceSource : RecordedSource
	{
		readonly byte[] pixels; // Cairo ARGB32 layout, stride = width * 4
		readonly SurfaceRecorderSnapshot recording;
		internal SurfaceRecorderSnapshot Recording => recording;
		readonly int width, height;
		readonly Matrix patternMatrix, sourceCtm;
		readonly Extend extend;
		readonly Filter filter;
		readonly bool nearest;
		SKImage image;

		RecordedSurfaceSource (byte[] pixels, SurfaceRecorderSnapshot recording, int width, int height, Matrix patternMatrix, Matrix sourceCtm, Extend extend, Filter filter, bool nearest)
		{
			this.pixels = pixels; this.recording = recording; this.width = width; this.height = height;
			this.patternMatrix = patternMatrix; this.sourceCtm = sourceCtm; this.extend = extend; this.filter = filter; this.nearest = nearest;
		}

		internal static RecordedSource Capture (IntPtr pattern, Matrix patternMatrix, Extend extend, Filter filter, Matrix sourceCtm, SurfaceRecorder target, out string failure)
		{
			failure = null;
			if (NativeMethods.cairo_pattern_get_surface (pattern, out IntPtr surface) != Status.Success || surface == IntPtr.Zero) { failure = "surface-source-missing"; return null; }
			if (NativeMethods.cairo_surface_get_type (surface) != SurfaceType.Image) { failure = "surface-source-type"; return null; }
			Format format = NativeMethods.cairo_image_surface_get_format (surface);
			if (format != Format.Argb32 && format != Format.Rgb24) { failure = "surface-source-format-" + format; return null; }
			if (!TryDeviceToPattern (patternMatrix, sourceCtm, out Matrix deviceToPattern)) { failure = "surface-source-singular-matrix"; return null; }
			bool pixelExact = deviceToPattern.Xx == 1 && deviceToPattern.Yy == 1 && deviceToPattern.Xy == 0 && deviceToPattern.Yx == 0 &&
				deviceToPattern.X0 == Math.Floor (deviceToPattern.X0) && deviceToPattern.Y0 == Math.Floor (deviceToPattern.Y0);
			bool nearest;
			if (filter == Filter.Fast || filter == Filter.Nearest || pixelExact) nearest = true;
			else {
				// Cairo uses separable convolution kernels for Good/Best and Pixman's
				// fixed-point bilinear otherwise; neither is reproduced yet.
				failure = "surface-source-filter-" + filter;
				return null;
			}
			int width = NativeMethods.cairo_image_surface_get_width (surface), height = NativeMethods.cairo_image_surface_get_height (surface);
			SurfaceRecorder recorder = SurfaceRecorder.Lookup (surface);
			if (recorder != null) {
				if (ReferenceEquals (recorder, target)) { failure = "surface-source-self"; return null; }
				SurfaceRecorderSnapshot snapshot = recorder.CaptureSnapshot ();
				if (snapshot == null) { failure = "surface-source-recording-state"; return null; }
				if (snapshot.Depth >= RecordedDrawing.MaxSourceDepth) { failure = "surface-source-depth"; return null; }
				return new RecordedSurfaceSource (null, snapshot, width, height, patternMatrix, (Matrix)sourceCtm.Clone (), extend, filter, nearest);
			}
			NativeMethods.cairo_surface_flush (surface);
			IntPtr data = NativeMethods.cairo_image_surface_get_data (surface);
			int stride = NativeMethods.cairo_image_surface_get_stride (surface);
			if (data == IntPtr.Zero && width * height != 0) { failure = "surface-source-data"; return null; }
			var copy = new byte[checked(width * height * 4)];
			for (int y = 0; y < height; y++) Marshal.Copy (IntPtr.Add (data, y * stride), copy, y * width * 4, width * 4);
			return new RecordedSurfaceSource (copy, null, width, height, patternMatrix, (Matrix)sourceCtm.Clone (), extend, filter, nearest) { format = format };
		}

		Format format = Format.Argb32;

		internal override void ApplyNative (IntPtr cr)
		{
			IntPtr surface = NativeMethods.cairo_image_surface_create (recording != null ? Format.Argb32 : format, width, height);
			try {
				if (recording != null) recording.ReplayNative (surface);
				else {
					NativeMethods.cairo_surface_flush (surface);
					IntPtr data = NativeMethods.cairo_image_surface_get_data (surface);
					int stride = NativeMethods.cairo_image_surface_get_stride (surface);
					for (int y = 0; y < height; y++) Marshal.Copy (pixels, y * width * 4, IntPtr.Add (data, y * stride), width * 4);
					NativeMethods.cairo_surface_mark_dirty (surface);
				}
				IntPtr pattern = NativeMethods.cairo_pattern_create_for_surface (surface);
				try {
					NativeMethods.cairo_pattern_set_matrix (pattern, patternMatrix);
					NativeMethods.cairo_pattern_set_extend (pattern, extend);
					NativeMethods.cairo_pattern_set_filter (pattern, filter);
					NativeMethods.cairo_set_matrix (cr, sourceCtm);
					NativeMethods.cairo_set_source (cr, pattern);
				} finally { NativeMethods.cairo_pattern_destroy (pattern); }
			} finally { NativeMethods.cairo_surface_destroy (surface); }
		}

		internal override RecordedSourcePaint ConfigurePaint (SKPaint paint, double alpha, RecordedSkiaReplay replay)
		{
			SKImage source;
			IDisposable owned = null;
			if (recording != null) {
				source = recording.Render (replay);
			} else {
				image ??= CreateImage ();
				source = image;
			}
			TryDeviceToPattern (patternMatrix, sourceCtm, out Matrix deviceToPattern);
			SKMatrix patternToDevice = PixmanSampleMatrix (deviceToPattern);
			SKShaderTileMode tile = Tile (extend);
			SKShader shader = source.ToShader (tile, tile, new SKSamplingOptions (nearest ? SKFilterMode.Nearest : SKFilterMode.Linear), patternToDevice);
			paint.Shader = shader;
			paint.ColorF = new SKColorF (1, 1, 1, (float)Clamp (alpha));
			return new RecordedSourcePaint (paint, shader, owned);
		}

		SKImage CreateImage ()
		{
			byte[] data = pixels;
			if (format == Format.Rgb24) {
				data = (byte[])pixels.Clone ();
				for (int i = 3; i < data.Length; i += 4) data[i] = 255; // Cairo ignores the padding byte.
			}
			var info = new SKImageInfo (width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
			return SKImage.FromPixelCopy (info, data, width * 4) ?? throw new InvalidOperationException ("Skia could not copy a recorded surface source.");
		}

		// Pixman samples nearest texels with a 16.16 fixed-point transform and subtracts one
		// fixed epsilon before truncation. Round the matrix the same way and bias the sample
		// position so that boundary pixels select the same texel as Cairo.
		static SKMatrix PixmanSampleMatrix (Matrix deviceToPattern)
		{
			double Fixed (double v) => Math.Floor (v * 65536.0 + 0.5) / 65536.0;
			const double epsilon = 1.0 / 65536.0;
			var m = new SKMatrix (
				(float)Fixed (deviceToPattern.Xx), (float)Fixed (deviceToPattern.Xy), (float)(Fixed (deviceToPattern.X0) - epsilon),
				(float)Fixed (deviceToPattern.Yx), (float)Fixed (deviceToPattern.Yy), (float)(Fixed (deviceToPattern.Y0) - epsilon),
				0, 0, 1);
			if (!m.TryInvert (out SKMatrix inverse)) throw new NotSupportedException ("Recorded surface source has a singular matrix.");
			return inverse;
		}
	}
}
