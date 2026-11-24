using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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

		static readonly ConcurrentDictionary<string, string> windowsFamilyCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		static readonly ConcurrentDictionary<(string family, SKFontStyleWeight weight, SKFontStyleSlant slant), LinuxFontMatch> linuxMatchCache = new();
		static readonly ConcurrentDictionary<(string path, int faceIndex), SKTypeface> linuxTypefaceCache = new();
		static readonly ConcurrentDictionary<(string family, SKFontStyleWeight weight, SKFontStyleSlant slant), SKTypeface> systemTypefaceCache = new();

		private readonly struct LinuxFontMatch
		{
			public LinuxFontMatch(string path, int faceIndex, SKFontHinting hinting, Antialias antialias, SubpixelOrder order)
			{
				Path = path;
				FaceIndex = faceIndex;
				Hinting = hinting;
				Antialias = antialias;
				Order = order;
			}

			public string Path { get; }
			public int FaceIndex { get; }
			public SKFontHinting Hinting { get; }
			public Antialias Antialias { get; }
			public SubpixelOrder Order { get; }
		}

		internal static SKTypeface Resolve(string family, SKFontStyleWeight weight, SKFontStyleSlant slant, out SKFontHinting fontconfigHinting)
			=> Resolve(family, weight, slant, out fontconfigHinting, out _, out _);

		internal static void ResolveOptions(string family, SKFontStyleWeight weight, SKFontStyleSlant slant,
			out SKFontHinting fontconfigHinting, out Antialias fontconfigAntialias, out SubpixelOrder fontconfigOrder)
		{
			if (!OperatingSystem.IsLinux())
			{
				fontconfigHinting = SKFontHinting.Normal;
				fontconfigAntialias = OperatingSystem.IsWindows() ? Antialias.Subpixel : Antialias.Gray;
				fontconfigOrder = SubpixelOrder.Rgb;
				return;
			}
			LinuxFontMatch matched = ResolveLinuxMatch(family, weight, slant);
			fontconfigHinting = matched.Hinting;
			fontconfigAntialias = matched.Antialias;
			fontconfigOrder = matched.Order;
		}

		internal static SKTypeface BorrowTypeface(string family, SKFontStyleWeight weight, SKFontStyleSlant slant, out SKFontHinting fontconfigHinting)
		{
			if (!OperatingSystem.IsLinux())
			{
				fontconfigHinting = SKFontHinting.Normal;
				return systemTypefaceCache.GetOrAdd((family, weight, slant),
					static key => OperatingSystem.IsWindows()
						? CreateWindowsTypeface(key.family, key.weight, key.slant)
						: SKTypeface.FromFamilyName(key.family, key.weight, SKFontStyleWidth.Normal, key.slant));
			}

			LinuxFontMatch matched = ResolveLinuxMatch(family, weight, slant);
			fontconfigHinting = matched.Hinting;
			return linuxTypefaceCache.GetOrAdd((matched.Path, matched.FaceIndex),
				static key => SKTypeface.FromFile(key.path, key.faceIndex)
					?? throw new InvalidOperationException("Skia could not open Fontconfig's match: " + key.path));
		}

		internal static SKTypeface Resolve(string family, SKFontStyleWeight weight, SKFontStyleSlant slant,
			out SKFontHinting fontconfigHinting, out Antialias fontconfigAntialias, out SubpixelOrder fontconfigOrder)
		{
			fontconfigHinting = SKFontHinting.Normal;
			fontconfigAntialias = OperatingSystem.IsWindows() ? Antialias.Subpixel : Antialias.Gray;
			fontconfigOrder = SubpixelOrder.Rgb;
			if (!OperatingSystem.IsLinux())
			{
				return OperatingSystem.IsWindows()
					? CreateWindowsTypeface(family, weight, slant)
					: SKTypeface.FromFamilyName(family, weight, SKFontStyleWidth.Normal, slant);
			}

			LinuxFontMatch matched = ResolveLinuxMatch(family, weight, slant);
			fontconfigHinting = matched.Hinting;
			fontconfigAntialias = matched.Antialias;
			fontconfigOrder = matched.Order;
			return SKTypeface.FromFile(matched.Path, matched.FaceIndex)
				?? throw new InvalidOperationException("Skia could not open Fontconfig's match for GUI font family '" + family + "': " + matched.Path);
		}

		private static LinuxFontMatch ResolveLinuxMatch(string family, SKFontStyleWeight weight, SKFontStyleSlant slant)
		{
			return linuxMatchCache.GetOrAdd((family, weight, slant), static key => QueryFontconfig(key.family, key.weight, key.slant));
		}

		private static LinuxFontMatch QueryFontconfig(string family, SKFontStyleWeight weight, SKFontStyleSlant slant)
		{
			SKFontHinting fontconfigHinting = SKFontHinting.Normal;
			Antialias fontconfigAntialias = Antialias.Gray;
			SubpixelOrder fontconfigOrder = SubpixelOrder.Rgb;
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
					return new LinuxFontMatch(path, faceIndex, fontconfigHinting, fontconfigAntialias, fontconfigOrder);
				}
				finally { FcPatternDestroy(match); }
			}
			finally { FcPatternDestroy(pattern); }
		}

		internal static string ResolveWindowsFamilyName(string family)
		{
			if (string.IsNullOrWhiteSpace(family) ||
				family.Equals("Sans", StringComparison.OrdinalIgnoreCase) ||
				family.Equals("sans-serif", StringComparison.OrdinalIgnoreCase) ||
				family.Equals("serif", StringComparison.OrdinalIgnoreCase) ||
				family.Equals("monospace", StringComparison.OrdinalIgnoreCase))
				return "Arial";

			int nul = family.IndexOf('\0');
			if (nul >= 0) family = family.Substring(0, nul);
			if (string.IsNullOrWhiteSpace(family)) return "Arial";

			return windowsFamilyCache.GetOrAdd(family, static requested =>
			{
				IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
				if (dc == IntPtr.Zero) return requested;
				IntPtr nativeFont = IntPtr.Zero, previousFont = IntPtr.Zero;
				try
				{
					nativeFont = CreateFontW(-24, 0, 0, 0, 400, 0, 0, 0, 1, 4, 0, 5, 0, requested);
					if (nativeFont == IntPtr.Zero) return requested;
					previousFont = SelectObject(dc, nativeFont);
					if (previousFont == IntPtr.Zero || previousFont == new IntPtr(-1)) return requested;
					var buffer = new StringBuilder(64);
					int length = GetTextFaceW(dc, buffer.Capacity, buffer);
					return length > 0 ? buffer.ToString() : requested;
				}
				finally
				{
					if (previousFont != IntPtr.Zero && previousFont != new IntPtr(-1)) SelectObject(dc, previousFont);
					if (nativeFont != IntPtr.Zero) DeleteObject(nativeFont);
					DeleteDC(dc);
				}
			});
		}

		// Vanilla's Windows Cairo resolves a toy family through the GDI font mapper, which
		// accepts names such as "Arial Black" that DirectWrite files under another family.
		// When DirectWrite does not return the face GDI selected, build the typeface from
		// that face's font data so the weight and shape match vanilla.
		internal static SKTypeface CreateWindowsTypeface(string family, SKFontStyleWeight weight, SKFontStyleSlant slant)
		{
			string gdiFace = ResolveWindowsFamilyName(family);
			SKTypeface directWrite = SKTypeface.FromFamilyName(gdiFace, weight, SKFontStyleWidth.Normal, slant);
			if (!NeedsGdiFaceData(gdiFace, directWrite?.FamilyName)) return directWrite;

			SKTypeface gdi = TryLoadGdiFace(gdiFace, weight, slant);
			if (gdi == null) return directWrite;
			directWrite?.Dispose();
			gdiFaceNames.Add(gdi, gdiFace);
			return gdi;
		}

		// The font data of "Arial Black" reports the family "Arial", so GDI metrics for
		// glyphs of that typeface must be requested by the name GDI selected it under.
		internal static string GdiFaceNameOf(SKTypeface typeface)
			=> gdiFaceNames.TryGetValue(typeface, out string gdiFace) ? gdiFace : typeface.FamilyName;

		internal static bool NeedsGdiFaceData(string gdiFace, string directWriteFamily)
			=> !string.Equals(gdiFace, directWriteFamily, StringComparison.OrdinalIgnoreCase);

		static readonly ConditionalWeakTable<SKTypeface, string> gdiFaceNames = new ConditionalWeakTable<SKTypeface, string>();
		const uint GdiError = 0xFFFFFFFF;
		const uint TtcTag = 0x66637474; // 'ttcf' as GetFontData expects it
		const uint MaxGdiFontBytes = 64 * 1024 * 1024;

		static SKTypeface TryLoadGdiFace(string gdiFace, SKFontStyleWeight weight, SKFontStyleSlant slant)
		{
			IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
			if (dc == IntPtr.Zero) return null;
			IntPtr nativeFont = IntPtr.Zero, previousFont = IntPtr.Zero;
			try
			{
				nativeFont = CreateFontW(-24, 0, 0, 0, weight == SKFontStyleWeight.Bold ? 700 : 400, slant == SKFontStyleSlant.Upright ? 0u : 1u, 0, 0, 1, 4, 0, 5, 0, gdiFace);
				if (nativeFont == IntPtr.Zero) return null;
				previousFont = SelectObject(dc, nativeFont);
				if (previousFont == IntPtr.Zero || previousFont == new IntPtr(-1)) return null;
				var selectedFace = new StringBuilder(64);
				if (GetTextFaceW(dc, selectedFace.Capacity, selectedFace) <= 0 ||
					!string.Equals(selectedFace.ToString(), gdiFace, StringComparison.OrdinalIgnoreCase))
					return null;

				// A collection member's table directory points into the whole file and its
				// face index is unknown here, so keep DirectWrite's answer for collections.
				if (GetFontData(dc, TtcTag, 0, null, 0) != GdiError) return null;
				uint size = GetFontData(dc, 0, 0, null, 0);
				if (size == GdiError || size == 0 || size > MaxGdiFontBytes) return null;
				var data = new byte[size];
				if (GetFontData(dc, 0, 0, data, size) != size) return null;

				using SKData skData = SKData.CreateCopy(data);
				SKTypeface typeface = SKTypeface.FromData(skData, 0);
				if (typeface == null) return null;
				// GDI would synthesize a missing bold or italic; keep DirectWrite's face then.
				if ((weight == SKFontStyleWeight.Bold && typeface.FontWeight < (int)SKFontStyleWeight.SemiBold) ||
					(slant != SKFontStyleSlant.Upright && typeface.FontSlant == SKFontStyleSlant.Upright))
				{
					typeface.Dispose();
					return null;
				}
				return typeface;
			}
			finally
			{
				if (previousFont != IntPtr.Zero && previousFont != new IntPtr(-1)) SelectObject(dc, previousFont);
				if (nativeFont != IntPtr.Zero) DeleteObject(nativeFont);
				DeleteDC(dc);
			}
		}

		// Cairo's Windows toy-font backend advances each glyph on the GDI device
		// pixel grid when metrics hinting is active, or on the unscaled EM grid when
		// metrics hinting is disabled or the CTM does not preserve axes.
		internal static bool TryGetWindowsGlyphAdvances(SKFont font, ushort[] glyphs, out float[] advances)
			=> TryGetWindowsGlyphAdvances(font, null, true, glyphs, out advances);

		internal static bool TryGetWindowsGlyphAdvances(SKFont font, Matrix matrix, bool hintMetrics, ushort[] glyphs, out float[] advances)
		{
			advances = null;
			if (!OperatingSystem.IsWindows() || font == null || font.Typeface == null || glyphs == null || glyphs.Length == 0)
				return false;

			using var outlines = SkiaHintedFontOutlines.Create(font, matrix ?? new Matrix());
			if (outlines == null) return false;
			var result = new float[glyphs.Length];
			for (int i = 0; i < glyphs.Length; i++)
				result[i] = (float)outlines.GetGlyphAdvance(glyphs[i], hintMetrics);
			advances = result;
			return true;
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
		[DllImport("gdi32.dll", SetLastError = true)] static extern uint GetFontData(IntPtr dc, uint table, uint offset, byte[] buffer, uint length);
		[DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateCompatibleDC(IntPtr dc);
		[DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
		[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern int GetTextFaceW(IntPtr dc, int count, StringBuilder faceName);
		[DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(IntPtr gdiObject);
		[DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(IntPtr dc);
	}
}
