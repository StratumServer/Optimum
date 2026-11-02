using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NanoSvg;
using SkiaSharp;

namespace Cairo
{
	// NanoSVG remains the parser and geometry authority. Solid SVG paths are
	// replayed as Skia paths; opaque texture uploads are accepted only after a
	// CPU alpha-coverage check confirms that no transparent texels remain.
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

		internal static RecordedPreparationCache.Picture Acquire(string text, int textureWidth, int textureHeight, int width, int height)
		{
			RecordedPreparationCache.Key key = RecordedPreparationCache.Enabled && (text?.Length ?? 0) <= RecordedPreparationCache.MaxKeyBytes / 4
				? new RecordedPreparationCache.Key(5, new long[] { textureWidth, textureHeight, width, height }, text: text) : null;
			var cached = RecordedPreparationCache.Acquire<RecordedPreparationCache.Picture>(key);
			if (cached != null) return cached;
			var result = new RecordedPreparationCache.Picture(Parse(text, textureWidth, textureHeight, width, height));
			try { RecordedPreparationCache.Store(key, result); return result; }
			catch { result.Dispose(); throw; }
		}

		internal static SKPicture Parse (string text, int textureWidth, int textureHeight, int width, int height, int? tint = null,
			bool textureColorOrder = false, bool requireOpaque = false)
		{
			if (textureWidth <= 0 || textureHeight <= 0) throw new NotSupportedException ("Recorded SVG has no positive texture size.");
			IntPtr image = SvgNativeMethods.nsvgParse (text, "px", 96);
			if (image == IntPtr.Zero) throw new NotSupportedException ("NanoSVG could not parse the recorded SVG.");
			try {
				var size = Marshal.PtrToStructure<NsvgSize> (SvgNativeMethods.nsvgImageGetSize (image));
				if (!float.IsFinite (size.width) || !float.IsFinite (size.height) || size.width <= 0 || size.height <= 0)
					throw new NotSupportedException ("Recorded SVG has no positive viewport.");
				float scale = width == 0 && height == 0 ? 1 : width == 0 ? height / size.height : height == 0 ? width / size.width : Math.Min (width / size.width, height / size.height);
				if (!float.IsFinite (scale) || scale <= 0) throw new NotSupportedException ("Recorded SVG has an invalid scale.");
				int offsetX = width != 0 && height != 0 ? (int)(textureWidth - size.width * scale) / 2 : 0;
				int offsetY = width != 0 && height != 0 ? (int)(textureHeight - size.height * scale) / 2 : 0;
				using (var recorder = new SKPictureRecorder ()) {
					var canvas = recorder.BeginRecording (SKRect.Create (textureWidth, textureHeight));
					canvas.Translate (offsetX, offsetY); canvas.Scale (scale);
					IntPtr pointer = Marshal.ReadIntPtr (image, 24);
					int shapeCount = 0;
					while (pointer != IntPtr.Zero) {
						if (++shapeCount > 100000) throw new NotSupportedException ("Recorded SVG has too many shapes.");
						Shape shape = Marshal.PtrToStructure<Shape> (pointer); pointer = shape.Next;
						if ((shape.Flags & 1) == 0) continue;
						bool hasFill = shape.Fill.Type != 0;
						bool hasStroke = shape.Stroke.Type != 0 && shape.StrokeWidth > 0;
						if (!hasFill && !hasStroke) continue;
						if (hasFill && shape.Fill.Type != 1) throw new NotSupportedException ("Skia GUI recorder does not support SVG gradient fills.");
						if (hasStroke && shape.Stroke.Type != 1) throw new NotSupportedException ("Skia GUI recorder does not support SVG gradient strokes.");
						if (hasStroke && shape.DashCount != 0) throw new NotSupportedException ("Skia GUI recorder does not support dashed SVG strokes.");
						if (requireOpaque && shape.Opacity != 1f)
							throw new NotSupportedException ("Skia SVG texture upload requires opaque geometry.");
						using (var path = ReadPath (shape.Paths, shape.FillRule)) {
							if (hasFill) {
								if (requireOpaque && ((uint)shape.Fill.Value.ToInt64 () >> 24) != 255)
								throw new NotSupportedException ("Skia SVG texture upload requires opaque fill paint.");
								using (var paint = ReadPaint (shape.Fill, shape.Opacity, textureColorOrder)) canvas.DrawPath (path, paint);
							}
							if (hasStroke) {
							if (requireOpaque && ((uint)shape.Stroke.Value.ToInt64 () >> 24) != 255)
								throw new NotSupportedException ("Skia SVG texture upload requires opaque stroke paint.");
								using (var paint = ReadPaint (shape.Stroke, shape.Opacity, textureColorOrder, shape.StrokeWidth, shape.Cap, shape.Join)) canvas.DrawPath (path, paint);
							}
						}
					}
					SKPicture picture = recorder.EndRecording ();
					// LoadSvg uploads a filtered texture and later samples its edge texels.
					// Check actual Skia output instead of assuming geometry coverage from
					// path bounds; this accepts layered paths while rejecting transparent RGB.
					if (requireOpaque) {
						try { EnsureOpaqueCoverage (picture, textureWidth, textureHeight); }
						catch { picture.Dispose (); throw; }
					}
					return picture;
				}
			} finally { SvgNativeMethods.nsvgDelete (image); }
		}

		static void EnsureOpaqueCoverage (SKPicture picture, int width, int height)
		{
			using (var bitmap = new SKBitmap (new SKImageInfo (width, height, SKColorType.Rgba8888, SKAlphaType.Premul)))
			using (var canvas = new SKCanvas (bitmap)) {
				canvas.Clear (SKColors.Transparent);
				canvas.DrawPicture (picture);
				canvas.Flush ();
				for (int y = 0; y < height; y++)
					for (int x = 0; x < width; x++)
						if (bitmap.GetPixel (x, y).Alpha != 255)
							throw new NotSupportedException ("Recorded SVG texture has transparent Skia coverage at pixel " + x + "," + y + ".");
			}
		}

		static SKPath ReadPath (IntPtr pointer, byte fillRule)
		{
			var result = new SKPath { FillType = fillRule == 1 ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
			try {
				int pathCount = 0;
				while (pointer != IntPtr.Zero) {
					if (++pathCount > 100000) throw new NotSupportedException ("Recorded SVG has too many paths.");
					Path path = Marshal.PtrToStructure<Path> (pointer); pointer = path.Next;
					if (path.Count <= 0 || path.Count > 1000000 || (path.Count - 1) % 3 != 0 || path.Points == IntPtr.Zero)
						throw new NotSupportedException ("Recorded SVG has an invalid cubic path.");
					float* points = (float*)path.Points;
					result.MoveTo (points[0], points[1]);
					for (int i = 1; i < path.Count; i += 3)
						result.CubicTo (points[i * 2], points[i * 2 + 1], points[i * 2 + 2], points[i * 2 + 3], points[i * 2 + 4], points[i * 2 + 5]);
					if (path.Closed != 0) result.Close ();
				}
				return result;
			} catch { result.Dispose (); throw; }
		}

		static SKRect PixelRectangle (IntPtr pointer, float scale, int offsetX, int offsetY)
		{
			if (pointer == IntPtr.Zero) throw new NotSupportedException ("Recorded SVG geometry is not supported by the Skia recorder.");
			Path path = Marshal.PtrToStructure<Path> (pointer);
			if (path.Count != 13 || path.Closed == 0 || path.Next != IntPtr.Zero || path.Points == IntPtr.Zero)
				throw new NotSupportedException ("Recorded SVG geometry is not supported by the Skia recorder.");
			float* points = (float*)path.Points;
			float left = float.PositiveInfinity, top = float.PositiveInfinity, right = float.NegativeInfinity, bottom = float.NegativeInfinity;
			for (int segment = 0; segment < 4; segment++) {
				int start = segment * 6, end = start + 6;
				float x = points[start], y = points[start + 1], nextX = points[end], nextY = points[end + 1];
				bool vertical = x == nextX, horizontal = y == nextY;
				if (vertical == horizontal) throw new NotSupportedException ("Recorded SVG geometry is not supported by the Skia recorder.");
				for (int i = start + 2; i < end; i += 2)
					if ((vertical && points[i] != x) || (horizontal && points[i + 1] != y) || points[i] < Math.Min (x, nextX) || points[i] > Math.Max (x, nextX) || points[i + 1] < Math.Min (y, nextY) || points[i + 1] > Math.Max (y, nextY))
						throw new NotSupportedException ("Recorded SVG geometry is not supported by the Skia recorder.");
				left = Math.Min (left, x); right = Math.Max (right, x); top = Math.Min (top, y); bottom = Math.Max (bottom, y);
			}
			if (points[24] != points[0] || points[25] != points[1] || left >= right || top >= bottom)
				throw new NotSupportedException ("Recorded SVG geometry is not supported by the Skia recorder.");
			for (int corner = 0; corner < 4; corner++) {
				for (int previous = 0; previous < corner; previous++)
					if (points[corner * 6] == points[previous * 6] && points[corner * 6 + 1] == points[previous * 6 + 1])
						throw new NotSupportedException ("Recorded SVG geometry is not supported by the Skia recorder.");
				if ((points[corner * 6] != left && points[corner * 6] != right) || (points[corner * 6 + 1] != top && points[corner * 6 + 1] != bottom))
					throw new NotSupportedException ("Recorded SVG geometry is not supported by the Skia recorder.");
			}
			var result = new SKRect (left * scale + offsetX, top * scale + offsetY, right * scale + offsetX, bottom * scale + offsetY);
			if (!float.IsFinite (result.Left) || !float.IsFinite (result.Top) || !float.IsFinite (result.Right) || !float.IsFinite (result.Bottom) ||
				result.Left != MathF.Floor (result.Left) || result.Top != MathF.Floor (result.Top) || result.Right != MathF.Floor (result.Right) || result.Bottom != MathF.Floor (result.Bottom))
				throw new NotSupportedException ("Recorded SVG fractional rectangle bounds are unsupported by the Skia recorder.");
			return result;
		}

		static SKPaint ReadPaint (Paint source, float opacity, bool textureColorOrder, float strokeWidth = 0, byte cap = 0, byte join = 0)
		{
			uint packed = (uint)source.Value.ToInt64 ();
			byte red = (byte)packed, green = (byte)(packed >> 8), blue = (byte)(packed >> 16), alpha = (byte)(packed >> 24);
			// SvgLoader.rasterizeSvg swaps red and blue before uploading its byte
			// buffer as GL_RGBA. Preserve that texture output for LoadSvg while
			// DrawSvg continues to match Cairo's native ARGB32 interpretation.
			if (textureColorOrder) (red, blue) = (blue, red);
			int sourceAlpha = alpha * (int)(Math.Clamp (opacity, 0, 1) * 256) >> 8;
			var paint = new SKPaint { IsAntialias = true };
			if (strokeWidth > 0) {
				paint.Style = SKPaintStyle.Stroke;
				paint.StrokeWidth = strokeWidth;
				paint.StrokeCap = cap == 1 ? SKStrokeCap.Round : cap == 2 ? SKStrokeCap.Square : SKStrokeCap.Butt;
				paint.StrokeJoin = join == 1 ? SKStrokeJoin.Round : join == 2 ? SKStrokeJoin.Bevel : SKStrokeJoin.Miter;
			}
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
	}
}
