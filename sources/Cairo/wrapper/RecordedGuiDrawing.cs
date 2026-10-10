using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Cairo
{
	// Cairo supplies font selection, metrics and double-precision path construction.
	// Only immutable geometry/source snapshots cross into the Skia renderer.
	internal sealed class RecordedGuiDrawing : IDisposable
	{
		internal SKPath Path;
		internal SKPaint Paint;
		internal SKMatrix Matrix;
		internal RecordedGuiClip[] Clips;
		internal bool PaintAll;
		SKImage image;
		SurfaceRecorderSnapshot sourceSnapshot;
		SKShaderTileMode sourceTile;
		SKFilterMode sourceFilter;
		SKMatrix sourceTransform;
		SKShader shader;
		SKPathEffect dash;
		float[] dashIntervals;
		float dashOffset;
		Pattern nativeSource;
		ImageSurface nativeImage;
		Matrix nativeSourceMatrix;

		internal static RecordedGuiDrawing Capture(Context context, bool stroke, bool paintAll,
			RecordedGuiClip[] clips, SKPath textPath = null, double alpha = 1, Matrix sourceMatrix = null, SurfaceRecorderSnapshot sourceSnapshot = null)
		{
			using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.DrawingCapture);
			RecordedGuiDrawing result;
			using (SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.CaptureState))
				result = new RecordedGuiDrawing { Clips = clips, Matrix = ToSkia(context.Matrix), PaintAll = paintAll, sourceSnapshot = sourceSnapshot };
			try {
				result.Path = textPath ?? (paintAll ? null : CopyPath(context));
				using (SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.CaptureState)) {
					if (result.Path != null) result.Path.FillType = context.FillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
					result.Paint = new SKPaint {
						IsAntialias = context.Antialias != Cairo.Antialias.None,
						Style = stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
						StrokeWidth = (float)context.LineWidth,
						StrokeMiter = (float)context.MiterLimit,
						StrokeCap = (SKStrokeCap)(int)context.LineCap,
						StrokeJoin = (SKStrokeJoin)(int)context.LineJoin,
						BlendMode = Blend(context.Operator)
					};
				}
				if (stroke) result.SetDash(context);
				if (stroke && result.Paint.StrokeWidth > 0) {
					// Rasterize a stroke's union once. Ganesh's analytic stroke path can
					// blend retraced segments twice (a closed two-point caret becomes
					// alpha 191 instead of Cairo's 128 at a half-covered pixel).
					var outline = RecordedPreparationCache.StrokeOutline(result.Path, result.Paint, result.dashIntervals, result.dashOffset);
					if (outline != null) {
						result.Path.Dispose(); result.Path = outline;
						result.Paint.Style = SKPaintStyle.Fill;
						result.Paint.PathEffect = null;
					}
				}
				using (SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.SourceCapture))
				using (Pattern source = context.GetSource()) {
					result.SetSource(source, alpha, sourceMatrix);
					if (sourceSnapshot == null) result.FreezeSource(source);
					else {
						result.nativeSource = new SolidPattern(0, 0, 0, 0);
						result.nativeSource.Matrix = source.Matrix;
						result.nativeSource.Extend = source.Extend;
					}
				}
				result.nativeSourceMatrix = sourceMatrix is null ? context.Matrix : (Matrix)sourceMatrix.Clone();
				return result;
			} catch { result.Dispose(); throw; }
		}

		internal void Replay(Context context, Action<Context> operation)
		{
			context.Save();
			try {
				Matrix drawMatrix = context.Matrix;
				context.Matrix = nativeSourceMatrix;
				if (sourceSnapshot != null && nativeImage == null) {
					nativeImage = new ImageSurface(Format.Argb32, sourceSnapshot.Width, sourceSnapshot.Height);
					sourceSnapshot.ReplayNative(nativeImage.NativeHandleForMetadata);
					var pattern = new SurfacePattern(nativeImage) { Matrix = nativeSource.Matrix, Extend = nativeSource.Extend, Filter = sourceFilter == SKFilterMode.Nearest ? Filter.Nearest : Filter.Bilinear };
					nativeSource.Dispose(); nativeSource = pattern;
				}
				context.SetSource(nativeSource);
				context.Matrix = drawMatrix;
				operation(context);
			} finally { context.Restore(); }
		}

		void FreezeSource(Pattern source)
		{
			using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.SourceFreeze);
			IntPtr h = source.NativeHandleForRecorder;
			switch (NativeMethods.cairo_pattern_get_type(h)) {
			case PatternType.Solid:
				NativeMethods.cairo_pattern_get_rgba(h, out double r, out double g, out double b, out double a);
				nativeSource = new SolidPattern(r, g, b, a); break;
			case PatternType.Linear:
				NativeMethods.cairo_pattern_get_linear_points(h, out double x0, out double y0, out double x1, out double y1);
				nativeSource = new LinearGradient(x0, y0, x1, y1); break;
			case PatternType.Radial:
				NativeMethods.cairo_pattern_get_radial_circles(h, out double cx0, out double cy0, out double r0, out double cx1, out double cy1, out double r1);
				nativeSource = new RadialGradient(cx0, cy0, r0, cx1, cy1, r1); break;
			case PatternType.Surface:
				NativeMethods.cairo_pattern_get_surface(h, out IntPtr surface);
				int width = NativeMethods.cairo_image_surface_get_width(surface), height = NativeMethods.cairo_image_surface_get_height(surface);
				Format format = NativeMethods.cairo_image_surface_get_format(surface);
				nativeImage = new ImageSurface(format, width, height);
				int inputStride = NativeMethods.cairo_image_surface_get_stride(surface);
				int outputStride = nativeImage.Stride;
				var row = new byte[checked(width * 4)];
				IntPtr input = NativeMethods.cairo_image_surface_get_data(surface), output = nativeImage.DataPtr;
				for (int y = 0; y < height; y++) { Marshal.Copy(IntPtr.Add(input, y * inputStride), row, 0, row.Length); Marshal.Copy(row, 0, IntPtr.Add(output, y * outputStride), row.Length); }
				nativeImage.MarkDirty();
				nativeSource = new SurfacePattern(nativeImage) { Filter = ((SurfacePattern)source).Filter }; break;
			default: throw new NotSupportedException("Cannot freeze source pattern.");
			}
			if (nativeSource is Gradient gradient) {
				NativeMethods.cairo_pattern_get_color_stop_count(h, out int count);
				for (int i = 0; i < count; i++) {
					NativeMethods.cairo_pattern_get_color_stop_rgba(h, i, out double offset, out double cr, out double cg, out double cb, out double ca);
					gradient.AddColorStop(offset, new Color(cr, cg, cb, ca));
				}
			}
			nativeSource.Matrix = source.Matrix; nativeSource.Extend = source.Extend;
		}

		void SetDash(Context context)
		{
			int count = NativeMethods.cairo_get_dash_count(context.NativeHandleForRecorder);
			if (count == 0) return;
			IntPtr buffer = Marshal.AllocHGlobal(checked(count * sizeof(double)));
			try {
				NativeMethods.cairo_get_dash(context.NativeHandleForRecorder, buffer, out double offset);
				var values = new double[count]; Marshal.Copy(buffer, values, 0, count);
				var intervals = new float[count % 2 == 0 ? count : count * 2];
				for (int i = 0; i < intervals.Length; i++) intervals[i] = (float)values[i % count];
				dashIntervals = intervals; dashOffset = (float)offset;
				dash = SKPathEffect.CreateDash(intervals, dashOffset); Paint.PathEffect = dash;
			} finally { Marshal.FreeHGlobal(buffer); }
		}

		void SetSource(Pattern pattern, double alpha, Matrix sourceMatrix)
		{
			using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.SourceShader);
			IntPtr handle = pattern.NativeHandleForRecorder;
			PatternType type = NativeMethods.cairo_pattern_get_type(handle);
			if (type == PatternType.Solid) {
				NativeMethods.cairo_pattern_get_rgba(handle, out double r, out double g, out double b, out double a);
				// Cairo quantizes premultiplied solid channels to 16 bits, then
				// Pixman takes the high byte. Quantizing straight RGBA first
				// changes low-alpha inputs that partial blur can amplify.
				if (alpha == 1) Paint.ColorF = SolidColor(r, g, b, a);
				else Paint.Color = Color(r, g, b, a * alpha);
				return;
			}
			SKShaderTileMode tile = Tile(pattern.Extend);
			if (type == PatternType.Linear || type == PatternType.Radial) {
				NativeMethods.cairo_pattern_get_color_stop_count(handle, out int count);
				if (count == 0) { Paint.Color = SKColors.Transparent; return; }
				var colors = new SKColor[Math.Max(2, count)]; var positions = new float[colors.Length];
				for (int i = 0; i < count; i++) {
					NativeMethods.cairo_pattern_get_color_stop_rgba(handle, i, out double offset, out double r, out double g, out double b, out double a);
					colors[i] = Color(r, g, b, a); positions[i] = (float)offset;
				}
				if (count == 1) { colors[1] = colors[0]; positions[0] = 0; positions[1] = 1; }
				if (type == PatternType.Linear) {
					NativeMethods.cairo_pattern_get_linear_points(handle, out double x0, out double y0, out double x1, out double y1);
					shader = SKShader.CreateLinearGradient(new SKPoint((float)x0, (float)y0), new SKPoint((float)x1, (float)y1), colors, positions, tile);
				} else {
					NativeMethods.cairo_pattern_get_radial_circles(handle, out double x0, out double y0, out double r0, out double x1, out double y1, out double r1);
					shader = SKShader.CreateTwoPointConicalGradient(new SKPoint((float)x0, (float)y0), (float)r0, new SKPoint((float)x1, (float)y1), (float)r1, colors, positions, tile);
				}
			} else if (type == PatternType.Surface && sourceSnapshot != null) {
				sourceTile = tile; sourceFilter = pattern is SurfacePattern recorded && recorded.Filter == Filter.Nearest ? SKFilterMode.Nearest : SKFilterMode.Linear;
			} else if (type == PatternType.Surface) {
				NativeMethods.cairo_pattern_get_surface(handle, out IntPtr surface);
				Format format = NativeMethods.cairo_image_surface_get_format(surface);
				if (format != Format.Argb32 && format != Format.Rgb24) throw new NotSupportedException("Recorded image pattern format: " + format);
				NativeMethods.cairo_surface_flush(surface);
				int width = NativeMethods.cairo_image_surface_get_width(surface), height = NativeMethods.cairo_image_surface_get_height(surface);
				int stride = NativeMethods.cairo_image_surface_get_stride(surface);
				using (var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, format == Format.Argb32 ? SKAlphaType.Premul : SKAlphaType.Opaque)) {
					var row = new byte[checked(width * 4)];
					IntPtr input = NativeMethods.cairo_image_surface_get_data(surface), output = bitmap.GetPixels();
					for (int y = 0; y < height; y++) { Marshal.Copy(IntPtr.Add(input, checked(y * stride)), row, 0, row.Length); Marshal.Copy(row, 0, IntPtr.Add(output, checked(y * bitmap.RowBytes)), row.Length); }
					image = SKImage.FromBitmap(bitmap);
				}
				shader = image.ToShader(tile, tile, new SKSamplingOptions(pattern is SurfacePattern sp && sp.Filter == Filter.Nearest ? SKFilterMode.Nearest : SKFilterMode.Linear));
			} else throw new NotSupportedException("Recorded source pattern: " + type);
			SKMatrix matrix = ToSkia(pattern.Matrix);
			if (!matrix.TryInvert(out SKMatrix inverse)) throw new InvalidOperationException("Singular source pattern matrix.");
			if (sourceMatrix is not null) {
				if (!Matrix.TryInvert(out SKMatrix drawInverse)) throw new NotSupportedException("Singular drawing transform.");
				inverse = SKMatrix.Concat(SKMatrix.Concat(drawInverse, ToSkia(sourceMatrix)), inverse);
			}
			if (sourceSnapshot != null) { sourceTransform = inverse; Paint.Color = Color(1, 1, 1, alpha); return; }
			SKShader transformed = shader.WithLocalMatrix(inverse);
			if (!ReferenceEquals(shader, transformed)) shader.Dispose();
			shader = transformed;
			Paint.Shader = shader; Paint.Color = Color(1, 1, 1, alpha);
		}

		internal void Draw(SKCanvas canvas, RecordedGpuBlur gpu = null)
		{
			SKShader dependencyShader = null;
			SKImage dependencyImage = null;
			bool ownsDependencyImage = gpu == null;
			if (sourceSnapshot != null) {
				if (gpu != null) dependencyImage = gpu.RenderSource(sourceSnapshot, out ownsDependencyImage);
				else { using (var surface = SKSurface.Create(new SKImageInfo(sourceSnapshot.Width, sourceSnapshot.Height, SKColorType.Bgra8888, SKAlphaType.Premul))) { sourceSnapshot.DrawTo(surface.Canvas); dependencyImage = surface.Snapshot(); } }
				dependencyShader = dependencyImage.ToShader(sourceTile, sourceTile, new SKSamplingOptions(sourceFilter), sourceTransform); Paint.Shader = dependencyShader;
			}
			int save = canvas.Save();
			try {
				canvas.ResetMatrix();
				foreach (RecordedGuiClip clip in Clips) canvas.ClipPath(clip.Path, SKClipOperation.Intersect, clip.Antialias);
				canvas.SetMatrix(Matrix);
				if (PaintAll) canvas.DrawPaint(Paint); else canvas.DrawPath(Path, Paint);
			} finally { canvas.RestoreToCount(save); if (dependencyShader != null) { Paint.Shader = null; dependencyShader.Dispose(); } if (ownsDependencyImage) dependencyImage?.Dispose(); }
		}

		internal static SKMatrix ToSkia(Matrix matrix) => new SKMatrix((float)matrix.Xx, (float)matrix.Xy, (float)matrix.X0, (float)matrix.Yx, (float)matrix.Yy, (float)matrix.Y0, 0, 0, 1);
		static SKColorF SolidColor(double r, double g, double b, double a)
		{
			byte alpha = PremultipliedByte(a);
			if (alpha == 0) return new SKColorF(0, 0, 0, 0);
			return new SKColorF(PremultipliedByte(r * a) / (float)alpha,
				PremultipliedByte(g * a) / (float)alpha,
				PremultipliedByte(b * a) / (float)alpha, alpha / 255f);
		}
		static byte PremultipliedByte(double value) => (byte)((int)(Math.Max(0, Math.Min(1, value)) * 65535 + .5) >> 8);

		internal static SKColor Color(double r, double g, double b, double a) => new SKColor(Byte(r), Byte(g), Byte(b), Byte(a));
		static byte Byte(double value) => (byte)Math.Round(Math.Max(0, Math.Min(1, value)) * 255, MidpointRounding.AwayFromZero);
		static SKShaderTileMode Tile(Extend value) => value == Extend.Repeat ? SKShaderTileMode.Repeat : value == Extend.Reflect ? SKShaderTileMode.Mirror : value == Extend.Pad ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal;
		static SKBlendMode Blend(Operator value) => value switch {
			Operator.Clear => SKBlendMode.Clear, Operator.Source => SKBlendMode.Src, Operator.Over => SKBlendMode.SrcOver,
			Operator.In => SKBlendMode.SrcIn, Operator.Out => SKBlendMode.SrcOut, Operator.Atop => SKBlendMode.SrcATop,
			Operator.Dest => SKBlendMode.Dst, Operator.DestOver => SKBlendMode.DstOver, Operator.DestIn => SKBlendMode.DstIn,
			Operator.DestOut => SKBlendMode.DstOut, Operator.DestAtop => SKBlendMode.DstATop, Operator.Xor => SKBlendMode.Xor,
			Operator.Add => SKBlendMode.Plus, Operator.Multiply => SKBlendMode.Multiply, Operator.Screen => SKBlendMode.Screen,
			Operator.Overlay => SKBlendMode.Overlay, Operator.Darken => SKBlendMode.Darken, Operator.Lighten => SKBlendMode.Lighten,
			Operator.ColorDodge => SKBlendMode.ColorDodge, Operator.ColorBurn => SKBlendMode.ColorBurn,
			Operator.HardLight => SKBlendMode.HardLight, Operator.SoftLight => SKBlendMode.SoftLight,
			Operator.Difference => SKBlendMode.Difference, Operator.Exclusion => SKBlendMode.Exclusion,
			Operator.HSL_Hue => SKBlendMode.Hue, Operator.HSL_Saturation => SKBlendMode.Saturation,
			Operator.HSL_Color => SKBlendMode.Color, Operator.HSL_Luminosity => SKBlendMode.Luminosity,
			_ => throw new NotSupportedException("Cairo operator has no Skia equivalent: " + value)
		};

		[StructLayout(LayoutKind.Sequential)] struct NativePath { internal Status Status; internal IntPtr Data; internal int Count; }
		internal static SKPath CopyPath(Context context)
		{
			using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.PathCopy);
			using (Path native = context.CopyPath()) return CopyPath(native.Handle);
		}
		internal static SKPath CopyPath(IntPtr handle)
		{
			NativePath data = Marshal.PtrToStructure<NativePath>(handle);
			if (data.Status != Status.Success) throw new InvalidOperationException("Cairo path capture: " + data.Status);
			RecordedPreparationCache.Key key = null;
			if (RecordedPreparationCache.Enabled && data.Count >= 0 && data.Count <= 4096) {
				// Normalize only initialized union fields. Native header padding is not a key.
				var words = new long[data.Count * 2];
				for (int index = 0; index < data.Count;) {
					IntPtr p = IntPtr.Add(data.Data, index * 16); int kind = Marshal.ReadInt32(p), length = Marshal.ReadInt32(p, 4);
					int expected = kind == 0 || kind == 1 ? 2 : kind == 2 ? 4 : kind == 3 ? 1 : 0;
					if (expected == 0 || length != expected || index + length > data.Count) throw new InvalidOperationException("Malformed Cairo path.");
					words[index * 2] = kind; words[index * 2 + 1] = length;
					for (int i = 1; i < length; i++) { words[(index+i)*2] = Marshal.ReadInt64(p, i*16); words[(index+i)*2+1] = Marshal.ReadInt64(p, i*16+8); }
					index += length;
				}
				key = new RecordedPreparationCache.Key(3, words);
				using (var cached = RecordedPreparationCache.Acquire<RecordedPreparationCache.Geometry>(key)) if (cached != null) return new SKPath(cached.Path);
			}
			using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.PathConversion);
			var result = new SKPath();
			try {
				for (int index = 0; index < data.Count;) {
					IntPtr p = IntPtr.Add(data.Data, checked(index * 16)); int kind = Marshal.ReadInt32(p), length = Marshal.ReadInt32(p, 4);
					if (length <= 0 || index + length > data.Count) throw new InvalidOperationException("Malformed Cairo path.");
					if (kind == 0) result.MoveTo(Point(p, 1));
					else if (kind == 1) result.LineTo(Point(p, 1));
					else if (kind == 2) result.CubicTo(Point(p, 1), Point(p, 2), Point(p, 3));
					else if (kind == 3) result.Close();
					else throw new InvalidOperationException("Unknown Cairo path command.");
					index += length;
				}
				if (key != null) using (var prepared = new RecordedPreparationCache.Geometry(new SKPath(result))) RecordedPreparationCache.Store(key, prepared);
				return result;
			} catch { result.Dispose(); throw; }
		}
		static SKPoint Point(IntPtr data, int index) => new SKPoint((float)BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data, index * 16)), (float)BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data, index * 16 + 8)));
		public void Dispose() { Path?.Dispose(); dash?.Dispose(); Paint?.Dispose(); shader?.Dispose(); image?.Dispose(); nativeSource?.Dispose(); nativeImage?.Dispose(); sourceSnapshot?.Dispose(); }
	}

	internal sealed class RecordedGuiClip : IDisposable
	{
		internal readonly SKPath Path;
		internal readonly bool Antialias;
		internal RecordedGuiClip(Context context) { Path = RecordedGuiDrawing.CopyPath(context); Path.FillType = context.FillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding; Path.Transform(RecordedGuiDrawing.ToSkia(context.Matrix)); Antialias = context.Antialias != Cairo.Antialias.None; }
		public void Dispose() => Path.Dispose();
	}
}
