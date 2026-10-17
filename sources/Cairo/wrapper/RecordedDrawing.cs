using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Cairo
{
	/// <summary>
	/// Device-space copy of a Cairo path. Coordinates are Cairo's 24.8 fixed values, so a
	/// native replay under the identity matrix reproduces the original path exactly.
	/// </summary>
	internal sealed class RecordedPath
	{
		internal const byte Move = 0, Line = 1, Curve = 2, Close = 3;
		readonly byte[] ops;
		readonly double[] points;
		internal RecordedPath (byte[] ops, double[] points) { this.ops = ops; this.points = points; }
		internal int OperationCount => ops.Length;
		internal bool HasCurves => Array.IndexOf (ops, Curve) >= 0;

		[StructLayout (LayoutKind.Sequential)]
		struct NativePath { internal Status Status; internal IntPtr Data; internal int Count; }

		/// <summary>Copies the current path of <paramref name="cr"/> into device space without
		/// touching the live context state.</summary>
		internal static RecordedPath CaptureDevice (IntPtr cr, Matrix ctm, out string failure, bool flat = false)
		{
			failure = null;
			// The flat copy uses Cairo's own curve decomposition at the context tolerance, so
			// Skia rasterizes the same polygon that Cairo fills.
			IntPtr handle = flat ? NativeMethods.cairo_copy_path_flat (cr) : NativeMethods.cairo_copy_path (cr);
			if (handle == IntPtr.Zero) { failure = "path-copy-failed"; return null; }
			try {
				NativePath data = Marshal.PtrToStructure<NativePath> (handle);
				if (data.Status != Status.Success) { failure = "path-status-" + data.Status; return null; }
				int opCount = 0, pointCount = 0;
				for (int index = 0; index < data.Count;) {
					IntPtr p = IntPtr.Add (data.Data, checked(index * 16));
					int kind = Marshal.ReadInt32 (p), length = Marshal.ReadInt32 (p, 4);
					int expected = kind == 0 || kind == 1 ? 2 : kind == 2 ? 4 : kind == 3 ? 1 : 0;
					if (expected == 0 || length != expected || index + length > data.Count) { failure = "path-malformed"; return null; }
					opCount++; pointCount += (length - 1) * 2;
					index += length;
				}
				var ops = new byte[opCount];
				var points = new double[pointCount];
				int op = 0, pt = 0;
				for (int index = 0; index < data.Count;) {
					IntPtr p = IntPtr.Add (data.Data, checked(index * 16));
					int kind = Marshal.ReadInt32 (p), length = Marshal.ReadInt32 (p, 4);
					ops[op++] = (byte)kind;
					for (int i = 1; i < length; i++) {
						double x = BitConverter.Int64BitsToDouble (Marshal.ReadInt64 (p, i * 16));
						double y = BitConverter.Int64BitsToDouble (Marshal.ReadInt64 (p, i * 16 + 8));
						// copy_path reports user space through the inverse CTM; transform back
						// and snap to Cairo's 24.8 fixed grid, which the device path lies on.
						double dx = ctm.Xx * x + ctm.Xy * y + ctm.X0;
						double dy = ctm.Yx * x + ctm.Yy * y + ctm.Y0;
						points[pt++] = SnapFixed (dx);
						points[pt++] = SnapFixed (dy);
					}
					index += length;
				}
				return new RecordedPath (ops, points);
			} finally { NativeMethods.cairo_path_destroy (handle); }
		}

		static double SnapFixed (double value)
		{
			if (!double.IsFinite (value)) return value;
			return Math.Round (value * 256.0, MidpointRounding.ToEven) / 256.0;
		}

		internal void AppendNative (IntPtr cr)
		{
			int pt = 0;
			for (int i = 0; i < ops.Length; i++) {
				switch (ops[i]) {
				case Move: NativeMethods.cairo_move_to (cr, points[pt], points[pt + 1]); pt += 2; break;
				case Line: NativeMethods.cairo_line_to (cr, points[pt], points[pt + 1]); pt += 2; break;
				case Curve: NativeMethods.cairo_curve_to (cr, points[pt], points[pt + 1], points[pt + 2], points[pt + 3], points[pt + 4], points[pt + 5]); pt += 6; break;
				case Close: NativeMethods.cairo_close_path (cr); break;
				}
			}
		}

		/// <summary>True when every segment, including the implicit closing segment of each
		/// sub-path, is horizontal or vertical.</summary>
		internal bool IsRectilinear ()
		{
			int pt = 0;
			double startX = 0, startY = 0, x = 0, y = 0;
			bool open = false;
			for (int i = 0; i < ops.Length; i++) {
				switch (ops[i]) {
				case Move:
					if (open && x != startX && y != startY) return false;
					startX = x = points[pt]; startY = y = points[pt + 1]; pt += 2; open = true;
					break;
				case Line:
					if (points[pt] != x && points[pt + 1] != y) return false;
					x = points[pt]; y = points[pt + 1]; pt += 2;
					break;
				case Curve: return false;
				case Close:
					if (x != startX && y != startY) return false;
					x = startX; y = startY; open = false;
					break;
				}
			}
			return !open || x == startX || y == startY;
		}

		// Cairo's aliased rasterizer samples pixel centres with half-open [start, end)
		// edges. Moving every rectilinear edge to the pixel boundary ceil(e - 0.5) keeps the
		// sampled centres and removes centre-on-edge ties that Skia resolves differently.
		static float SnapAliased (double value) => (float)Math.Ceiling (value - 0.5);

		internal SKPath ToSkia (SKPathFillType fillType, bool snapAliased = false)
		{
			var path = new SKPath { FillType = fillType };
			int pt = 0;
			if (snapAliased) {
				for (int i = 0; i < ops.Length; i++) {
					switch (ops[i]) {
					case Move: path.MoveTo (SnapAliased (points[pt]), SnapAliased (points[pt + 1])); pt += 2; break;
					case Line: path.LineTo (SnapAliased (points[pt]), SnapAliased (points[pt + 1])); pt += 2; break;
					case Close: path.Close (); break;
					default: throw new NotSupportedException ("Aliased recorded geometry must be rectilinear.");
					}
				}
				return path;
			}
			for (int i = 0; i < ops.Length; i++) {
				switch (ops[i]) {
				case Move: path.MoveTo ((float)points[pt], (float)points[pt + 1]); pt += 2; break;
				case Line: path.LineTo ((float)points[pt], (float)points[pt + 1]); pt += 2; break;
				case Curve: path.CubicTo ((float)points[pt], (float)points[pt + 1], (float)points[pt + 2], (float)points[pt + 3], (float)points[pt + 4], (float)points[pt + 5]); pt += 6; break;
				case Close: path.Close (); break;
				}
			}
			return path;
		}

		internal bool AllFinite ()
		{
			for (int i = 0; i < points.Length; i++) if (!double.IsFinite (points[i]) || Math.Abs (points[i]) > 8388607) return false;
			return true;
		}
	}

	internal sealed class RecordedClip
	{
		internal readonly RecordedPath Path;
		internal readonly FillRule FillRule;
		internal readonly Antialias Antialias;
		internal readonly double Tolerance;
		internal readonly RecordedPath FlatPath;
		SKPath skia;
		internal RecordedClip (RecordedPath path, FillRule fillRule, Antialias antialias, double tolerance, RecordedPath flatPath = null)
		{ Path = path; FillRule = fillRule; Antialias = antialias; Tolerance = tolerance; FlatPath = flatPath; }

		internal static RecordedClip Capture (IntPtr cr, Matrix ctm, out string failure)
		{
			RecordedPath path = RecordedPath.CaptureDevice (cr, ctm, out failure);
			if (path == null) return null;
			RecordedPath flat = null;
			if (path.HasCurves) {
				flat = RecordedPath.CaptureDevice (cr, ctm, out failure, flat: true);
				if (flat == null) return null;
			}
			if (!path.AllFinite () || (flat != null && !flat.AllFinite ())) { failure = "path-out-of-range"; return null; }
			return new RecordedClip (path, NativeMethods.cairo_get_fill_rule (cr), NativeMethods.cairo_get_antialias (cr), NativeMethods.cairo_get_tolerance (cr), flat);
		}

		internal void ApplyNative (IntPtr cr)
		{
			NativeMethods.cairo_identity_matrix (cr);
			NativeMethods.cairo_new_path (cr);
			Path.AppendNative (cr);
			NativeMethods.cairo_set_fill_rule (cr, FillRule);
			NativeMethods.cairo_set_antialias (cr, Antialias);
			NativeMethods.cairo_set_tolerance (cr, Tolerance);
			NativeMethods.cairo_clip (cr);
		}

		internal void ApplySkia (SKCanvas canvas)
		{
			skia ??= (FlatPath ?? Path).ToSkia (FillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding, Antialias == Antialias.None);
			canvas.ClipPath (skia, SKClipOperation.Intersect, Antialias != Antialias.None);
		}
	}

	internal enum RecordedDrawKind { Fill, Stroke, Paint }

	/// <summary>Per-gstate state the recorder mirrors because Cairo cannot report it.</summary>
	internal sealed class RecordingGraphicsState
	{
		internal static readonly RecordedClip[] NoClips = new RecordedClip[0];
		internal Matrix SourceCtm = new Matrix ();
		internal IntPtr SourceSurface;
		internal RecordedClip[] Clips = NoClips;
		internal RecordingGraphicsState Clone () => new RecordingGraphicsState { SourceCtm = SourceCtm, SourceSurface = SourceSurface, Clips = Clips };
	}

	/// <summary>One Cairo fill/stroke/paint with all graphics state captured at call time.</summary>
	internal sealed class RecordedDrawing : RecordedCommand
	{
		internal const int MaxSourceDepth = 16;

		internal RecordedDrawKind Kind;
		internal RecordedPath Path;
		// Cairo-flattened copy of Path for Skia; null when Path has no curves.
		internal RecordedPath FlatPath;
		internal RecordedClip[] Clips;
		internal RecordedSource Source;
		internal Operator Operator;
		internal Antialias Antialias;
		internal FillRule FillRule;
		internal double Tolerance;
		internal Matrix Ctm;
		internal double LineWidth, MiterLimit, DashOffset;
		internal LineCap LineCap;
		internal LineJoin LineJoin;
		internal double[] Dash;
		internal double Alpha = 1;
		// Cairo text layout is retained for native replay. Compatible Gray and Subpixel glyph
		// runs can use a Skia text blob; other Gray text uses Cairo's hinted outline as a path.
		internal byte[] TextUtf8;
		internal byte[] TextGlyphs;
		internal PointD TextOrigin;
		internal ScaledFont ScaledFont;
		internal SKTextBlob TextBlob;
		internal bool IsText => ScaledFont != null;

		internal override void Dispose ()
		{
			ScaledFont?.Dispose ();
			ScaledFont = null;
			TextBlob?.Dispose ();
			TextBlob = null;
			skiaPath?.Dispose ();
			skiaPath = null;
		}

		/// <summary>Paint with Clear, or Source with a transparent solid colour, and no clip:
		/// every pixel becomes transparent (ctx.Clear in the GUI code).</summary>
		internal bool IsFullClear =>
			Kind == RecordedDrawKind.Paint && Clips.Length == 0 && Alpha >= 1 &&
			(Operator == Operator.Clear || (Operator == Operator.Source && Source is RecordedSolidSource solid && solid.IsTransparent));
		SKPath skiaPath;

		static readonly RecordedClip[] NoClips = new RecordedClip[0];

		/// <summary>
		/// Captures the drawing state of <paramref name="cr"/>. Returns null with a null
		/// <paramref name="failure"/> when Cairo would draw nothing (error state, zero
		/// width stroke); returns null with a reason when the GPU path cannot reproduce it.
		/// </summary>
		internal static RecordedDrawing Capture (IntPtr cr, RecordedDrawKind kind, double alpha, RecordedClip[] clips,
			Matrix sourceCtm, SurfaceRecorder target, out string failure, IntPtr pathContext = default, bool textDrawing = false, bool capturePath = true)
		{
			bool text = textDrawing || pathContext != IntPtr.Zero;
			failure = null;
			if (NativeMethods.cairo_status (cr) != Status.Success) return null; // Cairo ignores drawing in an error state.
			var drawing = new RecordedDrawing {
				Kind = kind,
				Clips = clips ?? NoClips,
				Operator = NativeMethods.cairo_get_operator (cr),
				Antialias = NativeMethods.cairo_get_antialias (cr),
				FillRule = NativeMethods.cairo_get_fill_rule (cr),
				Tolerance = NativeMethods.cairo_get_tolerance (cr),
				Ctm = new Matrix (),
				Alpha = alpha
			};
			NativeMethods.cairo_get_matrix (cr, drawing.Ctm);
			// Screen parity is verified for geometry and paint; keep text on its existing fallback.
			if (!IsSupportedOperator (drawing.Operator) || (text && drawing.Operator == Operator.Screen)) { failure = "operator-" + drawing.Operator; return null; }
			if (!text && !IsSupportedAntialias (drawing.Antialias)) { failure = "antialias-" + drawing.Antialias; return null; }
			foreach (RecordedClip clip in drawing.Clips) {
				if (!IsSupportedAntialias (clip.Antialias)) { failure = "clip-antialias-" + clip.Antialias; return null; }
				if (clip.Antialias == Antialias.None && !clip.Path.IsRectilinear ()) { failure = "aliased-geometry"; return null; }
			}
			if (alpha != 1) {
				// Paint-with-alpha is a uniform mask; only operators that are linear in the
				// source and bounded by it give the same result as a source-alpha multiply.
				if (drawing.Operator != Operator.Over && drawing.Operator != Operator.Atop && drawing.Operator != Operator.DestOver) { failure = "paint-alpha-operator-" + drawing.Operator; return null; }
				if (alpha <= 0) return null;
			}
			if (kind == RecordedDrawKind.Stroke) {
				drawing.LineWidth = NativeMethods.cairo_get_line_width (cr);
				if (!(drawing.LineWidth > 0)) return null; // Cairo strokes nothing at zero width.
				// Aliased strokes produce arbitrary outlines whose centre sampling Skia does not
				// reproduce exactly.
				if (drawing.Antialias == Antialias.None) { failure = "aliased-geometry"; return null; }
				drawing.LineCap = NativeMethods.cairo_get_line_cap (cr);
				drawing.LineJoin = NativeMethods.cairo_get_line_join (cr);
				drawing.MiterLimit = NativeMethods.cairo_get_miter_limit (cr);
				int dashCount = NativeMethods.cairo_get_dash_count (cr);
				if (dashCount > 0) {
					IntPtr buffer = Marshal.AllocHGlobal (checked(dashCount * sizeof (double)));
					try {
						NativeMethods.cairo_get_dash (cr, buffer, out drawing.DashOffset);
						drawing.Dash = new double[dashCount];
						Marshal.Copy (buffer, drawing.Dash, 0, dashCount);
					} finally { Marshal.FreeHGlobal (buffer); }
					int intervals = dashCount % 2 == 0 ? dashCount : dashCount * 2;
					for (int i = 0; i < intervals; i += 2)
						if (drawing.Dash[i % dashCount] == 0 && drawing.LineCap != LineCap.Butt) { failure = "dash-zero-length-cap"; return null; }
				}
				if (Math.Abs (drawing.Ctm.Xx * drawing.Ctm.Yy - drawing.Ctm.Xy * drawing.Ctm.Yx) < 1e-12) { failure = "stroke-singular-matrix"; return null; }
			}
			if (kind != RecordedDrawKind.Paint && capturePath) {
				IntPtr pathSource = pathContext != IntPtr.Zero ? pathContext : cr;
				drawing.Path = RecordedPath.CaptureDevice (pathSource, drawing.Ctm, out failure);
				if (drawing.Path == null) return null;
				if (!drawing.Path.AllFinite ()) { failure = "path-out-of-range"; return null; }
				if (!text && drawing.Antialias == Antialias.None && !drawing.Path.IsRectilinear ()) { failure = "aliased-geometry"; return null; }
				if (drawing.Path.HasCurves) {
					drawing.FlatPath = RecordedPath.CaptureDevice (pathSource, drawing.Ctm, out failure, flat: true);
					if (drawing.FlatPath == null) return null;
					if (!drawing.FlatPath.AllFinite ()) { failure = "path-out-of-range"; return null; }
				}
			}
			drawing.Source = RecordedSource.Capture (cr, sourceCtm, target, out failure);
			if (drawing.Source == null) return null;
			return drawing;
		}

		internal static RecordedDrawing SolidRectangle (double x, double y, double width, double height, double r, double g, double b, double a, Antialias antialias)
		{
			IntPtr surface = NativeMethods.cairo_image_surface_create (Format.Argb32, 1, 1);
			IntPtr cr = NativeMethods.cairo_create (surface);
			try {
				NativeMethods.cairo_set_antialias (cr, antialias);
				NativeMethods.cairo_set_source_rgba (cr, r, g, b, a);
				NativeMethods.cairo_rectangle (cr, x, y, width, height);
				RecordedDrawing drawing = Capture (cr, RecordedDrawKind.Fill, 1, null, new Matrix (), null, out string failure);
				if (drawing == null && failure != null) throw new NotSupportedException ("Recorder helper cannot capture rectangle: " + failure);
				return drawing;
			} finally {
				NativeMethods.cairo_destroy (cr);
				NativeMethods.cairo_surface_destroy (surface);
			}
		}

		internal static bool IsSupportedOperator (Operator value)
		{
			switch (value) {
			case Operator.Clear: case Operator.Source: case Operator.Over: case Operator.Atop:
			case Operator.Dest: case Operator.DestOver: case Operator.DestOut: case Operator.Xor:
			case Operator.Add: case Operator.HardLight:
			case Operator.Screen:
				return true;
			default:
				// In/Out/DestIn/DestAtop are unbounded in Cairo (they clear outside the mask
				// inside the clip); Saturate and the remaining blend modes are unverified.
				return false;
			}
		}

		// Cairo's image backend uses the same analytic rasterizer for every mode except
		// None (binary) and Fast (reduced grid). Skia's antialiased path replaces the
		// former set; None uses Skia's aliased rasterizer.
		internal static bool IsSupportedAntialias (Antialias value) => value != Antialias.Fast;

		internal static SKBlendMode ToSkia (Operator value) => value switch {
			Operator.Clear => SKBlendMode.Clear, Operator.Source => SKBlendMode.Src, Operator.Over => SKBlendMode.SrcOver,
			Operator.Atop => SKBlendMode.SrcATop, Operator.Dest => SKBlendMode.Dst, Operator.DestOver => SKBlendMode.DstOver,
			Operator.DestOut => SKBlendMode.DstOut, Operator.Xor => SKBlendMode.Xor, Operator.Add => SKBlendMode.Plus,
			Operator.HardLight => SKBlendMode.HardLight,
			Operator.Screen => SKBlendMode.Screen,
			_ => throw new NotSupportedException ("Cairo operator has no recorded Skia equivalent: " + value)
		};

		internal static SKMatrix ToSkia (Matrix m) => new SKMatrix ((float)m.Xx, (float)m.Xy, (float)m.X0, (float)m.Yx, (float)m.Yy, (float)m.Y0, 0, 0, 1);

		internal override void ReplayNative (RecordedNativeReplay replay)
		{
			IntPtr cr = replay.Context;
			NativeMethods.cairo_save (cr);
			try {
				foreach (RecordedClip clip in Clips) clip.ApplyNative (cr);
				NativeMethods.cairo_identity_matrix (cr);
				NativeMethods.cairo_new_path (cr);
				if (!IsText) Path?.AppendNative (cr);
				Source.ApplyNative (cr);
				NativeMethods.cairo_set_matrix (cr, Ctm);
				NativeMethods.cairo_set_operator (cr, Operator);
				NativeMethods.cairo_set_antialias (cr, Antialias);
				NativeMethods.cairo_set_fill_rule (cr, FillRule);
				NativeMethods.cairo_set_tolerance (cr, Tolerance);
				if (IsText) {
					RecordedText.ReplayNative (cr, this);
					return;
				}
				switch (Kind) {
				case RecordedDrawKind.Fill:
					NativeMethods.cairo_fill (cr);
					break;
				case RecordedDrawKind.Stroke:
					NativeMethods.cairo_set_line_width (cr, LineWidth);
					NativeMethods.cairo_set_line_cap (cr, LineCap);
					NativeMethods.cairo_set_line_join (cr, LineJoin);
					NativeMethods.cairo_set_miter_limit (cr, MiterLimit);
					if (Dash != null) NativeMethods.cairo_set_dash (cr, Dash, Dash.Length, DashOffset);
					NativeMethods.cairo_stroke (cr);
					break;
				case RecordedDrawKind.Paint:
					if (Alpha >= 1) NativeMethods.cairo_paint (cr);
					else NativeMethods.cairo_paint_with_alpha (cr, Alpha);
					break;
				}
			} finally { NativeMethods.cairo_restore (cr); }
		}

		internal override void DrawSkia (RecordedSkiaReplay replay)
		{
			SKCanvas canvas = replay.Canvas;
			int save = canvas.Save ();
			try {
				canvas.ResetMatrix ();
				foreach (RecordedClip clip in Clips) clip.ApplySkia (canvas);
				using (var paint = new SKPaint { IsAntialias = IsText || Antialias != Antialias.None, BlendMode = ToSkia (Operator), Style = SKPaintStyle.Fill }) {
					using (RecordedSourcePaint source = Source.ConfigurePaint (paint, Alpha, replay)) {
					if (TextBlob != null) {
						canvas.DrawText (TextBlob, 0, 0, paint);
						return;
					}
						switch (Kind) {
						case RecordedDrawKind.Fill:
							canvas.DrawPath (GetSkiaPath (), paint);
							break;
						case RecordedDrawKind.Stroke:
							canvas.DrawPath (GetSkiaPath (), paint);
							break;
						case RecordedDrawKind.Paint:
							canvas.DrawPaint (paint);
							break;
						}
					}
				}
			} finally { canvas.RestoreToCount (save); }
		}

		SKPath GetSkiaPath ()
		{
			if (skiaPath != null) return skiaPath;
			if (Kind == RecordedDrawKind.Fill) {
				skiaPath = (FlatPath ?? Path).ToSkia (FillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding, Antialias == Antialias.None);
				return skiaPath;
			}
			// Strokes are expanded once into a non-zero outline in user space and then mapped
			// to device space. A single fill covers the union of the pen like Cairo's stroker;
			// GPU hairline/analytic stroke renderers can blend retraced segments twice.
			SKMatrix ctm = ToSkia (Ctm);
			if (!ctm.TryInvert (out SKMatrix inverse)) throw new NotSupportedException ("Recorded stroke has a singular matrix.");
			using (SKPath device = (FlatPath ?? Path).ToSkia (SKPathFillType.Winding))
			using (var user = new SKPath (device))
			using (var stroke = new SKPaint {
				Style = SKPaintStyle.Stroke,
				StrokeWidth = (float)LineWidth,
				StrokeCap = (SKStrokeCap)(int)LineCap,
				StrokeJoin = (SKStrokeJoin)(int)LineJoin,
				StrokeMiter = (float)MiterLimit,
				IsAntialias = true
			}) {
				user.Transform (inverse);
				SKPathEffect dash = null;
				try {
					if (Dash != null) {
						int count = Dash.Length % 2 == 0 ? Dash.Length : Dash.Length * 2;
						var intervals = new float[count];
						for (int i = 0; i < count; i++) intervals[i] = (float)Dash[i % Dash.Length];
						dash = SKPathEffect.CreateDash (intervals, (float)DashOffset);
						stroke.PathEffect = dash;
					}
					// A high resolution scale keeps the offset-curve approximation well below the
					// antialiasing step; the default (1) deviates by up to a quarter pixel.
					float resScale = 16f * (float)Math.Max (1, Math.Sqrt (Math.Abs (Ctm.Xx * Ctm.Yy - Ctm.Xy * Ctm.Yx)));
					var outline = new SKPath ();
					if (!stroke.GetFillPath (user, outline, resScale)) outline.Reset ();
					outline.FillType = SKPathFillType.Winding;
					outline.Transform (ctm);
					skiaPath = outline;
				} finally { dash?.Dispose (); }
			}
			return skiaPath;
		}
	}
}
