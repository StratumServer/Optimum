using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Retains platform-hinted glyph geometry, metrics, and coverage (FreeType on Linux, GDI on Windows).</summary>
	internal sealed class SkiaHintedFontOutlines : IDisposable
	{
		const string FreeType = "libfreetype.so.6";
		readonly SKStreamAsset stream;
		readonly double scaleX, scaleY;
		readonly int loadFlags;
		readonly WindowsGdiFont windowsFont;
		readonly bool windowsRoundMetrics;
		GCHandle pinnedBytes;
		IntPtr library, face;

		internal static SkiaHintedFontOutlines Create(SKFont font, Matrix matrix, int fontLoadOptions = 0, bool fileSource = false)
		{
			if (font?.Typeface == null || font.Size <= 0 || matrix is null)
				return null;

			if (OperatingSystem.IsWindows())
			{
				if (fileSource) return null;
				double det = matrix.Xx * matrix.Yy - matrix.Xy * matrix.Yx;
				if (!double.IsFinite(det) || Math.Abs(det) <= 1e-9 || !double.IsFinite(font.Size))
					return null;
				WindowsGdiFont gdiFont = WindowsGdiFont.GetOrCreate(font, matrix);
				return gdiFont != null ? new SkiaHintedFontOutlines(gdiFont, !font.LinearMetrics && gdiFont.PreserveAxes, matrix.Xx, matrix.Yy) : null;
			}

			// The device grid determines hinting. Rotated or sheared outlines keep
			// Skia's scalable geometry rather than hinting against the wrong axes.
			if (!OperatingSystem.IsLinux() || (font.Hinting == SKFontHinting.None && fontLoadOptions == 0) ||
				matrix.Xy != 0 || matrix.Yx != 0 || matrix.Xx <= 0 || matrix.Yy <= 0 ||
				!double.IsFinite(matrix.Xx * font.Size) || !double.IsFinite(matrix.Yy * font.Size))
				return null;
			try { return new SkiaHintedFontOutlines(font, matrix.Xx, matrix.Yy, fontLoadOptions); }
			// Skia's scalable outlines remain available on systems without the
			// optional platform hinter. Neither route requires native Cairo.
			catch (DllNotFoundException) { return null; }
			catch (EntryPointNotFoundException) { return null; }
		}

		SkiaHintedFontOutlines(WindowsGdiFont windowsFont, bool windowsRoundMetrics, double scaleX, double scaleY)
		{
			this.windowsFont = windowsFont;
			this.windowsRoundMetrics = windowsRoundMetrics;
			this.scaleX = scaleX;
			this.scaleY = scaleY;
		}

		SkiaHintedFontOutlines(SKFont font, double scaleX, double scaleY, int fontLoadOptions)
		{
			this.scaleX = scaleX; this.scaleY = scaleY;
			// LIGHT is Cairo/Fontconfig's slight vertical hinting. GetGlyphPath
			// deliberately returns unhinted outlines even on a hinted SKFont.
			loadFlags = (fontLoadOptions & ~(15 << 16)) | 8 /* FT_LOAD_NO_BITMAP */;
			loadFlags |= font.Edging == SKFontEdging.Alias ? (2 << 16) | 4096 /* FT_LOAD_TARGET_MONO | FT_LOAD_MONOCHROME */ :
					font.Hinting == SKFontHinting.Slight ? 1 << 16 :
					font.Edging == SKFontEdging.SubpixelAntialias ? 3 << 16 : 0;
			try {
				stream = font.Typeface.OpenStream(out int faceIndex)
					?? throw new InvalidOperationException("The selected Skia font has no outline stream.");
				IntPtr bytes = stream.GetMemoryBase();
				if (bytes == IntPtr.Zero) {
					var copy = new byte[stream.Length];
					if (stream.Read(copy, copy.Length) != copy.Length) throw new InvalidOperationException("The selected Skia font stream is incomplete.");
					pinnedBytes = GCHandle.Alloc(copy, GCHandleType.Pinned);
					bytes = pinnedBytes.AddrOfPinnedObject();
				}
				Check(FT_Init_FreeType(out library), "initialize");
				Check(FT_New_Memory_Face(library, bytes, stream.Length, faceIndex, out face), "open font");
				Check(FT_Set_Char_Size(face, (nint)Math.Round(font.Size * font.ScaleX * scaleX * 64),
					(nint)Math.Round(font.Size * scaleY * 64), 72, 72), "size font");
			} catch { Dispose(); throw; }
		}

		internal bool IsWindows => windowsFont != null;

		internal SKBitmap GetGlyphCoverage(ushort glyph, Antialias antialias, SubpixelOrder order, out int left, out int top, double fractionX = 0, double fractionY = 0)
		{
			if (windowsFont != null)
				return windowsFont.GetGlyphCoverage(glyph, antialias, windowsRoundMetrics, out left, out top);

			var delta = new Vector { X = (nint)Math.Round(fractionX * 64), Y = (nint)Math.Round(-fractionY * 64) };
			FT_Set_Transform(face, IntPtr.Zero, ref delta);
			bool lcd = antialias == Antialias.Subpixel;
			bool vertical = order == SubpixelOrder.Vrgb || order == SubpixelOrder.Vbgr;
			bool reverse = order == SubpixelOrder.Bgr || order == SubpixelOrder.Vbgr;
			if (lcd) Check(FT_Library_SetLcdFilter(library, 1), "set LCD filter");
			Check(FT_Load_Glyph(face, glyph, loadFlags), "load coverage glyph");
			IntPtr slot = Marshal.PtrToStructure<FacePrefix>(face).Glyph;
			Check(FT_Render_Glyph(slot, lcd ? (vertical ? 4 : 3) : antialias == Antialias.None ? 2 : 0), "render coverage glyph");
			var rendered = Marshal.PtrToStructure<RenderedSlot>(slot);
			left = rendered.Left; top = rendered.Top;
			int width = checked((int)rendered.Bitmap.Width), rows = checked((int)rendered.Bitmap.Rows);
			int height = lcd && vertical ? rows / 3 : rows;
			if (lcd && !vertical) width /= 3;
			if (width == 0 || height == 0) return null;
			var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
			for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
				byte Read(int px, int py) {
					IntPtr row = IntPtr.Add(rendered.Bitmap.Buffer, rendered.Bitmap.Pitch >= 0 ? py * rendered.Bitmap.Pitch : (rows - 1 - py) * -rendered.Bitmap.Pitch);
					if (rendered.Bitmap.PixelMode == 1) return (Marshal.ReadByte(row, px >> 3) & (128 >> (px & 7))) != 0 ? (byte)255 : (byte)0;
					return Marshal.ReadByte(row, px);
				}
				byte r, g, b;
				if (lcd && vertical) { r = Read(x, y * 3); g = Read(x, y * 3 + 1); b = Read(x, y * 3 + 2); }
				else if (lcd) { r = Read(x * 3, y); g = Read(x * 3 + 1, y); b = Read(x * 3 + 2, y); }
				else r = g = b = Read(x, y);
				result.SetPixel(x, y, new SKColor(reverse ? b : r, g, reverse ? r : b, 255));
			}
			return result;
		}

		internal bool TryGetDeviceGlyphBounds(ushort glyph, Antialias antialias, bool round, out double left, out double top, out double right, out double bottom)
		{
			if (windowsFont != null)
				return windowsFont.TryGetDeviceGlyphBounds(glyph, antialias, round, out left, out top, out right, out bottom);

			SKRect ink = GetGlyphBounds(glyph, round);
			if (ink.Width <= 0 || ink.Height <= 0)
			{
				left = top = right = bottom = 0;
				return false;
			}
			left = Math.Round(scaleX * ink.Left * 256) / 256;
			top = Math.Round(scaleY * ink.Top * 256) / 256;
			right = Math.Round(scaleX * ink.Right * 256) / 256;
			bottom = Math.Round(scaleY * ink.Bottom * 256) / 256;
			return true;
		}

		internal bool TryGetFontExtents(out FontExtents extents)
		{
			if (windowsFont != null)
				return windowsFont.TryGetFontExtents(out extents);
			extents = default;
			return false;
		}

		internal SKRect GetGlyphBounds(ushort glyph, bool round)
		{
			if (windowsFont != null)
				return windowsFont.GetGlyphBounds(glyph, round);

			Check(FT_Load_Glyph(face, glyph, loadFlags), "load glyph metrics");
			var metrics = Marshal.PtrToStructure<SlotPrefix>(Marshal.PtrToStructure<FacePrefix>(face).Glyph).Metrics;
			double x = (double)metrics.BearingX / 64, y = (double)metrics.BearingY / 64;
			double width = (double)metrics.Width / 64, height = (double)metrics.Height / 64;
			double right = x + width, top = -y, bottom = height - y;
			if (round) { x = Math.Floor(x); top = Math.Floor(top); right = Math.Ceiling(right); bottom = Math.Ceiling(bottom); }
			return new SKRect((float)(x / scaleX), (float)(top / scaleY), (float)(right / scaleX), (float)(bottom / scaleY));
		}

		internal double GetGlyphAdvance(ushort glyph, bool round)
		{
			if (windowsFont != null)
				return windowsFont.GetGlyphAdvance(glyph, round);

			Check(FT_Load_Glyph(face, glyph, loadFlags), "load glyph advance");
			var slot = Marshal.PtrToStructure<RenderedSlot>(Marshal.PtrToStructure<FacePrefix>(face).Glyph);
			// Cairo uses the linear advance when metric hinting is disabled, even
			// when the outline itself was loaded with grid fitting enabled.
			return round ? Math.Floor((double)slot.Advance.X / 64 + 0.5) / scaleX
				: (double)slot.LinearHorizontalAdvance / (65536 * scaleX);
		}

		internal SKPath GetGlyphPath(ushort glyph)
		{
			if (windowsFont != null)
				return windowsFont.GetGlyphPath(glyph);

			Check(FT_Load_Glyph(face, glyph, loadFlags), "load glyph");
			IntPtr slot = Marshal.PtrToStructure<FacePrefix>(face).Glyph;
			Check(FT_Get_Glyph(slot, out IntPtr image), "copy glyph");
			try {
				if ((uint)Marshal.ReadInt32(image, IntPtr.Size * 2) != 0x6f75746c /* FT_GLYPH_FORMAT_OUTLINE */)
					return null;
				OutlineGlyph outlineGlyph = Marshal.PtrToStructure<OutlineGlyph>(image);
				var sink = new OutlineSink(scaleX, scaleY);
				var functions = new OutlineFunctions {
					Move = sink.Move, Line = sink.Line, Conic = sink.Conic, Cubic = sink.Cubic
				};
				try {
					int error = FT_Outline_Decompose(ref outlineGlyph.Outline, ref functions, IntPtr.Zero);
					if (sink.Failure != null) throw new InvalidOperationException("Skia could not retain the hinted glyph outline.", sink.Failure);
					Check(error, "decompose glyph");
					sink.Path.Close();
					return sink.Path;
				} catch { sink.Path.Dispose(); throw; }
			} finally { FT_Done_Glyph(image); }
		}

		sealed class WindowsGdiFont
		{
			const uint GgoMetrics = 0x0080;
			const uint GgoNative = 0x0082;
			const uint EtoGlyphIndex = 0x0010;
			const uint TaLeftBaseline = 0x0018;
			const int GmAdvanced = 2;
			const int BkModeTransparent = 1;

			readonly record struct FontKey(string Family, int Weight, byte Italic, double Size, float ScaleX, double Mxx, double Mxy, double Myx, double Myy, byte DefaultQuality);
			readonly record struct CoverageKey(ushort Glyph, byte Quality, bool RoundMetrics);
			readonly record struct CachedCoverage(byte[] Rgba, int Width, int Height, int Left, int Top);
			readonly record struct CachedMetrics(
				double ScaledAdvance,
				double UnscaledAdvance,
				double UnscaledLeft,
				double UnscaledTop,
				double UnscaledWidth,
				double UnscaledHeight,
				int ScaledOriginX,
				int ScaledOriginY,
				int ScaledBlackBoxX,
				int ScaledBlackBoxY);

			static readonly ConcurrentDictionary<FontKey, WindowsGdiFont> cache = new ConcurrentDictionary<FontKey, WindowsGdiFont>();

			readonly FontKey key;
			readonly int lfHeight;
			readonly int emSquare;
			readonly float eM11, eM12, eM21, eM22;
			internal readonly bool PreserveAxes;
			readonly ConcurrentDictionary<ushort, CachedMetrics> metricsCache = new ConcurrentDictionary<ushort, CachedMetrics>();
			readonly ConcurrentDictionary<CoverageKey, CachedCoverage> coverageCache = new ConcurrentDictionary<CoverageKey, CachedCoverage>();
			readonly ConcurrentDictionary<ushort, SKPath> pathCache = new ConcurrentDictionary<ushort, SKPath>();
			readonly object gdiSync = new object();
			FontExtents? cachedFontExtents;

			internal static WindowsGdiFont GetOrCreate(SKFont font, Matrix matrix)
			{
				string family = font.Typeface.FamilyName ?? "Arial";
				int weight = font.Typeface.FontStyle.Weight >= (int)SKFontStyleWeight.Bold ? 700 : 400;
				byte italic = font.Typeface.FontStyle.Slant != SKFontStyleSlant.Upright ? (byte)1 : (byte)0;
				byte quality = font.Edging switch
				{
					SKFontEdging.Alias => 3,
					SKFontEdging.Antialias => 4,
					_ => 5
				};
				var fontKey = new FontKey(family, weight, italic, font.Size, font.ScaleX, matrix.Xx, matrix.Xy, matrix.Yx, matrix.Yy, quality);
				if (cache.TryGetValue(fontKey, out WindowsGdiFont existing))
					return existing;
				var created = TryCreate(fontKey, font.Typeface.UnitsPerEm > 0 ? font.Typeface.UnitsPerEm : 2048);
				if (created == null)
					return null;
				if (cache.Count > 256)
					cache.Clear();
				return cache.GetOrAdd(fontKey, created);
			}

			static WindowsGdiFont TryCreate(FontKey key, int emUnits)
			{
				IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
				if (dc == IntPtr.Zero) return null;
				IntPtr hfont = IntPtr.Zero, oldFont = IntPtr.Zero;
				try
				{
					hfont = CreateFontW(-24, 0, 0, 0, key.Weight, key.Italic, 0, 0, 1, 4, 0, key.DefaultQuality, 0, key.Family);
					if (hfont == IntPtr.Zero) return null;
					oldFont = SelectObject(dc, hfont);
					if (oldFont == IntPtr.Zero || oldFont == new IntPtr(-1)) return null;
					var sb = new StringBuilder(64);
					int len = GetTextFaceW(dc, sb.Capacity, sb);
					if (len <= 0 || !string.Equals(sb.ToString(), key.Family, StringComparison.OrdinalIgnoreCase))
						return null;
					return new WindowsGdiFont(key, emUnits);
				}
				finally
				{
					if (oldFont != IntPtr.Zero && oldFont != new IntPtr(-1)) SelectObject(dc, oldFont);
					if (hfont != IntPtr.Zero) DeleteObject(hfont);
					DeleteDC(dc);
				}
			}

			WindowsGdiFont(FontKey key, int emSquare)
			{
				this.key = key;
				this.emSquare = emSquare > 0 ? emSquare : 2048;
				PreserveAxes = key.Mxy == 0 && key.Myx == 0 && key.Mxx > 0 && key.Myy > 0;
				if (PreserveAxes)
				{
					double dx = key.Size * key.ScaleX * key.Mxx;
					double dy = key.Size * key.Myy;
					double yScale = Math.Max(1.0, Math.Floor(dy + 0.5));
					lfHeight = -checked((int)Math.Round(32.0 * yScale));
					eM11 = (float)((dx / dy) / 32.0);
					eM12 = 0f;
					eM21 = 0f;
					eM22 = 1f / 32f;
				}
				else
				{
					lfHeight = -1024;
					eM11 = (float)(key.Size * key.ScaleX * key.Mxx / 1024.0);
					eM12 = (float)(key.Size * key.ScaleX * key.Myx / 1024.0);
					eM21 = (float)(key.Size * key.Mxy / 1024.0);
					eM22 = (float)(key.Size * key.Myy / 1024.0);
				}
			}

			static byte ToGdiQuality(Antialias antialias, byte fallbackQuality) => antialias switch
			{
				Antialias.None => 3,
				Antialias.Gray or Antialias.Fast or Antialias.Good => 4,
				Antialias.Subpixel or Antialias.Best => 5,
				_ => fallbackQuality
			};

			CachedMetrics GetMetrics(ushort glyph)
			{
				return metricsCache.GetOrAdd(glyph, g =>
				{
					lock (gdiSync)
					{
						IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
						if (dc == IntPtr.Zero) return default;
						IntPtr scaledFont = IntPtr.Zero, unscaledFont = IntPtr.Zero, oldFont = IntPtr.Zero;
						try
						{
							SetGraphicsMode(dc, GmAdvanced);
							scaledFont = CreateFontW(lfHeight, 0, 0, 0, key.Weight, key.Italic, 0, 0, 1, 4, 0, key.DefaultQuality, 0, key.Family);
							unscaledFont = CreateFontW(-emSquare, 0, 0, 0, key.Weight, key.Italic, 0, 0, 1, 4, 0, key.DefaultQuality, 0, key.Family);
							if (scaledFont == IntPtr.Zero || unscaledFont == IntPtr.Zero) return default;

							var mat = IdentityMat2();
							oldFont = SelectObject(dc, unscaledFont);
							var identityXform = new XForm { eM11 = 1f, eM22 = 1f };
							SetWorldTransform(dc, ref identityXform);
							if (GetGlyphOutlineW(dc, g, GgoMetrics, out GdiGlyphMetrics gmU, 0, IntPtr.Zero, ref mat) == uint.MaxValue)
								return default;

							SelectObject(dc, scaledFont);
							var scaledXform = new XForm { eM11 = eM11, eM12 = eM12, eM21 = eM21, eM22 = eM22 };
							SetWorldTransform(dc, ref scaledXform);
							if (GetGlyphOutlineW(dc, g, GgoMetrics, out GdiGlyphMetrics gmS, 0, IntPtr.Zero, ref mat) == uint.MaxValue)
								return default;

							double factor = key.Size / emSquare;
							double unscaledAdv = gmU.gmCellIncX * factor * key.ScaleX;
							double scaledAdv = PreserveAxes ? gmS.gmCellIncX / key.Mxx : unscaledAdv;
							double xb = gmU.gmptGlyphOrigin.X * factor * key.ScaleX;
							double yb = -gmU.gmptGlyphOrigin.Y * factor;
							double wU = gmU.gmBlackBoxX * factor * key.ScaleX;
							double hU = gmU.gmBlackBoxY * factor;

							return new CachedMetrics(
								scaledAdv,
								unscaledAdv,
								xb,
								yb,
								wU,
								hU,
								gmS.gmptGlyphOrigin.X,
								gmS.gmptGlyphOrigin.Y,
								(int)gmS.gmBlackBoxX,
								(int)gmS.gmBlackBoxY);
						}
						finally
						{
							if (oldFont != IntPtr.Zero && oldFont != new IntPtr(-1)) SelectObject(dc, oldFont);
							if (scaledFont != IntPtr.Zero) DeleteObject(scaledFont);
							if (unscaledFont != IntPtr.Zero) DeleteObject(unscaledFont);
							DeleteDC(dc);
						}
					}
				});
			}

			internal double GetGlyphAdvance(ushort glyph, bool round)
			{
				CachedMetrics m = GetMetrics(glyph);
				return round && PreserveAxes ? m.ScaledAdvance : m.UnscaledAdvance;
			}

			internal bool TryGetFontExtents(out FontExtents extents)
			{
				if (!PreserveAxes)
				{
					extents = default;
					return false;
				}
				if (cachedFontExtents.HasValue)
				{
					extents = cachedFontExtents.Value;
					return true;
				}
				lock (gdiSync)
				{
					if (cachedFontExtents.HasValue)
					{
						extents = cachedFontExtents.Value;
						return true;
					}
					IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
					if (dc == IntPtr.Zero)
					{
						extents = default;
						return false;
					}
					IntPtr scaledFont = IntPtr.Zero, oldFont = IntPtr.Zero;
					try
					{
						SetGraphicsMode(dc, GmAdvanced);
						scaledFont = CreateFontW(lfHeight, 0, 0, 0, key.Weight, key.Italic, 0, 0, 1, 4, 0, key.DefaultQuality, 0, key.Family);
						if (scaledFont == IntPtr.Zero)
						{
							extents = default;
							return false;
						}
						oldFont = SelectObject(dc, scaledFont);
						var scaledXform = new XForm { eM11 = eM11, eM12 = eM12, eM21 = eM21, eM22 = eM22 };
						SetWorldTransform(dc, ref scaledXform);
						if (!GetTextMetricsW(dc, out GdiTextMetric tm) || tm.Height <= 0)
						{
							extents = default;
							return false;
						}
						double yScale = 32.0 * key.Myy;
						double xScale = 32.0 * key.Mxx;
						var computed = new FontExtents(
							tm.Ascent / yScale,
							tm.Descent / yScale,
							(tm.Height + tm.ExternalLeading) / yScale,
							tm.MaximumCharWidth / xScale,
							0);
						cachedFontExtents = computed;
						extents = computed;
						return true;
					}
					finally
					{
						if (oldFont != IntPtr.Zero && oldFont != new IntPtr(-1)) SelectObject(dc, oldFont);
						if (scaledFont != IntPtr.Zero) DeleteObject(scaledFont);
						DeleteDC(dc);
					}
				}
			}

			internal SKRect GetGlyphBounds(ushort glyph, bool round)
			{
				CachedMetrics m = GetMetrics(glyph);
				if (round && PreserveAxes)
				{
					bool pad = key.DefaultQuality != 3;
					double left = (m.ScaledOriginX - (pad ? 1 : 0)) / key.Mxx;
					double top = -m.ScaledOriginY / key.Myy;
					double width = (m.ScaledBlackBoxX + (pad ? 3 : 0)) / key.Mxx;
					double height = m.ScaledBlackBoxY / key.Myy;
					return new SKRect((float)left, (float)top, (float)(left + width), (float)(top + height));
				}
				return new SKRect((float)m.UnscaledLeft, (float)m.UnscaledTop, (float)(m.UnscaledLeft + m.UnscaledWidth), (float)(m.UnscaledTop + m.UnscaledHeight));
			}

			internal bool TryGetDeviceGlyphBounds(ushort glyph, Antialias antialias, bool round, out double left, out double top, out double right, out double bottom)
			{
				CachedMetrics m = GetMetrics(glyph);
				byte quality = ToGdiQuality(antialias, key.DefaultQuality);
				if (round && PreserveAxes)
				{
					bool pad = quality != 3;
					int l = m.ScaledOriginX - (pad ? 1 : 0);
					int t = -m.ScaledOriginY;
					int w = m.ScaledBlackBoxX + (pad ? 3 : 0);
					int h = m.ScaledBlackBoxY;
					if (w <= 0 || h <= 0)
					{
						left = top = right = bottom = 0;
						return false;
					}
					left = l;
					top = t;
					right = l + w;
					bottom = t + h;
					return true;
				}
				if (m.UnscaledWidth <= 0 || m.UnscaledHeight <= 0)
				{
					left = top = right = bottom = 0;
					return false;
				}
				TransformUnscaledBounds(m, out left, out top, out right, out bottom);
				return true;
			}

			void TransformUnscaledBounds(CachedMetrics m, out double left, out double top, out double right, out double bottom)
			{
				double x1 = m.UnscaledLeft, x2 = m.UnscaledLeft + m.UnscaledWidth;
				double y1 = m.UnscaledTop, y2 = m.UnscaledTop + m.UnscaledHeight;
				double cx1 = key.Mxx * x1 + key.Mxy * y1, cy1 = key.Myx * x1 + key.Myy * y1;
				double cx2 = key.Mxx * x2 + key.Mxy * y1, cy2 = key.Myx * x2 + key.Myy * y1;
				double cx3 = key.Mxx * x1 + key.Mxy * y2, cy3 = key.Myx * x1 + key.Myy * y2;
				double cx4 = key.Mxx * x2 + key.Mxy * y2, cy4 = key.Myx * x2 + key.Myy * y2;
				left = Math.Round(Math.Min(Math.Min(cx1, cx2), Math.Min(cx3, cx4)) * 256.0) / 256.0;
				right = Math.Round(Math.Max(Math.Max(cx1, cx2), Math.Max(cx3, cx4)) * 256.0) / 256.0;
				top = Math.Round(Math.Min(Math.Min(cy1, cy2), Math.Min(cy3, cy4)) * 256.0) / 256.0;
				bottom = Math.Round(Math.Max(Math.Max(cy1, cy2), Math.Max(cy3, cy4)) * 256.0) / 256.0;
			}

			internal SKBitmap GetGlyphCoverage(ushort glyph, Antialias antialias, bool roundMetrics, out int left, out int top)
			{
				byte quality = ToGdiQuality(antialias, key.DefaultQuality);
				var covKey = new CoverageKey(glyph, quality, roundMetrics && PreserveAxes);
				CachedCoverage cached = coverageCache.GetOrAdd(covKey, ck => RenderCoverage(ck.Glyph, ck.Quality, ck.RoundMetrics));
				left = cached.Left;
				top = cached.Top;
				if (cached.Rgba == null || cached.Width <= 0 || cached.Height <= 0)
					return null;
				var bitmap = new SKBitmap(cached.Width, cached.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
				IntPtr dst = bitmap.GetPixels();
				Marshal.Copy(cached.Rgba, 0, dst, cached.Rgba.Length);
				return bitmap;
			}

			CachedCoverage RenderCoverage(ushort glyph, byte quality, bool roundMetrics)
			{
				CachedMetrics m = GetMetrics(glyph);
				int dibLeft, dibTop, gw, gh;
				if (roundMetrics && PreserveAxes)
				{
					bool pad = quality != 3;
					dibLeft = m.ScaledOriginX - (pad ? 1 : 0);
					dibTop = -m.ScaledOriginY;
					gw = m.ScaledBlackBoxX + (pad ? 3 : 0);
					gh = m.ScaledBlackBoxY;
				}
				else if (m.UnscaledWidth > 0 && m.UnscaledHeight > 0)
				{
					TransformUnscaledBounds(m, out double left, out double top, out double right, out double bottom);
					dibLeft = (int)Math.Floor(left);
					dibTop = (int)Math.Floor(top);
					gw = (int)Math.Ceiling(right) - dibLeft;
					gh = (int)Math.Ceiling(bottom) - dibTop;
				}
				else
				{
					return new CachedCoverage(null, 0, 0, 0, 0);
				}
				if (gw <= 0 || gh <= 0 || gw > 2048 || gh > 2048)
					return new CachedCoverage(null, 0, 0, dibLeft, -dibTop);

				lock (gdiSync)
				{
					IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
					if (dc == IntPtr.Zero) return new CachedCoverage(null, 0, 0, dibLeft, -dibTop);
					IntPtr hfont = IntPtr.Zero, oldFont = IntPtr.Zero, hbmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
					try
					{
						SetGraphicsMode(dc, GmAdvanced);
						hfont = CreateFontW(lfHeight, 0, 0, 0, key.Weight, key.Italic, 0, 0, 1, 4, 0, quality, 0, key.Family);
						if (hfont == IntPtr.Zero) return new CachedCoverage(null, 0, 0, dibLeft, -dibTop);
						oldFont = SelectObject(dc, hfont);
						if (oldFont == IntPtr.Zero || oldFont == new IntPtr(-1)) return new CachedCoverage(null, 0, 0, dibLeft, -dibTop);

						var bmi = new BitmapInfoHeader
						{
							Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
							Width = gw,
							Height = -gh,
							Planes = 1,
							BitCount = 32,
							Compression = 0,
							SizeImage = checked((uint)(gw * gh * 4))
						};
						hbmp = CreateDIBSection(dc, ref bmi, 0, out IntPtr pixels, IntPtr.Zero, 0);
						if (hbmp == IntPtr.Zero || pixels == IntPtr.Zero) return new CachedCoverage(null, 0, 0, dibLeft, -dibTop);
						oldBmp = SelectObject(dc, hbmp);
						if (oldBmp == IntPtr.Zero || oldBmp == new IntPtr(-1)) return new CachedCoverage(null, 0, 0, dibLeft, -dibTop);

						var bgra = new byte[checked(gw * gh * 4)];
						Array.Fill(bgra, (byte)255);
						Marshal.Copy(bgra, 0, pixels, bgra.Length);

						var xform = new XForm
						{
							eM11 = eM11,
							eM12 = eM12,
							eM21 = eM21,
							eM22 = eM22,
							eDx = -dibLeft,
							eDy = -dibTop
						};
						SetWorldTransform(dc, ref xform);
						SetTextAlign(dc, TaLeftBaseline);
						SetBkMode(dc, BkModeTransparent);
						SetTextColor(dc, 0);

						ushort g = glyph;
						if (!ExtTextOutW(dc, 0, 0, EtoGlyphIndex, IntPtr.Zero, ref g, 1, IntPtr.Zero))
							return new CachedCoverage(null, 0, 0, dibLeft, -dibTop);
						GdiFlush();

						Marshal.Copy(pixels, bgra, 0, bgra.Length);
						var rgba = new byte[bgra.Length];
						bool hasInk = false;
						for (int i = 0; i < bgra.Length; i += 4)
						{
							byte b = (byte)(255 - bgra[i]);
							byte green = (byte)(255 - bgra[i + 1]);
							byte r = (byte)(255 - bgra[i + 2]);
							hasInk |= (r | green | b) != 0;
							rgba[i] = r;
							rgba[i + 1] = green;
							rgba[i + 2] = b;
							rgba[i + 3] = 255;
						}
						return hasInk ? new CachedCoverage(rgba, gw, gh, dibLeft, -dibTop) : new CachedCoverage(null, 0, 0, dibLeft, -dibTop);
					}
					finally
					{
						if (oldFont != IntPtr.Zero && oldFont != new IntPtr(-1)) SelectObject(dc, oldFont);
						if (hfont != IntPtr.Zero) DeleteObject(hfont);
						if (oldBmp != IntPtr.Zero && oldBmp != new IntPtr(-1)) SelectObject(dc, oldBmp);
						if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
						DeleteDC(dc);
					}
				}
			}

			internal SKPath GetGlyphPath(ushort glyph)
			{
				if (!PreserveAxes) return null;
				SKPath cached = pathCache.GetOrAdd(glyph, BuildGlyphPath);
				return cached != null ? new SKPath(cached) : null;
			}

			SKPath BuildGlyphPath(ushort glyph)
			{
				lock (gdiSync)
				{
					IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
					if (dc == IntPtr.Zero) return null;
					IntPtr hfont = IntPtr.Zero, oldFont = IntPtr.Zero;
					try
					{
						SetGraphicsMode(dc, GmAdvanced);
						hfont = CreateFontW(lfHeight, 0, 0, 0, key.Weight, key.Italic, 0, 0, 1, 4, 0, key.DefaultQuality, 0, key.Family);
						if (hfont == IntPtr.Zero) return null;
						oldFont = SelectObject(dc, hfont);
						if (oldFont == IntPtr.Zero || oldFont == new IntPtr(-1)) return null;

						var xform = new XForm { eM11 = eM11, eM12 = eM12, eM21 = eM21, eM22 = eM22 };
						SetWorldTransform(dc, ref xform);
						var mat = IdentityMat2();
						uint size = GetGlyphOutlineW(dc, glyph, GgoNative, out GdiGlyphMetrics gm, 0, IntPtr.Zero, ref mat);
						if (size == uint.MaxValue) return null;
						var path = new SKPath();
						if (size == 0) return path;

						var buffer = new byte[size];
						GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
						try
						{
							if (GetGlyphOutlineW(dc, glyph, GgoNative, out gm, size, pin.AddrOfPinnedObject(), ref mat) == uint.MaxValue)
							{
								path.Dispose();
								return null;
							}
						}
						finally
						{
							pin.Free();
						}

						SKPoint ToUser(int fx, int fy) => new SKPoint((float)((fx / 65536.0) / key.Mxx), (float)(-(fy / 65536.0) / key.Myy));

						int offset = 0;
						while (offset + 16 <= buffer.Length)
						{
							int cb = BitConverter.ToInt32(buffer, offset);
							if (cb < 16 || offset + cb > buffer.Length) break;
							int startX = BitConverter.ToInt32(buffer, offset + 8);
							int startY = BitConverter.ToInt32(buffer, offset + 12);
							path.MoveTo(ToUser(startX, startY));
							int curveOffset = offset + 16;
							int contourEnd = offset + cb;
							while (curveOffset + 4 <= contourEnd)
							{
								ushort wType = BitConverter.ToUInt16(buffer, curveOffset);
								ushort cpfx = BitConverter.ToUInt16(buffer, curveOffset + 2);
								curveOffset += 4;
								if (curveOffset + cpfx * 8 > contourEnd) break;
								var pts = new SKPoint[cpfx];
								for (int i = 0; i < cpfx; i++)
								{
									int px = BitConverter.ToInt32(buffer, curveOffset + i * 8);
									int py = BitConverter.ToInt32(buffer, curveOffset + i * 8 + 4);
									pts[i] = ToUser(px, py);
								}
								curveOffset += cpfx * 8;
								if (wType == 1) // TT_PRIM_LINE
								{
									for (int i = 0; i < cpfx; i++)
										path.LineTo(pts[i]);
								}
								else if (wType == 2) // TT_PRIM_QSPLINE
								{
									for (int i = 0; i < cpfx - 1; i++)
									{
										SKPoint control = pts[i];
										SKPoint end = i == cpfx - 2
											? pts[i + 1]
											: new SKPoint((control.X + pts[i + 1].X) * 0.5f, (control.Y + pts[i + 1].Y) * 0.5f);
										path.QuadTo(control, end);
									}
								}
								else if (wType == 3) // TT_PRIM_CSPLINE
								{
									for (int i = 0; i + 2 < cpfx; i += 3)
										path.CubicTo(pts[i], pts[i + 1], pts[i + 2]);
								}
							}
							path.Close();
							offset += cb;
						}
						return path;
					}
					finally
					{
						if (oldFont != IntPtr.Zero && oldFont != new IntPtr(-1)) SelectObject(dc, oldFont);
						if (hfont != IntPtr.Zero) DeleteObject(hfont);
						DeleteDC(dc);
					}
				}
			}

			static Mat2 IdentityMat2() => new Mat2 { eM11 = 0x10000, eM22 = 0x10000 };

			[StructLayout(LayoutKind.Sequential)]
			struct XForm
			{
				public float eM11, eM12, eM21, eM22, eDx, eDy;
			}

			[StructLayout(LayoutKind.Sequential)]
			struct GdiPoint
			{
				public int X, Y;
			}

			[StructLayout(LayoutKind.Sequential)]
			struct GdiGlyphMetrics
			{
				public uint gmBlackBoxX, gmBlackBoxY;
				public GdiPoint gmptGlyphOrigin;
				public short gmCellIncX, gmCellIncY;
			}

			[StructLayout(LayoutKind.Sequential)]
			struct Mat2
			{
				public uint eM11, eM12, eM21, eM22;
			}

			[StructLayout(LayoutKind.Sequential)]
			struct BitmapInfoHeader
			{
				public uint Size;
				public int Width, Height;
				public ushort Planes, BitCount;
				public uint Compression, SizeImage;
				public int XPelsPerMeter, YPelsPerMeter;
				public uint ClrUsed, ClrImportant;
			}

			[StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
			struct GdiTextMetric
			{
				public int Height, Ascent, Descent, InternalLeading, ExternalLeading;
				public int AverageCharWidth, MaximumCharWidth, Weight, Overhang;
				public int DigitizedAspectX, DigitizedAspectY;
				public char FirstChar, LastChar, DefaultChar, BreakChar;
				public byte Italic, Underlined, StruckOut, PitchAndFamily, CharacterSet;
			}

			[DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateCompatibleDC(IntPtr dc);
			[DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
			[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
			[DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
			[DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(IntPtr gdiObject);
			[DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(IntPtr dc);
			[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern int GetTextFaceW(IntPtr dc, int count, StringBuilder faceName);
			[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetTextMetricsW(IntPtr dc, out GdiTextMetric metrics);
			[DllImport("gdi32.dll", SetLastError = true)] static extern int SetGraphicsMode(IntPtr dc, int mode);
			[DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWorldTransform(IntPtr dc, ref XForm xform);
			[DllImport("gdi32.dll", SetLastError = true)] static extern uint GetGlyphOutlineW(IntPtr dc, uint uChar, uint uFormat, out GdiGlyphMetrics lpgm, uint cbBuffer, IntPtr pvBuffer, ref Mat2 lpmat2);
			[DllImport("gdi32.dll", SetLastError = true)] static extern uint SetTextColor(IntPtr dc, uint color);
			[DllImport("gdi32.dll", SetLastError = true)] static extern int SetBkMode(IntPtr dc, int mode);
			[DllImport("gdi32.dll", SetLastError = true)] static extern uint SetTextAlign(IntPtr dc, uint align);
			[DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ExtTextOutW(IntPtr dc, int x, int y, uint options, IntPtr rect, ref ushort glyph, uint count, IntPtr dx);
			[DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GdiFlush();
		}

		sealed class OutlineSink(double scaleX, double scaleY)
		{
			internal readonly SKPath Path = new SKPath();
			internal Exception Failure;
			SKPoint Point(Vector point) => new SKPoint((float)((double)point.X / (64 * scaleX)), (float)(-(double)point.Y / (64 * scaleY)));
			// Keep managed exceptions on the managed side of the reverse P/Invoke boundary.
			internal int Move(ref Vector point, IntPtr _) {
				try { if (Path.PointCount > 0) Path.Close(); Path.MoveTo(Point(point)); return 0; }
				catch (Exception exception) { Failure = exception; return 1; }
			}
			internal int Line(ref Vector point, IntPtr _) {
				try { Path.LineTo(Point(point)); return 0; }
				catch (Exception exception) { Failure = exception; return 1; }
			}
			internal int Conic(ref Vector control, ref Vector point, IntPtr _) {
				try { Path.QuadTo(Point(control), Point(point)); return 0; }
				catch (Exception exception) { Failure = exception; return 1; }
			}
			internal int Cubic(ref Vector first, ref Vector second, ref Vector point, IntPtr _) {
				try { Path.CubicTo(Point(first), Point(second), Point(point)); return 0; }
				catch (Exception exception) { Failure = exception; return 1; }
			}
		}

		static void Check(int error, string operation)
		{
			if (error != 0) throw new InvalidOperationException("FreeType could not " + operation + " for a Skia text path (error " + error + ").");
		}

		public void Dispose()
		{
			if (face != IntPtr.Zero) { FT_Done_Face(face); face = IntPtr.Zero; }
			if (library != IntPtr.Zero) { FT_Done_FreeType(library); library = IntPtr.Zero; }
			if (pinnedBytes.IsAllocated) pinnedBytes.Free();
			stream?.Dispose();
		}

		// FreeType's public Unix ABI uses C long (pointer-sized), not C# long on every architecture.
		[StructLayout(LayoutKind.Sequential)] struct Vector { public nint X, Y; }
		[StructLayout(LayoutKind.Sequential)] struct Generic { public IntPtr Data, Finalizer; }
		[StructLayout(LayoutKind.Sequential)] struct Box { public nint XMin, YMin, XMax, YMax; }
		[StructLayout(LayoutKind.Sequential)] struct GlyphMetrics {
			public nint Width, Height, BearingX, BearingY, Advance, VerticalBearingX, VerticalBearingY, VerticalAdvance;
		}
		[StructLayout(LayoutKind.Sequential)] struct SlotPrefix {
			public IntPtr Library, Face, Next;
			public uint GlyphIndex;
			public Generic Generic;
			public GlyphMetrics Metrics;
		}

		[StructLayout(LayoutKind.Sequential)] struct Bitmap {
			public uint Rows, Width;
			public int Pitch;
			public IntPtr Buffer;
			public ushort GrayCount;
			public byte PixelMode, PaletteMode;
			public IntPtr Palette;
		}
		[StructLayout(LayoutKind.Sequential)] struct RenderedSlot {
			public SlotPrefix Prefix;
			public nint LinearHorizontalAdvance, LinearVerticalAdvance;
			public Vector Advance;
			public uint Format;
			public Bitmap Bitmap;
			public int Left, Top;
		}

		[StructLayout(LayoutKind.Sequential)] struct FacePrefix {
			public nint NumFaces, FaceIndex, FaceFlags, StyleFlags, NumGlyphs;
			public IntPtr FamilyName, StyleName;
			public int NumFixedSizes; public IntPtr AvailableSizes;
			public int NumCharmaps; public IntPtr Charmaps;
			public Generic Generic; public Box Box;
			public ushort UnitsPerEm;
			public short Ascender, Descender, Height, MaxAdvanceWidth, MaxAdvanceHeight, UnderlinePosition, UnderlineThickness;
			public IntPtr Glyph;
		}
		[StructLayout(LayoutKind.Sequential)] struct Outline {
			public short ContourCount, PointCount;
			public IntPtr Points, Tags, Contours;
			public int Flags;
		}
		[StructLayout(LayoutKind.Sequential)] struct OutlineGlyph {
			public IntPtr Library, Class;
			public uint Format;
			public Vector Advance;
			public Outline Outline;
		}
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int MoveCallback(ref Vector point, IntPtr user);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ConicCallback(ref Vector control, ref Vector point, IntPtr user);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CubicCallback(ref Vector first, ref Vector second, ref Vector point, IntPtr user);
		[StructLayout(LayoutKind.Sequential)] struct OutlineFunctions {
			public MoveCallback Move, Line; public ConicCallback Conic; public CubicCallback Cubic;
			public int Shift; public nint Delta;
		}
		[DllImport(FreeType, CallingConvention = CallingConvention.Cdecl)] static extern void FT_Set_Transform(IntPtr face, IntPtr matrix, ref Vector delta);
		[DllImport(FreeType, CallingConvention = CallingConvention.Cdecl)] static extern int FT_Render_Glyph(IntPtr slot, int mode);
		[DllImport(FreeType, CallingConvention = CallingConvention.Cdecl)] static extern int FT_Library_SetLcdFilter(IntPtr library, int filter);
		[DllImport(FreeType, CallingConvention = CallingConvention.Cdecl)] static extern int FT_Init_FreeType(out IntPtr library);
		[DllImport(FreeType, CallingConvention = CallingConvention.Cdecl)] static extern int FT_Done_FreeType(IntPtr library);
		[DllImport(FreeType, CallingConvention = CallingConvention.Cdecl)] static extern int FT_New_Memory_Face(IntPtr library, IntPtr bytes, nint length, nint index, out IntPtr face);
		[DllImport(FreeType, CallingConvention = CallingConvention.Cdecl)] static extern int FT_Done_Face(IntPtr face);
		[DllImport(FreeType, CallingConvention = CallingConvention.Cdecl)] static extern int FT_Set_Char_Size(IntPtr face, nint width, nint height, uint horizontalResolution, uint verticalResolution);
		[DllImport(FreeType, CallingConvention = CallingConvention.Cdecl)] static extern int FT_Load_Glyph(IntPtr face, uint glyph, int flags);
		[DllImport(FreeType, CallingConvention = CallingConvention.Cdecl)] static extern int FT_Get_Glyph(IntPtr slot, out IntPtr glyph);
		[DllImport(FreeType, CallingConvention = CallingConvention.Cdecl)] static extern void FT_Done_Glyph(IntPtr glyph);
		[DllImport(FreeType, CallingConvention = CallingConvention.Cdecl)] static extern int FT_Outline_Decompose(ref Outline outline, ref OutlineFunctions functions, IntPtr user);
	}
}
