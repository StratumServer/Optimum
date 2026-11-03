using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Resolves recorded GUI fonts through the same system font database used by Cairo.</summary>
	internal static class SkiaFontResolver
	{
		const int FcMatchPattern = 0;
		// Cairo's toy font backend maps Normal to FC_WEIGHT_MEDIUM, not REGULAR.
		const int FcWeightMedium = 100;
		const int FcWeightBold = 200;
		const int FcSlantRoman = 0;
		const int FcSlantItalic = 100;
		const int FcSlantOblique = 110;
		const int FcHintNone = 0;
		const int FcHintSlight = 1;
		const int FcHintMedium = 2;
		const int FcHintFull = 3;

		internal static SKTypeface Resolve(string family, SKFontStyleWeight weight, SKFontStyleSlant slant, out SKFontHinting fontconfigHinting)
			=> Resolve(family, weight, slant, out fontconfigHinting, out _, out _);

		internal static SKTypeface Resolve(string family, SKFontStyleWeight weight, SKFontStyleSlant slant,
			out SKFontHinting fontconfigHinting, out Antialias fontconfigAntialias, out SubpixelOrder fontconfigOrder)
		{
			fontconfigHinting = SKFontHinting.Normal;
			fontconfigAntialias = Antialias.Gray;
			fontconfigOrder = SubpixelOrder.Rgb;
			if (!OperatingSystem.IsLinux())
			{
				// Cairo's Windows toy-font backend resolves its generic aliases through
				// GDI. On current Windows systems it selects Arial for these aliases,
				// while Skia maps "sans-serif" to Segoe UI. Keep the recorded renderer
				// on the same effective face as the Cairo compatibility backend.
				if (OperatingSystem.IsWindows() && family is not null &&
					(family.Equals("sans-serif", StringComparison.OrdinalIgnoreCase) ||
					 family.Equals("serif", StringComparison.OrdinalIgnoreCase) ||
					 family.Equals("monospace", StringComparison.OrdinalIgnoreCase)))
					family = "Arial";
				return SKTypeface.FromFamilyName(family, weight, SKFontStyleWidth.Normal, slant);
			}
			if (FcInit() == 0) throw new InvalidOperationException("Fontconfig could not initialize GUI font resolution.");

			IntPtr pattern = FcPatternCreate();
			if (pattern == IntPtr.Zero) throw new InvalidOperationException("Fontconfig could not create a GUI font query.");
			try
			{
				if (FcPatternAddString(pattern, "family", family) == 0 ||
					FcPatternAddInteger(pattern, "weight", weight == SKFontStyleWeight.Bold ? FcWeightBold : FcWeightMedium) == 0 ||
					FcPatternAddInteger(pattern, "slant", slant == SKFontStyleSlant.Italic ? FcSlantItalic :
						slant == SKFontStyleSlant.Oblique ? FcSlantOblique : FcSlantRoman) == 0)
					throw new InvalidOperationException("Fontconfig rejected GUI font family '" + family + "'.");

				FcConfigSubstitute(IntPtr.Zero, pattern, FcMatchPattern);
				FcDefaultSubstitute(pattern);
				IntPtr match = FcFontMatch(IntPtr.Zero, pattern, out int result);
				if (match == IntPtr.Zero) throw new InvalidOperationException("Fontconfig found no match for GUI font family '" + family + "'.");
				try
				{
					if (result != 0 || FcPatternGetString(match, "file", 0, out IntPtr filePointer) != 0 || filePointer == IntPtr.Zero)
						throw new InvalidOperationException("Fontconfig could not identify the matched file for GUI font family '" + family + "'.");
					if (FcPatternGetInteger(match, "hintstyle", 0, out int hintStyle) == 0) {
						fontconfigHinting = FcPatternGetBool(match, "hinting", 0, out int hintingEnabled) == 0 && hintingEnabled == 0
							? SKFontHinting.None
							: hintStyle switch {
								FcHintNone => SKFontHinting.None,
								FcHintSlight => SKFontHinting.Slight,
								FcHintMedium => SKFontHinting.Normal,
								FcHintFull => SKFontHinting.Full,
								_ => SKFontHinting.Normal
							};
					}
					if (FcPatternGetInteger(match, "rgba", 0, out int rgba) == 0) {
						fontconfigOrder = rgba switch { 2 => SubpixelOrder.Bgr, 3 => SubpixelOrder.Vrgb, 4 => SubpixelOrder.Vbgr, _ => SubpixelOrder.Rgb };
						if (rgba >= 1 && rgba <= 4) fontconfigAntialias = Antialias.Subpixel;
					}
					if (FcPatternGetBool(match, "antialias", 0, out int antialias) == 0 && antialias == 0) fontconfigAntialias = Antialias.None;
					string path = Marshal.PtrToStringUTF8(filePointer);
					if (FcPatternGetInteger(match, "index", 0, out int faceIndex) != 0) faceIndex = 0;
					return SKTypeface.FromFile(path, faceIndex)
						?? throw new InvalidOperationException("Skia could not open Fontconfig's match for GUI font family '" + family + "': " + path);
				}
				finally { FcPatternDestroy(match); }
			}
			finally { FcPatternDestroy(pattern); }
		}

		// Cairo's Windows toy-font backend advances each glyph on the GDI device
		// pixel grid. Use the same integer advances for the recorded text run while
		// leaving glyph coverage to Skia. This is limited to integral-size Arial
		// runs so fractional transforms and unrelated font metrics retain Skia's
		// normal behavior until their parity is measured separately.
		internal static bool TryGetWindowsGlyphAdvances(SKFont font, ushort[] glyphs, out float[] advances)
		{
			advances = null;
			if (!OperatingSystem.IsWindows() || font == null || font.Typeface == null || glyphs == null || glyphs.Length == 0 ||
				!string.Equals(font.Typeface.FamilyName, "Arial", StringComparison.OrdinalIgnoreCase) ||
				Math.Abs(font.Size - Math.Round(font.Size)) > 0.001 || font.ScaleX != 1f)
				return false;

			IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
			if (dc == IntPtr.Zero) return false;
			IntPtr nativeFont = IntPtr.Zero;
			IntPtr previousFont = IntPtr.Zero;
			try
			{
				int height = -(int)Math.Round(font.Size);
				int weight = font.Typeface.FontStyle.Weight >= (int)SKFontStyleWeight.Bold ? 700 : 400;
				bool italic = font.Typeface.FontStyle.Slant != SKFontStyleSlant.Upright;
				nativeFont = CreateFontW(height, 0, 0, 0, weight, italic ? 1u : 0u, 0, 0,
					1, 4, 0, 5, 0, font.Typeface.FamilyName);
				if (nativeFont == IntPtr.Zero) return false;
				previousFont = SelectObject(dc, nativeFont);
				if (previousFont == IntPtr.Zero || previousFont == new IntPtr(-1)) return false;
				var widths = new int[glyphs.Length];
				if (!GetCharWidthI(dc, 0, (uint)glyphs.Length, glyphs, widths)) return false;
				advances = Array.ConvertAll(widths, width => (float)width);
				return true;
			}
			finally
			{
				if (previousFont != IntPtr.Zero && previousFont != new IntPtr(-1)) SelectObject(dc, previousFont);
				if (nativeFont != IntPtr.Zero) DeleteObject(nativeFont);
				DeleteDC(dc);
			}
		}

		[DllImport("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)] static extern int FcInit();
		[DllImport("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr FcPatternCreate();
		[DllImport("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)] static extern int FcPatternAddString(IntPtr pattern, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectName, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
		[DllImport("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)] static extern int FcPatternAddInteger(IntPtr pattern, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectName, int value);
		[DllImport("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)] static extern void FcConfigSubstitute(IntPtr config, IntPtr pattern, int kind);
		[DllImport("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)] static extern void FcDefaultSubstitute(IntPtr pattern);
		[DllImport("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr FcFontMatch(IntPtr config, IntPtr pattern, out int result);
		[DllImport("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)] static extern int FcPatternGetString(IntPtr pattern, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectName, int index, out IntPtr value);
		[DllImport("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)] static extern int FcPatternGetInteger(IntPtr pattern, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectName, int index, out int value);
		[DllImport("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)] static extern int FcPatternGetBool(IntPtr pattern, [MarshalAs(UnmanagedType.LPUTF8Str)] string objectName, int index, out int value);
		[DllImport("libfontconfig.so.1", CallingConvention = CallingConvention.Cdecl)] static extern void FcPatternDestroy(IntPtr pattern);
		[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
		[DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateCompatibleDC(IntPtr dc);
		[DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
		[DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCharWidthI(IntPtr dc, uint firstGlyph, uint glyphCount, ushort[] glyphs, int[] widths);
		[DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(IntPtr gdiObject);
		[DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(IntPtr dc);
	}
}
