using System;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Preserves Cairo's byte-rounded intermediate products for separable blends.</summary>
	internal static class RecordedCairoIntegerBlend
	{
		static readonly Lazy<SKBlender> difference = new Lazy<SKBlender>(() => Create(false), true);
		static readonly Lazy<SKBlender> exclusion = new Lazy<SKBlender>(() => Create(true), true);

		internal static SKBlender For(Operator op) => op == Operator.Difference ? difference.Value :
			op == Operator.Exclusion ? exclusion.Value : null;

		static SKBlender Create(bool exclude)
		{
			string source = @"
float3 multiplyBytes(float3 a, float3 b) { return floor(a * b / 255.0 + 0.5); }
half4 main(half4 src, half4 dst) {
    float4 s = floor(float4(src) * 255.0 + 0.5);
    float4 d = floor(float4(dst) * 255.0 + 0.5);
    float3 product = " + (exclude ? "multiplyBytes(s.rgb, d.rgb)" : "min(multiplyBytes(s.rgb, d.aaa), multiplyBytes(d.rgb, s.aaa))") + @";
    return half4((s.rgb + d.rgb - 2.0 * product) / 255.0,
        (s.a + d.a - floor(s.a * d.a / 255.0 + 0.5)) / 255.0);
}";
			using var effect = SKRuntimeEffect.CreateBlender(source, out string errors);
			if (effect == null) throw new InvalidOperationException("Skia could not create the byte-rounded Cairo blender: " + errors);
			return effect.ToBlender();
		}
	}
}
