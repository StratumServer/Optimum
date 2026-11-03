using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Captures GDI's ClearType mask for the narrow Windows Cairo Arial text path.</summary>
	internal static class WindowsGdiTextCoverage
	{
		const uint BiRgb = 0;
		const uint EtoGlyphIndex = 0x0010;
		const uint TaLeftBaseline = 0x0018;
		const uint DefaultCharset = 1;
		const uint OutDefaultPrecision = 0;
		const uint ClipDefaultPrecision = 0;
		const uint ClearTypeQuality = 5;
		const uint DefaultPitch = 0;
		const uint GgiMarkNonexistingGlyphs = 1;
		const int BkModeOpaque = 2;

		[StructLayout(LayoutKind.Sequential)]
		struct BitmapInfoHeader
		{
			internal uint Size;
			internal int Width, Height;
			internal ushort Planes, BitCount;
			internal uint Compression, SizeImage;
			internal int XPelsPerMeter, YPelsPerMeter;
			internal uint ClrUsed, ClrImportant;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct BitmapInfo
		{
			internal BitmapInfoHeader Header;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
			internal uint[] Colors;
		}

		[StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
		struct TextMetric
		{
			internal int Height, Ascent, Descent, InternalLeading, ExternalLeading;
			internal int AverageCharWidth, MaximumCharWidth, Weight, Overhang;
			internal int DigitizedAspectX, DigitizedAspectY;
			internal char FirstChar, LastChar, DefaultChar, BreakChar;
			internal byte Italic, Underlined, StruckOut, PitchAndFamily, CharSet;
		}

		internal static bool TryCapture(ManagedGraphicsState state, SKFont font, FontOptions options, string text,
			RecordedGuiClip[] clips, int width, int height, SKRect? textInkClip, out SKImage coverage, out PointD origin)
		{
			coverage = null;
			origin = default;
			if (!OperatingSystem.IsWindows() || state == null || font?.Typeface == null || options == null ||
				string.IsNullOrEmpty(text) || text.Length > 4096 || width <= 0 || height <= 0 ||
				clips == null || clips.Length != 0 ||
				options.Antialias != Antialias.Subpixel || options.HintMetrics == HintMetrics.Off ||
				options.SubpixelOrder is not (SubpixelOrder.Default or SubpixelOrder.Rgb or SubpixelOrder.Bgr) ||
				!string.Equals(font.Typeface.FamilyName, "Arial", StringComparison.OrdinalIgnoreCase) ||
				font.Typeface.FontStyle.Weight != (int)SKFontStyleWeight.Normal || font.Typeface.FontStyle.Slant != SKFontStyleSlant.Upright ||
				Math.Abs(font.Size - Math.Round(font.Size)) > 0.001 || font.Size < 1 || font.Size > 128 ||
				font.ScaleX != 1f || font.SkewX != 0f || !font.Subpixel ||
				state.Matrix.Xx != 1 || state.Matrix.Yy != 1 || state.Matrix.Xy != 0 || state.Matrix.Yx != 0 || ContainsSurrogate(text))
				return false;

			state.TryGetCurrentPoint(out double baselineX, out double baselineY);
			PointD translation = state.FontTranslation;
			baselineX += translation.X;
			baselineY += translation.Y;
			baselineX = state.Matrix.Xx * baselineX + state.Matrix.Xy * baselineY + state.Matrix.X0;
			baselineY = state.Matrix.Yx * baselineX + state.Matrix.Yy * baselineY + state.Matrix.Y0;
			if (!double.IsFinite(baselineX) || !double.IsFinite(baselineY) ||
				baselineX < -width || baselineX > width * 2d || baselineY < -height || baselineY > height * 2d)
				return false;
			// Cairo's Win32 glyph path rounds each glyph origin to the device grid.
			int baselineDeviceX = (int)Math.Floor(baselineX + 0.5), baselineDeviceY = (int)Math.Floor(baselineY + 0.5);

			ushort[] expectedGlyphs;
			try { expectedGlyphs = font.GetGlyphs(text); }
			catch { return false; }
			if (expectedGlyphs.Length == 0) return false;

			IntPtr dc = IntPtr.Zero, bitmap = IntPtr.Zero, nativeFont = IntPtr.Zero, oldBitmap = IntPtr.Zero, oldFont = IntPtr.Zero;
			try {
				dc = CreateCompatibleDC(IntPtr.Zero);
				if (dc == IntPtr.Zero) return false;
				int weight = font.Typeface.FontStyle.Weight >= (int)SKFontStyleWeight.Bold ? 700 : 400;
				bool italic = font.Typeface.FontStyle.Slant != SKFontStyleSlant.Upright;
				nativeFont = CreateFontW(-(int)Math.Round(font.Size), 0, 0, 0, weight, italic ? 1u : 0u, 0, 0,
					DefaultCharset, OutDefaultPrecision, ClipDefaultPrecision, ClearTypeQuality, DefaultPitch, "Arial");
				if (nativeFont == IntPtr.Zero) return false;
				oldFont = SelectObject(dc, nativeFont);
				if (oldFont == IntPtr.Zero || oldFont == new IntPtr(-1)) return false;
				if (!GetTextMetricsW(dc, out TextMetric metrics) || metrics.Height <= 0 || metrics.Ascent <= 0 || metrics.Descent < 0)
					return false;

				ushort[] glyphs = new ushort[text.Length];
				uint glyphResult = GetGlyphIndicesW(dc, text, text.Length, glyphs, GgiMarkNonexistingGlyphs);
				if (glyphResult == uint.MaxValue || glyphs.Length != expectedGlyphs.Length) return false;
				for (int i = 0; i < glyphs.Length; i++)
					if (glyphs[i] == ushort.MaxValue || glyphs[i] != expectedGlyphs[i]) return false;

				int[] advances = new int[glyphs.Length];
				if (!GetCharWidthI(dc, 0, (uint)glyphs.Length, glyphs, advances)) return false;
				long runWidth = 0;
				foreach (int advance in advances) { if (advance < 0) return false; runWidth += advance; }
				const int padding = 2;
				if (runWidth + padding * 2L > 32768 || (long)metrics.Height + padding * 2L > 1024) return false;
				int maskWidth = checked((int)Math.Max(runWidth + padding * 2L, 1));
				int maskHeight = checked((int)Math.Max((long)metrics.Height + padding * 2L, 1));
				int glyphBaselineY = checked(padding + metrics.Ascent);
				var bitmapInfo = new BitmapInfo {
					Header = new BitmapInfoHeader {
						Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = maskWidth, Height = -maskHeight,
						Planes = 1, BitCount = 32, Compression = BiRgb,
						SizeImage = checked((uint)(maskWidth * maskHeight * 4))
					},
					Colors = new uint[3]
				};
				bitmap = CreateDIBSection(dc, ref bitmapInfo, 0, out IntPtr pixels, IntPtr.Zero, 0);
				if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero) return false;
				oldBitmap = SelectObject(dc, bitmap);
				if (oldBitmap == IntPtr.Zero || oldBitmap == new IntPtr(-1)) return false;
				var white = new byte[checked(maskWidth * maskHeight * 4)];
				for (int i = 0; i < white.Length; i += 4)
					white[i] = white[i + 1] = white[i + 2] = white[i + 3] = 255;
				Marshal.Copy(white, 0, pixels, white.Length);

				if (SetTextColor(dc, 0) == uint.MaxValue || SetBkColor(dc, 0x00ffffff) == uint.MaxValue || SetBkMode(dc, BkModeOpaque) == 0)
					return false;
				if (SetTextAlign(dc, TaLeftBaseline) == uint.MaxValue) return false;

				GCHandle glyphPin = GCHandle.Alloc(glyphs, GCHandleType.Pinned);
				GCHandle advancePin = GCHandle.Alloc(advances, GCHandleType.Pinned);
				try {
					if (!ExtTextOutW(dc, padding, glyphBaselineY, EtoGlyphIndex, IntPtr.Zero, glyphPin.AddrOfPinnedObject(),
						(uint)glyphs.Length, advancePin.AddrOfPinnedObject())) return false;
					if (!GdiFlush()) return false;
				} finally { advancePin.Free(); glyphPin.Free(); }

				int originX = baselineDeviceX - padding, originY = baselineDeviceY - glyphBaselineY;
				var mask = new SKBitmap(maskWidth, maskHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
				try {
					IntPtr destination = mask.GetPixels();
					if (destination == IntPtr.Zero) return false;
					var bgra = new byte[white.Length];
					Marshal.Copy(pixels, bgra, 0, bgra.Length);
					bool reverse = options.SubpixelOrder == SubpixelOrder.Bgr;
					for (int i = 0; i < bgra.Length; i += 4) {
						int px = originX + (i / 4) % maskWidth, py = originY + (i / 4) / maskWidth;
						if (textInkClip.HasValue && (px < textInkClip.Value.Left || px >= textInkClip.Value.Right ||
							py < textInkClip.Value.Top || py >= textInkClip.Value.Bottom)) {
							bgra[i] = bgra[i + 1] = bgra[i + 2] = 0;
							bgra[i + 3] = 255;
							continue;
						}
						byte b = (byte)(255 - bgra[i]);
						byte g = (byte)(255 - bgra[i + 1]);
						byte r = (byte)(255 - bgra[i + 2]);
						bgra[i] = reverse ? r : b;
						bgra[i + 1] = g;
						bgra[i + 2] = reverse ? b : r;
						bgra[i + 3] = 255;
					}
					Marshal.Copy(bgra, 0, destination, bgra.Length);
					coverage = SKImage.FromBitmap(mask);
					origin = new PointD(originX, originY);
					return coverage != null;
				} finally { mask.Dispose(); }
			} catch (Exception) {
				coverage?.Dispose(); coverage = null;
				return false;
			} finally {
				if (oldFont != IntPtr.Zero && oldFont != new IntPtr(-1)) SelectObject(dc, oldFont);
				if (nativeFont != IntPtr.Zero) DeleteObject(nativeFont);
				if (oldBitmap != IntPtr.Zero && oldBitmap != new IntPtr(-1)) SelectObject(dc, oldBitmap);
				if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
				if (dc != IntPtr.Zero) DeleteDC(dc);
			}
		}

		static bool ContainsSurrogate(string text)
		{
			foreach (char value in text) if (char.IsSurrogate(value)) return true;
			return false;
		}

		[DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateCompatibleDC(IntPtr dc);
		[DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo bitmapInfo, uint usage, out IntPtr bits, IntPtr section, uint offset);
		[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
		[DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
		[DllImport("gdi32.dll", SetLastError = true)] static extern bool DeleteObject(IntPtr gdiObject);
		[DllImport("gdi32.dll", SetLastError = true)] static extern bool DeleteDC(IntPtr dc);
		[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetGlyphIndicesW(IntPtr dc, string text, int count, [Out] ushort[] glyphs, uint flags);
		[DllImport("gdi32.dll", SetLastError = true)] static extern bool GetCharWidthI(IntPtr dc, uint firstGlyph, uint glyphCount, ushort[] glyphs, [Out] int[] widths);
		[DllImport("gdi32.dll", SetLastError = true)] static extern uint SetTextColor(IntPtr dc, uint color);
		[DllImport("gdi32.dll", SetLastError = true)] static extern uint SetBkColor(IntPtr dc, uint color);
		[DllImport("gdi32.dll", SetLastError = true)] static extern int SetBkMode(IntPtr dc, int mode);
		[DllImport("gdi32.dll", SetLastError = true)] static extern uint SetTextAlign(IntPtr dc, uint align);
		[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetTextMetricsW(IntPtr dc, out TextMetric metrics);
		[DllImport("gdi32.dll", SetLastError = true)] static extern bool ExtTextOutW(IntPtr dc, int x, int y, uint options, IntPtr rect, IntPtr text, uint count, IntPtr dx);
		[DllImport("gdi32.dll", SetLastError = true)] static extern bool GdiFlush();
	}
}
