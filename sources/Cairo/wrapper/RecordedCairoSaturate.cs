using System;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Skia runtime blender for Cairo's Porter-Duff SATURATE operator.</summary>
	internal static class RecordedCairoSaturate
	{
		const string Source = @"
half4 main(half4 src, half4 dst)
{
		half remaining = max(0.0, 1.0 - dst.a);
		half factor = src.a > 0.0 ? min(1.0, remaining / src.a) : 1.0;
		return dst + src * factor;
}";

		static readonly Lazy<SKBlender> blender = new Lazy<SKBlender>(Create, true);

		internal static SKBlender Blender => blender.Value;

		static SKBlender Create()
		{
			using SKRuntimeEffect effect = SKRuntimeEffect.CreateBlender(Source, out string errors);
			if (effect == null) throw new InvalidOperationException("Skia could not create the Cairo SATURATE blender: " + errors);
			return effect.ToBlender();
		}
	}
}
