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
		internal SKFont TextFont;
		internal SKTextBlob TextBlob;
		internal string Text;
		internal float TextX, TextY;
		internal SKRect? TextInkClip;
		internal SKMatrix Matrix;
		internal RecordedGuiClip[] Clips;
		internal bool PaintAll;
		bool saturate;
		SKImage image;
		SKImage maskImage;
		RecordedGuiDrawing maskPatternDrawing;
		Pattern maskReplayPattern;
		float maskX, maskY;
		SurfaceRecorderSnapshot sourceSnapshot;
		SKShaderTileMode sourceTile;
		SKFilterMode sourceFilter;
		Matrix sourcePatternMatrix;
		Extend sourceExtend;
		SKMatrix sourceTransform;
		SKShader shader;
		SKPathEffect dash;
		float[] dashIntervals;
		float dashOffset;
		Pattern nativeSource;
		(double Red, double Green, double Blue, double Alpha) deferredSolidSource;
		bool hasDeferredSolidSource;
		ImageSurface nativeImage;
		Matrix nativeSourceMatrix;
		static SKImage gammaInverseTable;

		internal static RecordedGuiDrawing Capture(ManagedGraphicsState context, int targetWidth, int targetHeight, bool stroke, bool paintAll,
		RecordedGuiClip[] clips, SKPath textPath = null, double alpha = 1, Matrix sourceMatrix = null, SurfaceRecorderSnapshot sourceSnapshot = null,
			string text = null, SKFont textFont = null, SKTextBlob textBlob = null, float textX = 0, float textY = 0,
			SKRect? textInkClip = null, ImageSurface maskSurface = null, Pattern maskPattern = null,
			RecordedSurfaceSource maskBinding = null, double maskX = 0, double maskY = 0)
		{
			using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.DrawingCapture);
			RecordedGuiDrawing result;
			using (SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.CaptureState))
				result = new RecordedGuiDrawing { Clips = clips, Matrix = ToSkia(context.Matrix), PaintAll = paintAll, sourceSnapshot = sourceSnapshot,
					Text = text, TextFont = textFont, TextBlob = textBlob, TextX = textX, TextY = textY, TextInkClip = textInkClip,
					maskX = (float)maskX, maskY = (float)maskY };
			try {
				if (maskSurface != null) result.maskImage = CaptureMask(maskSurface);
				if (maskPattern != null) {
					result.maskReplayPattern = maskPattern.CloneForRecorder();
					ManagedGraphicsState maskState = context.CloneForGroup();
					maskState.SetSource(maskPattern, maskBinding);
					result.maskPatternDrawing = Capture(maskState, targetWidth, targetHeight, false, true, clips,
						sourceMatrix: maskState.SourceMatrix, sourceSnapshot: maskBinding?.Capture());
				}
				result.Path = textPath ?? (paintAll || textFont != null || textBlob != null ? null : CopyPath(context));
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
					result.saturate = context.Operator == Operator.Saturate;
					if (result.saturate) result.Paint.Blender = RecordedCairoSaturate.Blender;
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
				using (SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.SourceCapture)) {
				Pattern source = context.SourcePattern;
				result.SetSource(source, context.SourceColor, alpha, sourceMatrix, context, targetWidth, targetHeight);
				if (sourceSnapshot == null) result.FreezeSource(source, context.SourceColor);
				else {
				result.sourcePatternMatrix = (Matrix)source.Matrix.Clone();
				result.sourceExtend = source.Extend;
				}
				}
				result.nativeSourceMatrix = sourceMatrix is null ? context.Matrix : (Matrix)sourceMatrix.Clone();
				return result;
			} catch { result.Dispose(); throw; }
		}

		internal void Replay(Context context, Action<Context> operation)
		{
			context.Save();
			Pattern replaySource = null;
			bool ownsReplaySource = false;
			try {
				Matrix drawMatrix = context.Matrix;
				context.Matrix = nativeSourceMatrix;
				if (sourceSnapshot != null && nativeImage == null) {
					nativeImage = new ImageSurface(sourceSnapshot.PixelFormat, sourceSnapshot.Width, sourceSnapshot.Height);
					sourceSnapshot.ReplayNative(nativeImage.NativeHandleForMetadata);
					var pattern = new SurfacePattern(nativeImage) { Matrix = sourcePatternMatrix, Extend = sourceExtend, Filter = sourceFilter == SKFilterMode.Nearest ? Filter.Nearest : Filter.Bilinear };
					nativeSource?.Dispose(); nativeSource = pattern;
				}
				replaySource = nativeSource;
				if (replaySource == null && hasDeferredSolidSource) {
					replaySource = new SolidPattern (deferredSolidSource.Red, deferredSolidSource.Green, deferredSolidSource.Blue, deferredSolidSource.Alpha);
					ownsReplaySource = true;
				}
				context.SetSource(replaySource);
				context.Matrix = drawMatrix;
				if (maskReplayPattern == null) operation(context);
				else context.Mask(maskReplayPattern);
			} finally { if (ownsReplaySource) replaySource.Dispose (); context.Restore(); }
		}

		void FreezeSource(Pattern source, (double Red, double Green, double Blue, double Alpha) color)
		{
		using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.SourceFreeze);
		if (source == null) { deferredSolidSource = color; hasDeferredSolidSource = true; return; }
		if (source.IsManagedSolidPattern) { nativeSource = source.CloneForRecorder(); return; }
		if (source.ManagedGradient != null) { nativeSource = source.CloneForRecorder(); return; }
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

		void SetDash(ManagedGraphicsState context)
		{
		double[] values = context.GetDashes();
		int count = values.Length;
		if (count == 0) return;
		var intervals = new float[count % 2 == 0 ? count : count * 2];
		for (int i = 0; i < intervals.Length; i++) intervals[i] = (float)values[i % count];
		dashIntervals = intervals; dashOffset = (float)context.DashOffset;
		dash = SKPathEffect.CreateDash(intervals, dashOffset); Paint.PathEffect = dash;
		}

		void SetSource(Pattern pattern, (double Red, double Green, double Blue, double Alpha) color, double alpha, Matrix sourceMatrix, ManagedGraphicsState context, int targetWidth, int targetHeight)
		{
		using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.SourceShader);
		IntPtr handle = pattern == null ? IntPtr.Zero : pattern.NativeHandleForRecorder;
		ManagedGradientSource managedGradient = pattern?.ManagedGradient;
		PatternType type = pattern == null ? PatternType.Solid : pattern.IsManagedSolidPattern ? PatternType.Solid : managedGradient != null ? managedGradient.Type : pattern is SurfacePattern surfacePattern && surfacePattern.IsManagedSource ? PatternType.Surface : NativeMethods.cairo_pattern_get_type(handle);
		if (type == PatternType.Solid) {
		double r, g, b, a;
		if (pattern == null) { r = color.Red; g = color.Green; b = color.Blue; a = color.Alpha; }
		else if (pattern.IsManagedSolidPattern) { r = pattern.ManagedSolidColor.R; g = pattern.ManagedSolidColor.G; b = pattern.ManagedSolidColor.B; a = pattern.ManagedSolidColor.A; }
		else NativeMethods.cairo_pattern_get_rgba(handle, out r, out g, out b, out a);
				// Cairo quantizes premultiplied solid channels to 16 bits, then
				// Pixman takes the high byte. Quantizing straight RGBA first
				// changes low-alpha inputs that partial blur can amplify.
				Paint.ColorF = SolidColor(r, g, b, a * Math.Max(0, Math.Min(1, alpha)));
				return;
			}
			SKMatrix matrix = ToSkia(pattern.Matrix);
			if (!matrix.TryInvert(out SKMatrix inverse)) throw new InvalidOperationException("Singular source pattern matrix.");
			if (sourceMatrix is not null) {
				if (!Matrix.TryInvert(out SKMatrix drawInverse)) throw new NotSupportedException("Singular drawing transform.");
				inverse = SKMatrix.Concat(SKMatrix.Concat(drawInverse, ToSkia(sourceMatrix)), inverse);
			}
			SKShaderTileMode tile = Tile(pattern.Extend);
			if (type == PatternType.Linear || type == PatternType.Radial) {
				var bindingInverse = (Matrix)(sourceMatrix ?? context.Matrix).Clone();
				bindingInverse.Invert();
				var patternFromDevice = Cairo.Matrix.Multiply(bindingInverse, pattern.Matrix);
				shader = RecordedCairoGradient.Create(handle, type, pattern.Extend, SKMatrix.Identity, patternFromDevice,
				targetWidth, targetHeight, out image, managedGradient);
				// Gradient parameters are evaluated in device coordinates, preserving double source binding.
				if (!Matrix.TryInvert(out inverse)) throw new NotSupportedException("Singular drawing transform.");
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

			if (sourceSnapshot != null) { sourceTransform = inverse; Paint.Color = Color(1, 1, 1, alpha); return; }
			SKShader transformed = shader.WithLocalMatrix(inverse);
			if (!ReferenceEquals(shader, transformed)) shader.Dispose();
			shader = transformed;
			Paint.Shader = shader; Paint.Color = Color(1, 1, 1, alpha);
		}

		internal void Draw(SKCanvas canvas, RecordedGpuBlur gpu = null, PointD deviceOffset = default, float outputScale = 1f)
		{
			DrawCore(canvas, gpu, deviceOffset, outputScale);
		}

		internal bool CanUseLcdCompositor => (TextBlob != null || TextFont != null) && Paint.Shader == null && !saturate &&
			sourceSnapshot == null && (Paint.BlendMode == SKBlendMode.SrcOver || Paint.BlendMode == SKBlendMode.Src);

		internal bool Draw(SKSurface target, RecordedGpuBlur gpu, SKRuntimeEffect lcdEffect, int width, int height, PointD deviceOffset = default)
		{
			if (target == null) throw new ArgumentNullException(nameof(target));
			if (!CanUseLcdCompositor) { DrawCore(target.Canvas, gpu, deviceOffset); return false; }
			if (gpu != null) gpu.DrawLcdText(this, target, width, height, deviceOffset);
			else DrawLcdTextCpu(target, lcdEffect, width, height, deviceOffset);
			return true;
		}

		internal void DrawDirect(SKCanvas canvas, RecordedGpuBlur gpu = null, PointD deviceOffset = default) => DrawCore(canvas, gpu, deviceOffset);

		void DrawCore(SKCanvas canvas, RecordedGpuBlur gpu, PointD deviceOffset = default, float outputScale = 1f)
		{
			SKShader dependencyShader = null;
			SKImage dependencyImage = null;
			SKShader maskDependencyShader = null;
			SKImage maskDependencyImage = null;
			bool ownsDependencyImage = gpu == null;
			bool ownsMaskDependencyImage = gpu == null;
			if (sourceSnapshot != null) {
				if (gpu != null) dependencyImage = gpu.RenderSource(sourceSnapshot, out ownsDependencyImage);
				else {
					using var properties = new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal);
					using var surface = SKSurface.Create(new SKImageInfo(sourceSnapshot.Width, sourceSnapshot.Height, SKColorType.Bgra8888, SKAlphaType.Premul), properties)
						?? throw new InvalidOperationException("Skia could not create a software GUI surface.");
					sourceSnapshot.DrawTo(surface); dependencyImage = surface.Snapshot();
				}
				dependencyShader = dependencyImage.ToShader(sourceTile, sourceTile, new SKSamplingOptions(sourceFilter), sourceTransform); Paint.Shader = dependencyShader;
			}
			if (maskPatternDrawing?.sourceSnapshot != null) {
				if (gpu != null) maskDependencyImage = gpu.RenderSource(maskPatternDrawing.sourceSnapshot, out ownsMaskDependencyImage);
				else {
					using var properties = new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal);
					using var surface = SKSurface.Create(new SKImageInfo(maskPatternDrawing.sourceSnapshot.Width, maskPatternDrawing.sourceSnapshot.Height, SKColorType.Bgra8888, SKAlphaType.Premul), properties)
						?? throw new InvalidOperationException("Skia could not create a software Cairo mask surface.");
					maskPatternDrawing.sourceSnapshot.DrawTo(surface); maskDependencyImage = surface.Snapshot();
				}
				maskDependencyShader = maskDependencyImage.ToShader(maskPatternDrawing.sourceTile, maskPatternDrawing.sourceTile,
					new SKSamplingOptions(maskPatternDrawing.sourceFilter), maskPatternDrawing.sourceTransform);
			}
			int save = canvas.Save();
			try {
				canvas.ResetMatrix();
				canvas.Scale (outputScale);
				canvas.Translate ((float)deviceOffset.X, (float)deviceOffset.Y);
				foreach (RecordedGuiClip clip in Clips) canvas.ClipPath(clip.Path, SKClipOperation.Intersect, clip.Antialias);
				if (TextInkClip.HasValue) canvas.ClipRect(TextInkClip.Value, SKClipOperation.Intersect, false);
				SKMatrix drawMatrix = new SKMatrix (
					Matrix.ScaleX * outputScale, Matrix.SkewX * outputScale, (Matrix.TransX + (float)deviceOffset.X) * outputScale,
					Matrix.SkewY * outputScale, Matrix.ScaleY * outputScale, (Matrix.TransY + (float)deviceOffset.Y) * outputScale,
					0, 0, 1);
				if (!Paint.IsAntialias) {
					// Cairo assigns exact pixel-center ties to the upper/left sample.
					// Bias aliased Skia edges by a tiny device-space amount to preserve
					// that ownership without moving visible non-tie edges by a pixel.
					drawMatrix.TransX -= 1f / 1024f;
					drawMatrix.TransY -= 1f / 1024f;
				}
				canvas.SetMatrix(drawMatrix);
				// Cairo's IN, OUT, DEST_IN and DEST_ATOP are not bounded by the mask: they also rewrite the clipped area outside
				// the shape, where the source is transparent. Composite the shape as a layer so the operator sees that whole area.
				SKBlendMode operation = Paint.BlendMode;
				bool unbounded = !PaintAll && (operation == SKBlendMode.SrcIn || operation == SKBlendMode.SrcOut || operation == SKBlendMode.DstIn || operation == SKBlendMode.DstATop);
				bool layered = unbounded || maskImage != null || maskPatternDrawing != null;
				if (layered) {
				using var layerPaint = new SKPaint { BlendMode = operation };
				if (saturate) layerPaint.Blender = RecordedCairoSaturate.Blender;
				canvas.SaveLayer(layerPaint);
				Paint.BlendMode = SKBlendMode.SrcOver;
				}
				try {
				if (TextBlob != null) canvas.DrawText(TextBlob, TextX, TextY, Paint);
				else if (TextFont != null) canvas.DrawText(Text, TextX, TextY, TextFont, Paint);
				else if (PaintAll) canvas.DrawPaint(Paint);
				else canvas.DrawPath(Path, Paint);
				if (maskImage != null || maskPatternDrawing != null) {
					using var maskShader = maskImage?.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal,
						new SKSamplingOptions(SKFilterMode.Linear));
					using var maskPaint = new SKPaint {
						BlendMode = SKBlendMode.DstIn, IsAntialias = false,
						Color = maskPatternDrawing?.Paint.Color ?? SKColors.White,
						Shader = maskShader ?? maskDependencyShader ?? maskPatternDrawing?.Paint.Shader
					};
					canvas.Save();
					if (maskImage != null) canvas.Translate(maskX, maskY);
					canvas.DrawPaint(maskPaint);
					canvas.Restore();
				}
				} finally { if (layered) { Paint.BlendMode = operation; canvas.Restore(); } }
			} finally {
				canvas.RestoreToCount(save);
				if (dependencyShader != null) { Paint.Shader = null; dependencyShader.Dispose(); }
				if (maskDependencyShader != null) maskDependencyShader.Dispose();
				if (ownsDependencyImage) dependencyImage?.Dispose();
				if (ownsMaskDependencyImage) maskDependencyImage?.Dispose();
			}
		}

		internal void DrawLcdTextCpu(SKSurface target, SKRuntimeEffect effect, int width, int height, PointD deviceOffset = default)
		{
			if (effect == null) throw new ArgumentNullException(nameof(effect));
			var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
			using var properties = new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal);
			using var black = SKSurface.Create(info, properties) ?? throw new InvalidOperationException("Skia could not create a CPU LCD black surface.");
			using var white = SKSurface.Create(info, properties) ?? throw new InvalidOperationException("Skia could not create a CPU LCD white surface.");
			black.Canvas.Clear(SKColors.Black); white.Canvas.Clear(SKColors.White);
			DrawCore(black.Canvas, null, deviceOffset); DrawCore(white.Canvas, null, deviceOffset);
			using SKImage blackImage = black.Snapshot();
			using SKImage whiteImage = white.Snapshot();
			using SKImage destination = target.Snapshot();
			DrawLcdComposite(effect, blackImage, whiteImage, destination, target.Canvas, Paint.BlendMode == SKBlendMode.Src,
				Paint.ColorF.Alpha, Paint.Color, width, height, deviceOffset);
		}

		internal static void DrawLcdComposite(SKRuntimeEffect effect, SKImage black, SKImage white, SKImage destination,
			SKCanvas canvas, bool sourceOperator, float sourceAlpha, SKColor sourceColor, int width, int height, PointD deviceOffset = default)
		{
			bool invertGamma = Environment.GetEnvironmentVariable("OPTIMUM_GUI_LCD_GAMMA_INVERSE") switch {
				"0" => false,
				"1" => true,
				_ => OperatingSystem.IsLinux()
			};
			var uniforms = new SKRuntimeEffectUniforms(effect) {
				["sourceOperator"] = sourceOperator ? 1 : 0,
				["sourceAlpha"] = sourceAlpha,
				["invertGamma"] = invertGamma ? 1 : 0,
				["sourceColor"] = new float[] { sourceColor.Red / 255f, sourceColor.Green / 255f, sourceColor.Blue / 255f },
				["colorIndices"] = new int[] { sourceColor.Red >> 5, sourceColor.Green >> 5, sourceColor.Blue >> 5 }
			};
			using var blackShader = black.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
			using var whiteShader = white.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
			using var destinationShader = destination.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
			SKImage inverseGammaTable = gammaInverseTable ??= CreateGammaInverseTable();
			using var inverseGammaShader = inverseGammaTable.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
			var children = new SKRuntimeEffectChildren(effect) {
				["blackImage"] = blackShader,
				["whiteImage"] = whiteShader,
				["destinationImage"] = destinationShader,
				["inverseGammaTable"] = inverseGammaShader
			};
			using var shader = effect.ToShader(uniforms, children);
			using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src, IsAntialias = false };
			if (shader == null) throw new InvalidOperationException("Skia rejected the LCD text compositor shader.");
			int save = canvas.Save();
			try { canvas.ResetMatrix(); canvas.Translate ((float)deviceOffset.X, (float)deviceOffset.Y); canvas.DrawRect(0, 0, width, height, paint); }
			finally { canvas.RestoreToCount(save); }
		}

		internal static string LcdTextCompositorShaderSource => @"
uniform shader blackImage;
uniform shader whiteImage;
uniform shader destinationImage;
uniform shader inverseGammaTable;
uniform int sourceOperator;
uniform float sourceAlpha;
uniform int invertGamma;
uniform float3 sourceColor;
uniform int3 colorIndices;
half4 main(float2 p) {
    float4 black = blackImage.eval(p);
    float4 white = whiteImage.eval(p);
    float4 destination = destinationImage.eval(p);
    float3 retention = clamp(white.rgb - black.rgb, 0.0, 1.0);
    if (invertGamma != 0) {
        float3 coverage = clamp(1.0 - retention, 0.0, 1.0);
        if (sourceOperator == 0) coverage = clamp(coverage / max(sourceAlpha, 1.0 / 255.0), 0.0, 1.0);
        float3 rawCoverage = float3(
            inverseGammaTable.eval(float2(coverage.r * 255.0 + 0.5, float(colorIndices.r) + 0.5)).r,
            inverseGammaTable.eval(float2(coverage.g * 255.0 + 0.5, float(colorIndices.g) + 0.5)).r,
            inverseGammaTable.eval(float2(coverage.b * 255.0 + 0.5, float(colorIndices.b) + 0.5)).r);
        float3 sourcePremul = sourceColor * sourceAlpha;
        float3 destinationRetention = sourceOperator != 0 ? 1.0 - rawCoverage : 1.0 - sourceAlpha * rawCoverage;
        float3 rgb = sourcePremul * rawCoverage + destination.rgb * destinationRetention;
        float alpha = sourceAlpha * rawCoverage.g + destination.a * destinationRetention.g;
        return half4(rgb, alpha);
    }
    float3 rgb = black.rgb + retention * destination.rgb;
    float alpha = sourceOperator != 0
        ? sourceAlpha * (1.0 - retention.g) + destination.a * retention.g
        : (1.0 - retention.g) + destination.a * retention.g;
    return half4(rgb, alpha);
}";

		static SKImage CreateGammaInverseTable()
		{
			// Skia's LCD mask preblend uses canonical 3-bit source channels, an sRGB
			// transfer curve and 128/255 contrast for the null-color-space software
			// surface used by this recorder. The table reverses that quantized mapping
			// by choosing the midpoint of the nearest raw-coverage interval.
			using var bitmap = new SKBitmap(256, 8, SKColorType.Rgba8888, SKAlphaType.Opaque);
			for (int colorIndex = 0; colorIndex < 8; colorIndex++) {
				int canonicalByte = (colorIndex << 5) | (colorIndex << 2) | (colorIndex >> 1);
				float source = canonicalByte / 255f;
				float destination = 1f - source;
				float linearSource = SrgbToLinear(source);
				float linearDestination = SrgbToLinear(destination);
				float adjustedContrast = (128f / 255f) * linearDestination;
				var forward = new float[256];
				for (int raw = 0; raw < 256; raw++) {
					float alpha = raw / 255f;
					float contrastedAlpha = alpha + (1f - alpha) * adjustedContrast * alpha;
					float linearOutput = linearSource * contrastedAlpha + linearDestination * (1f - contrastedAlpha);
					float output = LinearToSrgb(linearOutput);
					float result = (output - destination) / (source - destination);
					int gammaByte = Math.Clamp((int)MathF.Floor(result * 255f + 0.5f), 0, 255);
					forward[raw] = (gammaByte >> 2) * (255f / 63f);
				}
				for (int observed = 0; observed < 256; observed++) {
					float best = float.MaxValue;
					int first = 0, last = 0;
					for (int raw = 0; raw < 256; raw++) {
						float error = MathF.Abs(forward[raw] - observed);
						if (error < best) { best = error; first = last = raw; }
						else if (error == best) last = raw;
					}
					int inverse = observed == 0 ? 0 : observed == 255 ? 255 : (first + last) / 2;
					bitmap.SetPixel(observed, colorIndex, new SKColor((byte)inverse, 0, 0, 255));
				}
			}
			return SKImage.FromBitmap(bitmap);
		}

		static float SrgbToLinear(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
		static float LinearToSrgb(float value) => value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;

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
			Operator.Saturate => SKBlendMode.SrcOver,
			_ => throw new NotSupportedException("Cairo operator has no Skia equivalent: " + value)
		};

		[StructLayout(LayoutKind.Sequential)] struct NativePath { internal Status Status; internal IntPtr Data; internal int Count; }
		internal static SKPath CopyPath(ManagedGraphicsState state)
		{
		using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.PathCopy);
		return CopyPath(state.CopyPath());
		}
		internal static SKPath CopyPath(Context context)
		{
		using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.PathCopy);
		using (Path native = context.CopyPath()) return CopyPath(native.Handle);
		}
		internal static SKPath CopyPath(ManagedGraphicsState.PathElement[] elements)
		{
		RecordedPreparationCache.Key key = null;
		int dataCount = 0;
		foreach (var element in elements) dataCount += element.Op == ManagedGraphicsState.PathOp.ClosePath ? 1 : element.Op == ManagedGraphicsState.PathOp.CurveTo ? 4 : 2;
		if (RecordedPreparationCache.Enabled && dataCount <= 4096) {
		// Same key layout as the native path data so both conversions share cache entries.
		var words = new long[dataCount * 2];
		int index = 0;
		foreach (var element in elements) {
		int length = element.Op == ManagedGraphicsState.PathOp.ClosePath ? 1 : element.Op == ManagedGraphicsState.PathOp.CurveTo ? 4 : 2;
		words[index * 2] = (int)element.Op; words[index * 2 + 1] = length;
		if (length >= 2) { words[(index + 1) * 2] = BitConverter.DoubleToInt64Bits(element.X1); words[(index + 1) * 2 + 1] = BitConverter.DoubleToInt64Bits(element.Y1); }
		if (length >= 4) {
		words[(index + 2) * 2] = BitConverter.DoubleToInt64Bits(element.X2); words[(index + 2) * 2 + 1] = BitConverter.DoubleToInt64Bits(element.Y2);
		words[(index + 3) * 2] = BitConverter.DoubleToInt64Bits(element.X3); words[(index + 3) * 2 + 1] = BitConverter.DoubleToInt64Bits(element.Y3);
		}
		index += length;
		}
		key = new RecordedPreparationCache.Key(3, words);
		using (var cached = RecordedPreparationCache.Acquire<RecordedPreparationCache.Geometry>(key)) if (cached != null) return new SKPath(cached.Path);
		}
		using var conversion = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.PathConversion);
		var result = new SKPath();
		try {
		foreach (var element in elements) {
		switch (element.Op) {
		case ManagedGraphicsState.PathOp.MoveTo: result.MoveTo((float)element.X1, (float)element.Y1); break;
		case ManagedGraphicsState.PathOp.LineTo: result.LineTo((float)element.X1, (float)element.Y1); break;
		case ManagedGraphicsState.PathOp.CurveTo: result.CubicTo((float)element.X1, (float)element.Y1, (float)element.X2, (float)element.Y2, (float)element.X3, (float)element.Y3); break;
		default: result.Close(); break;
		}
		}
		if (key != null) using (var prepared = new RecordedPreparationCache.Geometry(new SKPath(result))) RecordedPreparationCache.Store(key, prepared);
		return result;
		} catch { result.Dispose(); throw; }
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
		public void Dispose() { Path?.Dispose(); TextFont?.Dispose(); TextBlob?.Dispose(); dash?.Dispose(); Paint?.Dispose(); shader?.Dispose(); image?.Dispose(); maskImage?.Dispose(); maskPatternDrawing?.Dispose(); maskReplayPattern?.Dispose(); nativeSource?.Dispose(); nativeImage?.Dispose(); sourceSnapshot?.Dispose(); }

		static SKImage CaptureMask(ImageSurface surface)
		{
			if (surface == null) throw new ArgumentNullException(nameof(surface));
			int width = surface.Width, height = surface.Height;
			Format format = surface.Format;
			int stride = surface.Stride;
			byte[] data = surface.Data;
			if (data == null) throw new InvalidOperationException("Cairo mask surface has no readable pixel data.");
			var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
			using var bitmap = new SKBitmap(info);
			IntPtr pixels = bitmap.GetPixels();
			if (pixels == IntPtr.Zero) throw new InvalidOperationException("Skia could not allocate a Cairo mask snapshot.");
			byte[] row = new byte[checked(width * 4)];
			for (int y = 0; y < height; y++) {
				Array.Clear(row, 0, row.Length);
				int source = checked(y * stride);
				switch (format) {
				case Format.Argb32:
				case Format.Rgb24:
					Buffer.BlockCopy(data, source, row, 0, Math.Min(row.Length, stride));
					if (format == Format.Rgb24) for (int x = 0; x < width; x++) row[x * 4 + 3] = 255;
					break;
				case Format.A8:
					for (int x = 0; x < width; x++) row[x * 4 + 3] = data[source + x];
					break;
				case Format.A1:
					for (int x = 0; x < width; x++) {
						int bit = BitConverter.IsLittleEndian ? x & 7 : 7 - (x & 7);
						row[x * 4 + 3] = (data[source + (x >> 3)] & (1 << bit)) != 0 ? (byte)255 : (byte)0;
					}
					break;
				case Format.Rgb16565:
					for (int x = 0; x < width; x++) row[x * 4 + 3] = 255;
					break;
				default: throw new NotSupportedException("Managed Cairo masks require ARGB32, RGB24, A8, A1, or RGB565 image surfaces.");
				}
				Marshal.Copy(row, 0, IntPtr.Add(pixels, y * bitmap.RowBytes), row.Length);
			}
			return SKImage.FromBitmap(bitmap);
		}
	}

	internal sealed class RecordedGuiClip : IDisposable
	{
		internal readonly SKPath Path;
		internal readonly bool Antialias;
		internal RecordedGuiClip(ManagedGraphicsState context) { Path = RecordedGuiDrawing.CopyPath(context); Path.FillType = context.FillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding; Path.Transform(RecordedGuiDrawing.ToSkia(context.Matrix)); Antialias = context.Antialias != Cairo.Antialias.None;
			// Aliased clips follow the same pixel-centre tie ownership as aliased fills (see RecordedGuiDrawing.DrawCore).
			if (!Antialias) Path.Transform(SKMatrix.CreateTranslation(-1f / 1024f, -1f / 1024f)); }
		internal RecordedGuiClip Clone() => new RecordedGuiClip (new SKPath (Path), Antialias);
		RecordedGuiClip (SKPath path, bool antialias) { Path = path; Antialias = antialias; }
		public void Dispose() => Path.Dispose();
	}
}
