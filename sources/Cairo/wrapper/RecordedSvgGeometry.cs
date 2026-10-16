using System;
using System.Runtime.InteropServices;
using NanoSvg;
using SkiaSharp;

namespace Cairo
{
	// NanoSVG remains the parser and geometry authority. Skia rasterizes the parsed solid
	// fills and strokes, and the result is post-processed with NanoSVG's unpremultiply and
	// defringe steps and SvgLoader.rasterizeSvg's tint/channel conversion, so callers get
	// the same straight-alpha RGBA bytes. Gradients and dashed strokes stay native.
	internal static unsafe class RecordedSvgGeometry
	{
		[StructLayout (LayoutKind.Sequential)] struct Paint { internal byte Type; internal IntPtr Value; }
		[StructLayout (LayoutKind.Sequential)] struct Shape
		{
			internal fixed byte Id[64]; internal Paint Fill, Stroke;
			internal float Opacity, StrokeWidth, DashOffset; internal fixed float Dash[8];
			internal byte DashCount, Join, Cap; internal float Miter;
			internal byte FillRule, Flags; internal fixed float Bounds[4]; internal IntPtr Paths, Next;
		}
		[StructLayout (LayoutKind.Sequential)] struct Path
		{
			internal IntPtr Points; internal int Count; internal byte Closed;
			internal fixed float Bounds[4]; internal IntPtr Next;
		}

		const byte PaintNone = 0, PaintColor = 1;
		const int MaxShapes = 100000, MaxPaths = 100000, MaxPoints = 1000000;
		const long MaxPixels = 16L * 1024 * 1024;

		/// <summary>
		/// Equivalent of <c>SvgLoader.rasterizeSvg</c>: straight-alpha RGBA bytes, tinted or
		/// with red and blue swapped. Throws <see cref="NotSupportedException"/> for content
		/// that must stay on the native rasterizer.
		/// </summary>
		internal static byte[] Rasterize (string text, int textureWidth, int textureHeight, int width, int height, int? tint)
		{
			if (text == null) throw new NotSupportedException ("Recorded SVG has no text.");
			if (textureWidth <= 0 || textureHeight <= 0 || (long)textureWidth * textureHeight > MaxPixels)
				throw new NotSupportedException ("Recorded SVG has no supported texture size.");
			IntPtr image = SvgNativeMethods.nsvgParse (text, "px", 96);
			if (image == IntPtr.Zero) throw new NotSupportedException ("NanoSVG could not parse the recorded SVG.");
			try {
				IntPtr sizePointer = SvgNativeMethods.nsvgImageGetSize (image);
				if (sizePointer == IntPtr.Zero) throw new NotSupportedException ("NanoSVG reported no SVG size.");
				var size = Marshal.PtrToStructure<NsvgSize> (sizePointer);
				// Same float arithmetic as SvgLoader.rasterizeSvg.
				float scale;
				int offsetX = 0, offsetY = 0;
				if (width == 0 && height == 0) scale = 1f;
				else if (width == 0) scale = (float)height / size.height;
				else if (height == 0) scale = (float)width / size.width;
				else {
					float scaleX = (float)width / size.width;
					float scaleY = (float)height / size.height;
					scale = scaleX < scaleY ? scaleX : scaleY;
					offsetX = (int)((float)textureWidth - size.width * scale) / 2;
					offsetY = (int)((float)textureHeight - size.height * scale) / 2;
				}
				if (!float.IsFinite (scale) || scale <= 0) throw new NotSupportedException ("Recorded SVG has an invalid scale.");

				var info = new SKImageInfo (textureWidth, textureHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
				byte[] pixels = new byte[textureWidth * textureHeight * 4];
				using (SKSurface surface = SKSurface.Create (info)) {
					if (surface == null) throw new NotSupportedException ("Skia could not create the SVG raster.");
					SKCanvas canvas = surface.Canvas;
					canvas.Clear (SKColors.Transparent);
					canvas.Translate (offsetX, offsetY);
					DrawShapes (canvas, Marshal.ReadIntPtr (image, 24), scale);
					fixed (byte* data = pixels)
						if (!surface.ReadPixels (info, (IntPtr)data, textureWidth * 4, 0, 0)) throw new NotSupportedException ("Skia could not read the SVG raster.");
				}
				UnpremultiplyAndDefringe (pixels, textureWidth, textureHeight, textureWidth * 4);
				ApplyLoaderConversion (pixels, tint);
				return pixels;
			} finally { SvgNativeMethods.nsvgDelete (image); }
		}

		static void DrawShapes (SKCanvas canvas, IntPtr pointer, float scale)
		{
			int shapeCount = 0;
			while (pointer != IntPtr.Zero) {
				if (++shapeCount > MaxShapes) throw new NotSupportedException ("Recorded SVG has too many shapes.");
				Shape shape = Marshal.PtrToStructure<Shape> (pointer); pointer = shape.Next;
				if ((shape.Flags & 1) == 0) continue;
				if (shape.Fill.Type != PaintNone) {
					if (shape.Fill.Type != PaintColor) throw new NotSupportedException ("Recorded SVG gradient requires native rasterization.");
					using (SKPath path = ReadPath (shape.Paths, scale, shape.FillRule == 1 ? SKPathFillType.EvenOdd : SKPathFillType.Winding))
					using (SKPaint paint = ReadPaint (shape.Fill, shape.Opacity)) canvas.DrawPath (path, paint);
				}
				// NanoSVG skips strokes whose scaled width is not above 0.01.
				if (shape.Stroke.Type != PaintNone && shape.StrokeWidth * scale > 0.01f) {
					if (shape.Stroke.Type != PaintColor) throw new NotSupportedException ("Recorded SVG stroke gradient requires native rasterization.");
					if (shape.DashCount != 0) throw new NotSupportedException ("Recorded SVG dashed stroke requires native rasterization.");
					using (SKPath path = ReadPath (shape.Paths, scale, SKPathFillType.Winding))
					using (SKPaint paint = ReadPaint (shape.Stroke, shape.Opacity))
					using (var stroke = new SKPaint {
						Style = SKPaintStyle.Stroke, StrokeWidth = shape.StrokeWidth * scale,
						StrokeCap = (SKStrokeCap)Math.Min ((int)shape.Cap, 2), StrokeJoin = (SKStrokeJoin)Math.Min ((int)shape.Join, 2),
						StrokeMiter = shape.Miter, IsAntialias = true
					})
					using (var outline = new SKPath ()) {
						// Fill the stroke union once, as NanoSVG rasterizes its stroke polygon.
						if (!stroke.GetFillPath (path, outline, 16f)) continue;
						outline.FillType = SKPathFillType.Winding;
						canvas.DrawPath (outline, paint);
					}
				}
			}
		}

		static SKPath ReadPath (IntPtr pointer, float scale, SKPathFillType fillType)
		{
			var result = new SKPath { FillType = fillType };
			try {
				int pathCount = 0;
				while (pointer != IntPtr.Zero) {
					if (++pathCount > MaxPaths) throw new NotSupportedException ("Recorded SVG has too many paths.");
					Path path = Marshal.PtrToStructure<Path> (pointer); pointer = path.Next;
					if (path.Count <= 0 || path.Count > MaxPoints || (path.Count - 1) % 3 != 0 || path.Points == IntPtr.Zero)
						throw new NotSupportedException ("Recorded SVG has an invalid cubic path.");
					float* points = (float*)path.Points;
					result.MoveTo (points[0] * scale, points[1] * scale);
					for (int i = 1; i < path.Count; i += 3)
						result.CubicTo (points[i * 2] * scale, points[i * 2 + 1] * scale, points[i * 2 + 2] * scale, points[i * 2 + 3] * scale, points[i * 2 + 4] * scale, points[i * 2 + 5] * scale);
					if (path.Closed != 0) result.Close ();
				}
				return result;
			} catch { result.Dispose (); throw; }
		}

		static SKPaint ReadPaint (Paint source, float opacity)
		{
			uint packed = (uint)source.Value.ToInt64 ();
			byte red = (byte)packed, green = (byte)(packed >> 8), blue = (byte)(packed >> 16), alpha = (byte)(packed >> 24);
			// nsvg__applyOpacity, then NanoSVG's div255 premultiply expressed through Skia's
			// float colour premultiply.
			int sourceAlpha = alpha * (int)(Math.Clamp (opacity, 0, 1) * 256) >> 8;
			var paint = new SKPaint { IsAntialias = true };
			if (sourceAlpha == 0) paint.Color = SKColors.Transparent;
			else {
				paint.ColorF = new SKColorF (
					Premultiply (red, sourceAlpha) / (float)sourceAlpha,
					Premultiply (green, sourceAlpha) / (float)sourceAlpha,
					Premultiply (blue, sourceAlpha) / (float)sourceAlpha,
					sourceAlpha / 255f);
			}
			return paint;
		}
		static int Premultiply (int channel, int alpha) => ((channel * alpha + 1) * 257) >> 16;

		/// <summary>nsvg__unpremultiplyAlpha: integer unpremultiply, then fill the colour of fully
		/// transparent pixels from opaque neighbours.</summary>
		internal static void UnpremultiplyAndDefringe (byte[] image, int w, int h, int stride)
		{
			for (int y = 0; y < h; y++) {
				int row = y * stride;
				for (int x = 0; x < w; x++) {
					int i = row + x * 4;
					int r = image[i], g = image[i + 1], b = image[i + 2], a = image[i + 3];
					if (a != 0) {
						image[i] = (byte)(r * 255 / a);
						image[i + 1] = (byte)(g * 255 / a);
						image[i + 2] = (byte)(b * 255 / a);
					}
				}
			}
			for (int y = 0; y < h; y++) {
				int row = y * stride;
				for (int x = 0; x < w; x++) {
					int i = row + x * 4;
					int r = 0, g = 0, b = 0, a = image[i + 3], n = 0;
					if (a != 0) continue;
					if (x - 1 > 0 && image[i - 1] != 0) { r += image[i - 4]; g += image[i - 3]; b += image[i - 2]; n++; }
					if (x + 1 < w && image[i + 7] != 0) { r += image[i + 4]; g += image[i + 5]; b += image[i + 6]; n++; }
					if (y - 1 > 0 && image[i - stride + 3] != 0) { r += image[i - stride]; g += image[i - stride + 1]; b += image[i - stride + 2]; n++; }
					if (y + 1 < h && image[i + stride + 3] != 0) { r += image[i + stride]; g += image[i + stride + 1]; b += image[i + stride + 2]; n++; }
					if (n > 0) {
						image[i] = (byte)(r / n);
						image[i + 1] = (byte)(g / n);
						image[i + 2] = (byte)(b / n);
					}
				}
			}
		}

		/// <summary>The pixel loop of SvgLoader.DrawSvg over a rasterizeSvg buffer, including its
		/// row-major index arithmetic. <paramref name="colorOver"/> is ColorUtil.ColorOver.</summary>
		internal static RecordedPixelOperation DrawSvgOperation (byte[] rgba, int posx, int posy, int width, int height, Func<int, int, int> colorOver)
		{
			if (rgba == null) throw new ArgumentNullException (nameof (rgba));
			if (colorOver == null) throw new ArgumentNullException (nameof (colorOver));
			return (data, surfaceWidth, surfaceHeight, stride) => {
				int num = surfaceWidth * surfaceHeight;
				int* destination = (int*)data.ToPointer ();
				fixed (byte* source = rgba) {
					int* pixels = (int*)source;
					for (int i = 0; i < width; i++) {
						for (int j = 0; j < height; j++) {
							int rgb = pixels[j * width + i];
							int index = (posy + j) * surfaceWidth + posx + i;
							if (index >= 0 && index < num) destination[index] = colorOver (rgb, destination[index]);
						}
					}
				}
			};
		}

		/// <summary>Writes a rasterizeSvg buffer (uploaded by vanilla as GL_RGBA) into a surface
		/// whose logical R, G, B, A channels become the candidate texture's channels.</summary>
		internal static RecordedPixelOperation TextureOperation (byte[] rgba)
		{
			if (rgba == null) throw new ArgumentNullException (nameof (rgba));
			return (data, width, height, stride) => {
				uint* destination = (uint*)data.ToPointer ();
				int count = Math.Min (width * height, rgba.Length / 4);
				for (int i = 0; i < count; i++)
					destination[i] = (uint)rgba[i * 4 + 3] << 24 | (uint)rgba[i * 4] << 16 | (uint)rgba[i * 4 + 1] << 8 | rgba[i * 4 + 2];
			};
		}

		/// <summary>The tint (or red/blue swap) loop of SvgLoader.rasterizeSvg.</summary>
		internal static void ApplyLoaderConversion (byte[] pixels, int? tint)
		{
			if (tint.HasValue) {
				int color = tint.Value;
				byte[] rgba = { (byte)(color >> 16), (byte)(color >> 8), (byte)color, (byte)(color >> 24) };
				for (int i = 0; i < pixels.Length - 1; i += 4) {
					float coverage = (float)(int)pixels[i + 3] / 255f;
					pixels[i] = (byte)(coverage * (float)(int)rgba[0]);
					pixels[i + 1] = (byte)(coverage * (float)(int)rgba[1]);
					pixels[i + 2] = (byte)(coverage * (float)(int)rgba[2]);
					pixels[i + 3] = (byte)(coverage * (float)(int)rgba[3]);
				}
			} else {
				for (int i = 0; i < pixels.Length - 1; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
			}
		}
	}
}
