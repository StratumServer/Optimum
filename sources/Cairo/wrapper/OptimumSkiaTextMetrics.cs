using System;
using SkiaSharp;

namespace Cairo
{
	/// <summary>
	/// Skia text measurement for GUI layout, with the signature the patched <c>CairoFont</c> calls. It measures with the
	/// options <c>CairoFont.SetupContext</c> sets (subpixel antialiasing, default hinting), so layout no longer needs a
	/// Cairo context to size text.
	/// </summary>
	public static class OptimumSkiaTextMetrics
	{
		public static FontExtents GetFontExtents(string family, FontWeight weight, FontSlant slant, double size)
		{
			if (!(size > 0) || double.IsInfinity(size)) return default;
			using FontOptions options = LayoutOptions();
			using SKFont font = SkiaGuiFont.Create(family ?? string.Empty, slant, weight, size, options);
			return SkiaTextMetrics.FontExtents(font, options);
		}

		public static TextExtents GetTextExtents(string family, FontWeight weight, FontSlant slant, double size, string text)
		{
			if (string.IsNullOrEmpty(text) || !(size > 0) || double.IsInfinity(size)) return default;
			using FontOptions options = LayoutOptions();
			using SKFont font = SkiaGuiFont.Create(family ?? string.Empty, slant, weight, size, options);
			return SkiaTextMetrics.TextExtents(font, options, text);
		}

		static FontOptions LayoutOptions() => FontOptions.FromValues(Antialias.Subpixel, HintStyle.Default, HintMetrics.Default, SubpixelOrder.Default);
	}
}
