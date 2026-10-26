using System;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Builds the Skia font that stands in for a recorded Cairo font selection.</summary>
	internal static class SkiaGuiFont
	{
		internal static SKFont Create(ManagedGraphicsState state, FontOptions options)
		{
			string family = state.FontFamily;
			if (family == null)
				throw new InvalidOperationException("The selected font face has no managed family metadata; Cairo materialization is disabled for recorded text.");
			return Create(family, state.FontSlant, state.FontWeight, state.FontSize, options, state.FontFileSource);
		}

		internal static SKFont Create(string family, FontSlant fontSlant, FontWeight fontWeight, double size, FontOptions options)
			=> Create(family, fontSlant, fontWeight, size, options, null);

		static SKFont Create(string family, FontSlant fontSlant, FontWeight fontWeight, double size, FontOptions options, ManagedFileFontSource fileSource)
		{
			using var fontSelection = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.FontSelection);
            if (string.IsNullOrWhiteSpace (family)) family = "Sans";
			var weight = fontWeight == FontWeight.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
			var slant = fontSlant == FontSlant.Italic ? SKFontStyleSlant.Italic :
				fontSlant == FontSlant.Oblique ? SKFontStyleSlant.Oblique : SKFontStyleSlant.Upright;
			SKFontHinting fontconfigHinting = SKFontHinting.Full;
			SKTypeface typeface = fileSource?.Typeface ?? SkiaFontResolver.Resolve(family, weight, slant, out fontconfigHinting)
				?? throw new InvalidOperationException("Skia could not resolve GUI font family '" + family + "'; Cairo materialization is disabled for recorded text.");
			try {
			var font = new SKFont(typeface, (float)size) {
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
			fileSource?.ApplyLoadOptions(font, options);
			return font;
			} finally { if (fileSource == null) typeface.Dispose(); }
		}
	}
}
