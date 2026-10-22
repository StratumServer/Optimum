using System;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Builds the Skia font that stands in for a recorded Cairo font selection.</summary>
	internal static class SkiaGuiFont
	{
		internal static SKFont Create(ManagedGraphicsState state, FontOptions options)
		{
			using var fontSelection = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.FontSelection);
			string family = state.FontFamily;
			if (string.IsNullOrWhiteSpace(family))
				throw new InvalidOperationException("The selected font face has no managed family metadata; Cairo materialization is disabled for recorded text.");
			var weight = state.FontWeight == FontWeight.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
			var slant = state.FontSlant == FontSlant.Italic ? SKFontStyleSlant.Italic :
				state.FontSlant == FontSlant.Oblique ? SKFontStyleSlant.Oblique : SKFontStyleSlant.Upright;
			using SKTypeface typeface = SkiaFontResolver.Resolve(family, weight, slant, out SKFontHinting fontconfigHinting)
				?? throw new InvalidOperationException("Skia could not resolve GUI font family '" + family + "'; Cairo materialization is disabled for recorded text.");
			return new SKFont(typeface, (float)state.FontSize) {
				Edging = options.Antialias == Antialias.Subpixel ? SKFontEdging.SubpixelAntialias :
					options.Antialias == Antialias.None ? SKFontEdging.Alias : SKFontEdging.Antialias,
				Hinting = options.HintStyle switch {
					HintStyle.None => SKFontHinting.None,
					HintStyle.Slight => SKFontHinting.Slight,
					HintStyle.Medium => SKFontHinting.Normal,
					HintStyle.Full => SKFontHinting.Full,
					HintStyle.Default => fontconfigHinting,
					_ => SKFontHinting.Normal
				},
				LinearMetrics = options.HintMetrics == HintMetrics.Off,
				Subpixel = false
			};
		}
	}
}
