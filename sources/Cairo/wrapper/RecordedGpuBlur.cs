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
		SKSurface horizontal, vertical;
		int width, height;
		internal long PassCount { get; private set; }

		internal RecordedGpuBlur (GRContext context) { this.context = context ?? throw new ArgumentNullException (nameof (context)); }

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
			horizontal?.Dispose (); horizontal = null;
			vertical?.Dispose (); vertical = null;
			foreach (SKRuntimeEffect effect in effects.Values) effect.Dispose ();
			effects.Clear (); effectOrder.Clear ();
		}
	}
}
