using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Retains FreeType's hinted geometry for Linux text paths; Skia still rasterizes the paths.</summary>
	internal sealed class SkiaHintedFontOutlines : IDisposable
	{
		const string FreeType = "libfreetype.so.6";
		readonly SKStreamAsset stream;
		readonly double scaleX, scaleY;
		readonly int loadFlags;
		GCHandle pinnedBytes;
		IntPtr library, face;

		internal static SkiaHintedFontOutlines Create(SKFont font, Matrix matrix, int fontLoadOptions = 0)
		{
			// The device grid determines hinting. Rotated or sheared outlines keep
			// Skia's scalable geometry rather than hinting against the wrong axes.
			if (!OperatingSystem.IsLinux() || (font.Hinting == SKFontHinting.None && fontLoadOptions == 0) ||
				matrix.Xy != 0 || matrix.Yx != 0 || matrix.Xx <= 0 || matrix.Yy <= 0 || font.Size <= 0 ||
				!double.IsFinite(matrix.Xx * font.Size) || !double.IsFinite(matrix.Yy * font.Size))
				return null;
			try { return new SkiaHintedFontOutlines(font, matrix.Xx, matrix.Yy, fontLoadOptions); }
			// Skia's scalable outlines remain available on systems without the
			// optional platform hinter. Neither route requires native Cairo.
			catch (DllNotFoundException) { return null; }
			catch (EntryPointNotFoundException) { return null; }
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

		internal SKBitmap GetGlyphCoverage(ushort glyph, Antialias antialias, SubpixelOrder order, out int left, out int top, double fractionX = 0, double fractionY = 0)
		{
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

		internal SKRect GetGlyphBounds(ushort glyph, bool round)
		{
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
			Check(FT_Load_Glyph(face, glyph, loadFlags), "load glyph advance");
			var slot = Marshal.PtrToStructure<RenderedSlot>(Marshal.PtrToStructure<FacePrefix>(face).Glyph);
			// Cairo uses the linear advance when metric hinting is disabled, even
			// when the outline itself was loaded with grid fitting enabled.
			return round ? Math.Floor((double)slot.Advance.X / 64 + 0.5) / scaleX
				: (double)slot.LinearHorizontalAdvance / (65536 * scaleX);
		}

		internal SKPath GetGlyphPath(ushort glyph)
		{
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
