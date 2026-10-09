using System;
using System.Collections.Generic;
using System.Text;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Context-owned GPU scratch surfaces and shaders for the game's RGB-only box blur.</summary>
	internal sealed class RecordedGpuBlur : IDisposable
	{
		const int MaxCachedEffects = 16;
		readonly GRContext context;
		readonly Dictionary<int, SKRuntimeEffect> effects = new Dictionary<int, SKRuntimeEffect> ();
		readonly Queue<int> effectOrder = new Queue<int> ();
		readonly Dictionary<(long, int), SKImage> sourceImages = new Dictionary<(long, int), SKImage>();
		readonly Queue<(long, int)> sourceOrder = new Queue<(long, int)>();
		long sourceBytes;
		int dependencyDepth;
		SKSurface horizontal, vertical;
		SKRuntimeEffect demultiplyEffect;
		SKImage demultiplyTable;
		SKRuntimeEffect imageOverlayEffect;
		int width, height;
		internal long PassCount { get; private set; }

		internal RecordedGpuBlur (GRContext context) { this.context = context ?? throw new ArgumentNullException (nameof (context)); }

		internal SKImage RenderSource(SurfaceRecorderSnapshot recording, out bool callerOwnsImage)
		{
			callerOwnsImage = false;
			var key = (recording.Identity, recording.Generation);
			if (sourceImages.TryGetValue(key, out SKImage cached)) { SurfaceRecordingDiagnostics.ReuseDependency(); return cached; }
			if (dependencyDepth >= 64) throw new NotSupportedException("Surface dependency depth exceeds 64.");
			dependencyDepth++;
			try {
				using (var target = SKSurface.Create(context, true, new SKImageInfo(recording.Width, recording.Height, SKColorType.Rgba8888, SKAlphaType.Premul))) {
					if (target == null) throw new InvalidOperationException("Could not create a GPU surface dependency.");
					recording.DrawTo(target, this); SurfaceRecordingDiagnostics.RenderDependency();
					SKImage image = target.Snapshot();
					long bytes = checked((long)recording.Width * recording.Height * 4);
					if (bytes > 8 * 1024 * 1024) { callerOwnsImage = true; return image; }
					while (sourceOrder.Count > 0 && (sourceImages.Count >= 32 || sourceBytes + bytes > 8 * 1024 * 1024)) {
						var oldest = sourceOrder.Dequeue(); SKImage old = sourceImages[oldest]; sourceBytes -= (long)old.Width * old.Height * 4; sourceImages.Remove(oldest); old.Dispose();
					}
					sourceImages.Add(key, image); sourceOrder.Enqueue(key); sourceBytes += bytes;
					return image;
				}
			} finally { dependencyDepth--; }
		}

		internal void Apply (RecordedBlur blur, SKSurface target, int width, int height)
		{
			int left = blur.Full ? 0 : blur.X1, top = blur.Full ? 0 : blur.Y1;
			int right = blur.Full ? width : blur.X2, bottom = blur.Full ? height : blur.Y2;
			if (left < 0 || top < 0 || right > width || bottom > height || left >= right || top >= bottom)
				throw new NotSupportedException ("Recorded GPU blur rectangle is outside the surface.");
			EnsureSurfaces (width, height);
			using (var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = false }) {
				for (int pass = 0; pass < 3; pass++) {
					int radius = blur.Radius (pass);
					if (radius < 0 || 2 * radius >= right - left || 2 * radius >= bottom - top)
						throw new NotSupportedException ("Recorded GPU blur radius exceeds its rectangle.");
					SKRuntimeEffect effect = GetEffect (radius);
					// GPU snapshots retain their backing textures. No ReadPixels or CPU bitmap is used.
					using (SKImage original = target.Snapshot ()) {
						DrawPass (effect, original, original, horizontal, paint, left, top, right, bottom, blur.Edge, true, width, height);
						PassCount++;
						using (SKImage intermediate = horizontal.Snapshot ())
							DrawPass (effect, intermediate, original, vertical, paint, left, top, right, bottom, blur.Edge, false, width, height);
						PassCount++;
					}
					using (SKImage result = vertical.Snapshot ()) target.Canvas.DrawImage (result, 0, 0, paint);
				}
			}
		}

		// Preserve the game's existing native-endian byte operation, including its
		// little-endian channel order and unchecked overlapping shifted values.
		internal static string DemultiplyShaderSource => @"
uniform shader sourceImage;
uniform shader divisionTable;
uniform int littleEndian;
int divideByte(int value, int alpha) {
    int2 divided = int2(floor(float2(divisionTable.eval(float2(value, alpha) + float2(0.5)).rg) * 255 + 0.5));
    return divided.x + divided.y * 256;
}
int byteOr(int x, int y) {
    int result = 0;
    int place = 1;
    for (int bit = 0; bit < 8; bit++) {
        if (x - (x / 2) * 2 != 0 || y - (y / 2) * 2 != 0) result += place;
        x /= 2; y /= 2; place *= 2;
    }
    return result;
}
float4 main(float2 p) {
    int4 c = int4(floor(float4(sourceImage.eval(p)) * 255 + 0.5));
    int alpha = littleEndian != 0 ? c.b : c.a;
    if (alpha == 0) return littleEndian != 0
        ? float4(c.r > 0 || c.g > 0 ? 1 : 0, c.g > 0 ? 1 : 0, 0, c.a > 0 || c.r > 0 || c.g > 0 ? 1 : 0)
        : float4(c.r > 0 || c.g > 0 || c.b > 0 ? 1 : 0, c.g > 0 || c.b > 0 ? 1 : 0, c.b > 0 ? 1 : 0, c.r > 0 || c.g > 0 || c.b > 0 ? 1 : 0);
    int r = divideByte(littleEndian != 0 ? c.a : c.b, alpha);
    int g = divideByte(littleEndian != 0 ? c.r : c.g, alpha);
    int b = divideByte(littleEndian != 0 ? c.g : c.r, alpha);
    int rLow = r - (r / 256) * 256;
    int gLow = g - (g / 256) * 256;
    int bLow = b - (b / 256) * 256;
    return littleEndian != 0
        ? float4(byteOr(gLow, b / 256), bLow, alpha, byteOr(rLow, g / 256)) / 255.0
        : float4(byteOr(bLow, g / 256), byteOr(gLow, r / 256), rLow, byteOr(alpha, b / 256)) / 255.0;
}";

		internal static SKImage CreateDemultiplyTable()
		{
			// A fixed numerical lookup, independent of UI pixels, reproduces the CLR's
			// float division and truncation without driver-specific reciprocal rounding.
			using (var bitmap = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Opaque)) {
				for (int alpha = 0; alpha < 256; alpha++)
				for (int value = 0; value < 256; value++) {
					uint divided = alpha == 0 ? 0 : (uint)(value / (alpha / 255f));
					bitmap.SetPixel(value, alpha, new SKColor((byte)divided, (byte)(divided >> 8), 0, 255));
				}
				return SKImage.FromBitmap(bitmap);
			}
		}

		internal static void DrawDemultiply(SKRuntimeEffect effect, SKImage input, SKImage table, SKSurface destination, int width, int height)
		{
			var uniforms = new SKRuntimeEffectUniforms(effect) { ["littleEndian"] = BitConverter.IsLittleEndian ? 1 : 0 };
			using (var source = input.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest)))
			using (var division = table.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest))) {
				var children = new SKRuntimeEffectChildren(effect) { ["sourceImage"] = source, ["divisionTable"] = division };
				using (var shader = effect.ToShader(uniforms, children))
				using (var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src, IsAntialias = false }) {
					if (shader == null) throw new InvalidOperationException("Skia rejected the recorded demultiply shader.");
					destination.Canvas.DrawRect(0, 0, width, height, paint);
				}
			}
		}

		internal void Demultiply(SKSurface target, int width, int height)
		{
			if (demultiplyEffect == null) {
				demultiplyEffect = SKRuntimeEffect.CreateShader(DemultiplyShaderSource, out string errors);
				if (demultiplyEffect == null) throw new NotSupportedException("Recorded demultiply shader compilation failed: " + errors);
			}
			EnsureSurfaces(width, height);
			demultiplyTable ??= CreateDemultiplyTable();
			using (var original = target.Snapshot()) DrawDemultiply(demultiplyEffect, original, demultiplyTable, horizontal, width, height);
			using (var result = horizontal.Snapshot())
			using (var paint = new SKPaint { BlendMode = SKBlendMode.Src }) target.Canvas.DrawImage(result, 0, 0, paint);
		}

		internal static string ImageOverlayShaderSource => @"
uniform shader sourceImage;
uniform shader original;
uniform float4 rectangle;
uniform float2 scale;
uniform int hasTint;
uniform int swapSource;
uniform float4 tint;
float4 main(float2 p) {
    float4 base = float4(original.eval(p));
    if (p.x < rectangle.x || p.y < rectangle.y || p.x >= rectangle.z || p.y >= rectangle.w) return base;
    float4 sampled = float4(sourceImage.eval((p - rectangle.xy) * scale));
    float aOver = floor(sampled.a * 255 + 0.5) / 255.0;
    float3 rgbOver = sampled.a > 0 ? floor(clamp(sampled.rgb / sampled.a, 0.0, 1.0) * 255 + 0.5) : float3(0);
    if (swapSource != 0) rgbOver = rgbOver.bgr;
    if (hasTint != 0) { rgbOver = floor(aOver * tint.rgb); aOver = floor(aOver * tint.a) / 255.0; }
    float aBase = floor(base.a * 255 + 0.5) / 255.0;
    float3 rgbBase = floor(base.rgb * 255 + 0.5);
    float total = aOver + aBase * (1.0 - aOver);
    if (total == 0) return float4(0);
    float3 rgb = floor((rgbOver * aOver + rgbBase * aBase * (1.0 - aOver)) / total);
    return float4(rgb, floor(255.0 * total)) / 255.0;
}";

		internal static void DrawImageOverlay(SKRuntimeEffect effect, RecordedGuiImage image, SKImage original, SKSurface destination, int width, int height)
			=> DrawOverlay(effect, image.Image, original, destination, image.X, image.Y, image.Width, image.Height, width, height,
				new SKSamplingOptions(SKCubicResampler.Mitchell), null, false, false);

		internal static void DrawOverlay(SKRuntimeEffect effect, SKImage image, SKImage original, SKSurface destination,
			int x, int y, int imageWidth, int imageHeight, int width, int height, SKSamplingOptions sampling, int? tint, bool textureColorOrder, bool svg)
		{
			var uniforms = new SKRuntimeEffectUniforms(effect) {
				["rectangle"] = new float[] { x, y, x + imageWidth, y + imageHeight },
				["scale"] = new float[] { (float)image.Width / imageWidth, (float)image.Height / imageHeight },
				["hasTint"] = tint.HasValue ? 1 : 0,
				["swapSource"] = svg && textureColorOrder && !tint.HasValue ? 1 : 0,
				["tint"] = tint.HasValue ? new float[] { (tint.Value >> (textureColorOrder ? 16 : 0)) & 255, (tint.Value >> 8) & 255, (tint.Value >> (textureColorOrder ? 0 : 16)) & 255, (tint.Value >> 24) & 255 } : new float[4]
			};
			using (var source = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling))
			using (var backdrop = original.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest))) {
				var children = new SKRuntimeEffectChildren(effect) { ["sourceImage"] = source, ["original"] = backdrop };
				using (var shader = effect.ToShader(uniforms, children))
				using (var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src, IsAntialias = false }) {
					if (shader == null) throw new InvalidOperationException("Skia rejected the recorded GUI image shader.");
					destination.Canvas.DrawRect(0, 0, width, height, paint);
				}
			}
		}

		internal void DrawImage(RecordedGuiImage image, SKSurface target, int width, int height)
		{
			if (imageOverlayEffect == null) {
				imageOverlayEffect = SKRuntimeEffect.CreateShader(ImageOverlayShaderSource, out string errors);
				if (imageOverlayEffect == null) throw new NotSupportedException("Recorded GUI image shader compilation failed: " + errors);
			}
			EnsureSurfaces(width, height);
			using (var original = target.Snapshot()) DrawImageOverlay(imageOverlayEffect, image, original, horizontal, width, height);
			using (var result = horizontal.Snapshot())
			using (var paint = new SKPaint { BlendMode = SKBlendMode.Src }) target.Canvas.DrawImage(result, 0, 0, paint);
		}

		internal void DrawPicture(RecordedGuiPicture picture, SKSurface target, int width, int height)
		{
			if (imageOverlayEffect == null) {
				imageOverlayEffect = SKRuntimeEffect.CreateShader(ImageOverlayShaderSource, out string errors);
				if (imageOverlayEffect == null) throw new NotSupportedException("Recorded SVG shader compilation failed: " + errors);
			}
			EnsureSurfaces(width, height);
			using (var source = SKSurface.Create(context, true, new SKImageInfo(picture.Width, picture.Height, SKColorType.Rgba8888, SKAlphaType.Premul))) {
				if (source == null) throw new InvalidOperationException("Skia could not allocate a GPU SVG surface.");
				source.Canvas.Clear(SKColors.Transparent); source.Canvas.DrawPicture(picture.Picture);
				using (var pixels = source.Snapshot())
				using (var original = target.Snapshot()) DrawOverlay(imageOverlayEffect, pixels, original, horizontal,
					picture.X, picture.Y, picture.Width, picture.Height, width, height, new SKSamplingOptions(SKFilterMode.Nearest), picture.Tint, picture.TextureColorOrder, true);
			}
			using (var result = horizontal.Snapshot())
			using (var paint = new SKPaint { BlendMode = SKBlendMode.Src }) target.Canvas.DrawImage(result, 0, 0, paint);
		}

		internal static void DrawPass (SKRuntimeEffect effect, SKImage input, SKImage original, SKSurface destination,
			SKPaint paint, int left, int top, int right, int bottom, int edge, bool horizontalPass, int width, int height)
		{
			var uniforms = new SKRuntimeEffectUniforms (effect) {
				["bounds"] = new float[] { left, top, right, bottom },
				["edge"] = (float)edge,
				["axis"] = horizontalPass ? new float[] { 1, 0 } : new float[] { 0, 1 }
			};
			using (SKShader inputShader = input.ToShader (SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions (SKFilterMode.Nearest)))
			using (SKShader originalShader = original.ToShader (SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions (SKFilterMode.Nearest))) {
				var children = new SKRuntimeEffectChildren (effect) { ["sourceImage"] = inputShader, ["original"] = originalShader };
				using (SKShader shader = effect.ToShader (uniforms, children)) {
					if (shader == null) throw new InvalidOperationException ("Skia rejected the recorded blur shader.");
					paint.Shader = shader;
					destination.Canvas.DrawRect (0, 0, width, height, paint);
					paint.Shader = null;
				}
			}
		}

		void EnsureSurfaces (int nextWidth, int nextHeight)
		{
			if (horizontal != null && width == nextWidth && height == nextHeight) return;
			horizontal?.Dispose (); horizontal = null;
			vertical?.Dispose (); vertical = null;
			width = nextWidth; height = nextHeight;
			var info = new SKImageInfo (width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
			try {
				horizontal = SKSurface.Create (context, true, info);
				vertical = SKSurface.Create (context, true, info);
				if (horizontal == null || vertical == null) throw new InvalidOperationException ("Skia could not allocate GPU blur surfaces.");
			} catch { horizontal?.Dispose (); horizontal = null; vertical?.Dispose (); vertical = null; throw; }
		}

		SKRuntimeEffect GetEffect (int radius)
		{
			if (effects.TryGetValue (radius, out SKRuntimeEffect effect)) return effect;
			string errors;
			effect = SKRuntimeEffect.CreateShader (ShaderSource (radius), out errors);
			if (effect == null) throw new NotSupportedException ("Recorded blur shader compilation failed: " + errors);
			if (effects.Count == MaxCachedEffects) { int old = effectOrder.Dequeue (); effects[old].Dispose (); effects.Remove (old); }
			effects.Add (radius, effect); effectOrder.Enqueue (radius);
			return effect;
		}

		internal static string ShaderSource (int radius)
		{
			int divisor = checked (2 * radius + 1);
			// The CPU truncates sum * (double)(1/divisor), which can be one below an
			// exact integer quotient. Encode those cases rather than using GPU float rounding.
			var corrections = new StringBuilder ();
			for (int value = 1; value <= 255; value++)
				if ((int)(value * divisor * (1.0 / divisor)) < value) {
					if (corrections.Length != 0) corrections.Append (" || ");
					corrections.Append ("q == ").Append (value);
				}
			string correction = corrections.Length == 0 ? "" : "if (s == q * " + divisor + " && (" + corrections + ")) q--;";
			return @"
uniform shader sourceImage;
uniform shader original;
uniform float4 bounds;
uniform float2 axis;
uniform float edge;

float3 windowSum(float2 p, float position, float lo, float hi) {
    float3 sum = float3(0);
    for (int i = -" + radius + @"; i <= " + radius + @"; i++) {
        float coordinate = clamp(position + float(i), lo, hi - 1);
        float2 samplePoint = p + axis * (coordinate - dot(p - float2(0.5), axis));
        sum += floor(float3(sourceImage.eval(samplePoint).rgb) * 255 + 0.5);
    }
    return sum;
}

float quantize(float value) {
    int s = int(value);
    int q = s / " + divisor + @";
    " + correction + @"
    // Match the unchecked byte cast, including the partial algorithm's accumulator.
    return (float(q) - floor(float(q) / 256) * 256) / 255;
}

half4 main(float2 p) {
    half4 preserved = original.eval(p);
    float2 xy = floor(p);
    if (xy.x < bounds.x || xy.y < bounds.y || xy.x >= bounds.z || xy.y >= bounds.w) return preserved;
    float lo = dot(bounds.xy, axis), hi = dot(bounds.zw, axis);
    float position = dot(xy, axis);
    float2 crossAxis = axis.yx;
    float cross = dot(xy, crossAxis);
    float crossLo = dot(bounds.xy, crossAxis), crossHi = dot(bounds.zw, crossAxis);
    float anchor = lo + edge;
    float skipped = max(0, hi - lo - 2 * edge);
    bool skips = cross > crossLo + edge && cross < crossHi - 2 * edge
        && anchor >= lo + " + (radius + 1) + @" && anchor < hi - " + radius + @" && skipped > 0;
    if (skips && position > anchor && position <= anchor + skipped) return preserved;
    float3 sum = windowSum(p, position, lo, hi);
    // Vanilla skips the interior without advancing its RGB running sum.
    if (skips && position > anchor + skipped)
        sum += windowSum(p, anchor, lo, hi) - windowSum(p, anchor + skipped, lo, hi);
    return half4(quantize(sum.r), quantize(sum.g), quantize(sum.b), preserved.a);
}";
		}

		public void Dispose ()
		{
			demultiplyEffect?.Dispose(); demultiplyEffect = null;
			demultiplyTable?.Dispose(); demultiplyTable = null;
			imageOverlayEffect?.Dispose(); imageOverlayEffect = null;
			horizontal?.Dispose (); horizontal = null;
			vertical?.Dispose (); vertical = null;
			foreach (SKRuntimeEffect effect in effects.Values) effect.Dispose ();
			effects.Clear (); effectOrder.Clear ();
			foreach (var image in sourceImages.Values) image.Dispose(); sourceImages.Clear(); sourceOrder.Clear(); sourceBytes = 0;
		}
	}
}
