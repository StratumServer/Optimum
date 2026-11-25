using System;
using System.Buffers;
using System.Buffers.Binary;
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

		internal static string NormalizeWindowsFamilyName(string family)
		{
			if (string.IsNullOrEmpty(family)) return string.Empty;
			int nul = family.IndexOf('\0');
			return nul >= 0 ? family.Substring(0, nul) : family;
		}

		internal static bool TryResolveWindowsGenericFamily(string family, out string fixedFamily, out uint pitchAndFamily, out string fallbackFamily)
		{
			fixedFamily = null;
			pitchAndFamily = 0;
			fallbackFamily = null;
			if (string.IsNullOrWhiteSpace(family) ||
				family.Equals("Sans", StringComparison.OrdinalIgnoreCase) ||
				family.Equals("sans-serif", StringComparison.OrdinalIgnoreCase))
			{
				fixedFamily = "Arial";
				return true;
			}
			if (family.Equals("serif", StringComparison.OrdinalIgnoreCase))
			{
				fixedFamily = "Times New Roman";
				return true;
			}
			if (family.Equals("monospace", StringComparison.OrdinalIgnoreCase))
			{
				fixedFamily = "Courier New";
				return true;
			}
			if (family.Equals("cursive", StringComparison.OrdinalIgnoreCase))
			{
				pitchAndFamily = 0x40; // FF_SCRIPT
				fallbackFamily = "Comic Sans MS";
				return true;
			}
			if (family.Equals("fantasy", StringComparison.OrdinalIgnoreCase))
			{
				pitchAndFamily = 0x50; // FF_DECORATIVE
				fallbackFamily = "Impact";
				return true;
			}
			return false;
		}

		internal static bool ShouldTryDirectWriteRequestedFamily(string requestedFamily, string gdiFace)
		{
			string normalized = NormalizeWindowsFamilyName(requestedFamily);
			if (TryResolveWindowsGenericFamily(normalized, out _, out _, out _))
				return false;
			return !string.Equals(normalized, gdiFace, StringComparison.OrdinalIgnoreCase);
		}

		internal static string ResolveWindowsFamilyName(string family)
		{
			family = NormalizeWindowsFamilyName(family);
			if (TryResolveWindowsGenericFamily(family, out string fixedFamily, out uint pitchAndFamily, out string fallbackFamily))
			{
				if (fixedFamily != null) return fixedFamily;
				return windowsFamilyCache.GetOrAdd(family, _ => QueryGdiFaceName(string.Empty, pitchAndFamily, fallbackFamily));
			}

			return windowsFamilyCache.GetOrAdd(family, static requested => QueryGdiFaceName(requested, 0, requested));
		}

		static string QueryGdiFaceName(string requestedFace, uint pitchAndFamily, string fallbackFace)
		{
			IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
			if (dc == IntPtr.Zero) return fallbackFace;
			IntPtr nativeFont = IntPtr.Zero, previousFont = IntPtr.Zero;
			try
			{
				nativeFont = CreateFontW(-24, 0, 0, 0, 400, 0, 0, 0, 1, 4, 0, 5, pitchAndFamily, requestedFace);
				if (nativeFont == IntPtr.Zero) return fallbackFace;
				previousFont = SelectObject(dc, nativeFont);
				if (previousFont == IntPtr.Zero || previousFont == new IntPtr(-1)) return fallbackFace;
				var buffer = new StringBuilder(64);
				int length = GetTextFaceW(dc, buffer.Capacity, buffer);
				return length > 0 ? buffer.ToString() : fallbackFace;
			}
			finally
			{
				if (previousFont != IntPtr.Zero && previousFont != new IntPtr(-1)) SelectObject(dc, previousFont);
				if (nativeFont != IntPtr.Zero) DeleteObject(nativeFont);
				DeleteDC(dc);
			}
		}

		// Vanilla's Windows Cairo resolves a toy family through the GDI font mapper, which
		// accepts names such as "Arial Black", "Calibri Light", and TTC members that
		// DirectWrite files under another family. Conversely, modern Windows 11 typographic
		// families such as "Segoe UI Variable" or "Sitka" exist in DirectWrite even when
		// GDI falls back to Arial. Resolve GDI first for vanilla parity, rescue exact
		// DirectWrite families when GDI fell back, and load GDI font data (including TTC
		// collections and variable fonts) whenever DirectWrite returns a different family.
		internal static SKTypeface CreateWindowsTypeface(string family, SKFontStyleWeight weight, SKFontStyleSlant slant)
		{
			string requested = NormalizeWindowsFamilyName(family);
			string gdiFace = ResolveWindowsFamilyName(requested);
			if (ShouldTryDirectWriteRequestedFamily(requested, gdiFace))
			{
				SKTypeface directWriteRequested = SKTypeface.FromFamilyName(requested, weight, SKFontStyleWidth.Normal, slant);
				if (directWriteRequested != null && string.Equals(directWriteRequested.FamilyName, requested, StringComparison.OrdinalIgnoreCase))
					return directWriteRequested;
				directWriteRequested?.Dispose();
			}

			SKTypeface directWrite = SKTypeface.FromFamilyName(gdiFace, weight, SKFontStyleWidth.Normal, slant);
			if (!NeedsGdiFaceData(gdiFace, directWrite?.FamilyName)) return directWrite;

			SKTypeface gdi = TryLoadGdiFace(gdiFace, weight, slant, out bool synthesizeBold, out bool synthesizeOblique);
			if (gdi == null) return directWrite;
			directWrite?.Dispose();
			int gdiWeight = weight == SKFontStyleWeight.Bold ? 700 : 400;
			byte gdiItalic = slant == SKFontStyleSlant.Upright ? (byte)0 : (byte)1;
			gdiFaceInfos.AddOrUpdate(gdi, new GdiFaceInfo(gdiFace, gdiWeight, gdiItalic, synthesizeBold, synthesizeOblique));
			return gdi;
		}

		// The font data of "Arial Black" reports the family "Arial", so GDI metrics for
		// glyphs of that typeface must be requested by the name GDI selected it under.
		internal static string GdiFaceNameOf(SKTypeface typeface)
			=> gdiFaceInfos.TryGetValue(typeface, out GdiFaceInfo info) ? info.FaceName : typeface.FamilyName;

		internal static void GetGdiFaceStyle(SKTypeface typeface, out string family, out int weight, out byte italic)
		{
			if (typeface != null && gdiFaceInfos.TryGetValue(typeface, out GdiFaceInfo info))
			{
				family = info.FaceName;
				weight = info.Weight;
				italic = info.Italic;
				return;
			}
			family = typeface?.FamilyName ?? "Arial";
			weight = typeface != null && typeface.FontStyle.Weight >= (int)SKFontStyleWeight.Bold ? 700 : 400;
			italic = typeface != null && typeface.FontStyle.Slant != SKFontStyleSlant.Upright ? (byte)1 : (byte)0;
		}

		internal static bool NeedsSyntheticStyle(SKTypeface typeface, out bool embolden, out bool oblique)
		{
			if (typeface != null && gdiFaceInfos.TryGetValue(typeface, out GdiFaceInfo info))
			{
				embolden = info.SynthesizeBold;
				oblique = info.SynthesizeOblique;
				return embolden || oblique;
			}
			embolden = false;
			oblique = false;
			return false;
		}

		internal static bool NeedsGdiFaceData(string gdiFace, string directWriteFamily)
			=> !string.Equals(gdiFace, directWriteFamily, StringComparison.OrdinalIgnoreCase);

		sealed class GdiFaceInfo
		{
			public GdiFaceInfo(string faceName, int weight, byte italic, bool synthesizeBold, bool synthesizeOblique)
			{
				FaceName = faceName;
				Weight = weight;
				Italic = italic;
				SynthesizeBold = synthesizeBold;
				SynthesizeOblique = synthesizeOblique;
			}

			public string FaceName { get; }
			public int Weight { get; }
			public byte Italic { get; }
			public bool SynthesizeBold { get; }
			public bool SynthesizeOblique { get; }
		}

		static readonly ConditionalWeakTable<SKTypeface, GdiFaceInfo> gdiFaceInfos = new ConditionalWeakTable<SKTypeface, GdiFaceInfo>();
		const uint GdiError = 0xFFFFFFFF;
		const uint TtcTag = 0x66637474; // 'ttcf' as GetFontData expects it
		const uint FvarTagBigEndian = 0x66766172; // 'fvar' in SFNT big-endian tag order
		const uint MaxGdiFontBytes = 64 * 1024 * 1024;
		const int MaxSfntTables = 512;
		const int MaxTtcFaces = 4096;

		internal static bool TryGetSfntDirectoryLength(ReadOnlySpan<byte> sfntHeader, out int directoryLength)
		{
			directoryLength = 0;
			if (sfntHeader.Length < 12) return false;
			uint version = BinaryPrimitives.ReadUInt32BigEndian(sfntHeader.Slice(0, 4));
			if (version != 0x00010000u && version != 0x4F54544Fu /* OTTO */ &&
				version != 0x74727565u /* true */ && version != 0x74797031u /* typ1 */)
				return false;
			ushort numTables = BinaryPrimitives.ReadUInt16BigEndian(sfntHeader.Slice(4, 2));
			if (numTables == 0 || numTables > MaxSfntTables) return false;
			directoryLength = 12 + numTables * 16;
			return true;
		}

		internal static bool TryFindTtcFaceIndex(ReadOnlySpan<byte> ttcData, ReadOnlySpan<byte> faceDirectory, out int faceIndex)
		{
			faceIndex = -1;
			if (ttcData.Length < 12 || faceDirectory.Length < 12) return false;
			if (ttcData[0] != (byte)'t' || ttcData[1] != (byte)'t' || ttcData[2] != (byte)'c' || ttcData[3] != (byte)'f')
				return false;
			uint numFonts = BinaryPrimitives.ReadUInt32BigEndian(ttcData.Slice(8, 4));
			if (numFonts == 0 || numFonts > MaxTtcFaces) return false;
			ulong headerEnd = 12UL + (ulong)numFonts * 4UL;
			if (headerEnd > (ulong)ttcData.Length) return false;

			for (int i = 0; i < (int)numFonts; i++)
			{
				uint offset = BinaryPrimitives.ReadUInt32BigEndian(ttcData.Slice(12 + i * 4, 4));
				if ((ulong)offset + (ulong)faceDirectory.Length <= (ulong)ttcData.Length &&
					ttcData.Slice((int)offset, faceDirectory.Length).SequenceEqual(faceDirectory))
				{
					faceIndex = i;
					return true;
				}
			}
			return false;
		}

		internal static bool HasSfntTable(ReadOnlySpan<byte> sfntDirectory, uint tableTagBigEndian)
		{
			if (!TryGetSfntDirectoryLength(sfntDirectory, out int dirLength) || sfntDirectory.Length < dirLength)
				return false;
			for (int offset = 12; offset + 16 <= dirLength; offset += 16)
			{
				if (BinaryPrimitives.ReadUInt32BigEndian(sfntDirectory.Slice(offset, 4)) == tableTagBigEndian)
					return true;
			}
			return false;
		}

		internal static SKFontStyleWidth InferFontStyleWidth(string faceName)
		{
			if (string.IsNullOrWhiteSpace(faceName)) return SKFontStyleWidth.Normal;
			if (faceName.Contains("UltraCondensed", StringComparison.OrdinalIgnoreCase) ||
				faceName.Contains("Ultra Condensed", StringComparison.OrdinalIgnoreCase))
				return SKFontStyleWidth.UltraCondensed;
			if (faceName.Contains("ExtraCondensed", StringComparison.OrdinalIgnoreCase) ||
				faceName.Contains("Extra Condensed", StringComparison.OrdinalIgnoreCase))
				return SKFontStyleWidth.ExtraCondensed;
			if (faceName.Contains("SemiCondensed", StringComparison.OrdinalIgnoreCase) ||
				faceName.Contains("Semi Condensed", StringComparison.OrdinalIgnoreCase))
				return SKFontStyleWidth.SemiCondensed;
			if (faceName.Contains("Condensed", StringComparison.OrdinalIgnoreCase))
				return SKFontStyleWidth.Condensed;
			if (faceName.Contains("UltraExpanded", StringComparison.OrdinalIgnoreCase) ||
				faceName.Contains("Ultra Expanded", StringComparison.OrdinalIgnoreCase))
				return SKFontStyleWidth.UltraExpanded;
			if (faceName.Contains("ExtraExpanded", StringComparison.OrdinalIgnoreCase) ||
				faceName.Contains("Extra Expanded", StringComparison.OrdinalIgnoreCase))
				return SKFontStyleWidth.ExtraExpanded;
			if (faceName.Contains("SemiExpanded", StringComparison.OrdinalIgnoreCase) ||
				faceName.Contains("Semi Expanded", StringComparison.OrdinalIgnoreCase))
				return SKFontStyleWidth.SemiExpanded;
			if (faceName.Contains("Expanded", StringComparison.OrdinalIgnoreCase))
				return SKFontStyleWidth.Expanded;
			return SKFontStyleWidth.Normal;
		}

		static SKData CreateSkDataCopy(byte[] buffer, int length)
		{
			GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
			try
			{
				return SKData.CreateCopy(handle.AddrOfPinnedObject(), (ulong)length);
			}
			finally
			{
				handle.Free();
			}
		}

		static SKTypeface TryLoadGdiFace(string gdiFace, SKFontStyleWeight weight, SKFontStyleSlant slant,
			out bool synthesizeBold, out bool synthesizeOblique)
		{
			synthesizeBold = false;
			synthesizeOblique = false;
			IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
			if (dc == IntPtr.Zero) return null;
			IntPtr nativeFont = IntPtr.Zero, previousFont = IntPtr.Zero;
			byte[] rentedBuffer = null;
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

				bool hasMetrics = GetTextMetricsW(dc, out GdiTextMetric gdiMetrics);
				SKTypeface typeface;
				bool isVariableFont;

				uint ttcSize = GetFontData(dc, TtcTag, 0, null, 0);
				if (ttcSize != GdiError)
				{
					if (ttcSize < 12 || ttcSize > MaxGdiFontBytes) return null;
					var sfntHeader = new byte[12];
					if (GetFontData(dc, 0, 0, sfntHeader, 12) != 12 ||
						!TryGetSfntDirectoryLength(sfntHeader, out int dirLength))
						return null;
					var faceDirectory = new byte[dirLength];
					if (GetFontData(dc, 0, 0, faceDirectory, (uint)dirLength) != (uint)dirLength)
						return null;

					rentedBuffer = ArrayPool<byte>.Shared.Rent((int)ttcSize);
					if (GetFontData(dc, TtcTag, 0, rentedBuffer, ttcSize) != ttcSize)
						return null;
					if (!TryFindTtcFaceIndex(rentedBuffer.AsSpan(0, (int)ttcSize), faceDirectory, out int faceIndex))
						return null;

					using SKData skData = CreateSkDataCopy(rentedBuffer, (int)ttcSize);
					typeface = SKTypeface.FromData(skData, faceIndex);
					isVariableFont = HasSfntTable(faceDirectory, FvarTagBigEndian);
				}
				else
				{
					uint size = GetFontData(dc, 0, 0, null, 0);
					if (size == GdiError || size < 12 || size > MaxGdiFontBytes) return null;
					rentedBuffer = ArrayPool<byte>.Shared.Rent((int)size);
					if (GetFontData(dc, 0, 0, rentedBuffer, size) != size) return null;

					using SKData skData = CreateSkDataCopy(rentedBuffer, (int)size);
					typeface = SKTypeface.FromData(skData, 0);
					isVariableFont = HasSfntTable(rentedBuffer.AsSpan(0, (int)size), FvarTagBigEndian);
				}

				if (typeface == null) return null;

				// A raw variable font file loaded via FromData loses GDI's named-instance weight
				// and width coordinates (e.g. Bahnschrift Light / Condensed). Ask DirectWrite for
				// that family at GDI's selected weight and inferred width while keeping GDI metadata.
				if (isVariableFont && !string.IsNullOrWhiteSpace(typeface.FamilyName))
				{
					int targetWeight = hasMetrics && gdiMetrics.Weight > 0
						? Math.Clamp(gdiMetrics.Weight, 100, 1000)
						: (int)weight;
					SKFontStyleWidth targetWidth = InferFontStyleWidth(gdiFace);
					SKTypeface variable = SKTypeface.FromFamilyName(typeface.FamilyName, targetWeight, (int)targetWidth, slant);
					if (variable != null && string.Equals(variable.FamilyName, typeface.FamilyName, StringComparison.OrdinalIgnoreCase))
					{
						typeface.Dispose();
						typeface = variable;
					}
					else
					{
						variable?.Dispose();
					}
				}

				// Keep the GDI-selected face even when it lacks a separate bold or italic file:
				// DirectWrite already returned a different fallback family, so dropping the face
				// here would replace the user's chosen font with Segoe UI / Arial.
				synthesizeBold = weight == SKFontStyleWeight.Bold && typeface.FontWeight < (int)SKFontStyleWeight.SemiBold;
				synthesizeOblique = slant != SKFontStyleSlant.Upright && typeface.FontSlant == SKFontStyleSlant.Upright;
				return typeface;
			}
			finally
			{
				if (rentedBuffer != null) ArrayPool<byte>.Shared.Return(rentedBuffer);
				if (previousFont != IntPtr.Zero && previousFont != new IntPtr(-1)) SelectObject(dc, previousFont);
				if (nativeFont != IntPtr.Zero) DeleteObject(nativeFont);
				DeleteDC(dc);
			}
		}

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		struct GdiTextMetric
		{
			public int Height, Ascent, Descent, InternalLeading, ExternalLeading;
			public int AverageCharWidth, MaximumCharWidth, Weight, Overhang;
			public int DigitizedAspectX, DigitizedAspectY;
			public char FirstChar, LastChar, DefaultChar, BreakChar;
			public byte Italic, Underlined, StruckOut, PitchAndFamily, CharacterSet;
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
		[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetTextMetricsW(IntPtr dc, out GdiTextMetric metrics);
		[DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(IntPtr gdiObject);
		[DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(IntPtr dc);
	}
}
