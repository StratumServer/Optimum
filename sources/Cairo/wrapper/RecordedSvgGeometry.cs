using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NanoSvg;
using SkiaSharp;

namespace Cairo
{
	// NanoSVG remains the parser and geometry authority. Only fully covered,
	// disjoint, device-aligned solid rectangles are recorded; every other feature
	// is replayed by the original native rasterizer.
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

		internal static SKPicture Parse (string text, int textureWidth, int textureHeight, int width, int height, int? tint, bool opaqueOnly = false)
		{
			if (textureWidth <= 0 || textureHeight <= 0) throw new NotSupportedException ("Recorded SVG has no positive texture size.");
			// SvgLoader applies tint after NanoSVG rasterization using straight RGBA
			// bytes. Skia's recorded paint uses premultiplied alpha, so the same
			// channel values cannot be represented faithfully for partially covered
			// pixels. Keep tinted assets on the native path until parity is proven.
			if (tint.HasValue) throw new NotSupportedException ("Tinted SVG requires native rasterization.");
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
					var rectangles = new List<SKRect> ();
					double coveredArea = 0;
					while (pointer != IntPtr.Zero) {
						if (++shapeCount > 100000) throw new NotSupportedException ("Recorded SVG has too many shapes.");
						Shape shape = Marshal.PtrToStructure<Shape> (pointer); pointer = shape.Next;
						if ((shape.Flags & 1) == 0) continue;
						if (shape.Fill.Type == 0) {
							if (shape.Stroke.Type != 0 && shape.StrokeWidth > 0)
								throw new NotSupportedException ("Recorded SVG stroke requires native rasterization.");
							continue;
						}
						if (rectangles.Count >= 256) throw new NotSupportedException ("Recorded SVG rectangle budget requires native rasterization.");
						if (shape.Stroke.Type != 0 && shape.StrokeWidth > 0) throw new NotSupportedException ("Recorded SVG stroke requires native rasterization.");
						if (shape.Fill.Type != 1) throw new NotSupportedException ("Recorded SVG gradient requires native rasterization.");
						if (opaqueOnly && (((uint)shape.Fill.Value.ToInt64 () >> 24) != 255 || shape.Opacity != 1f))
							throw new NotSupportedException ("Recorded SVG transparency requires native rasterization for texture upload.");
						SKRect rectangle = PixelRectangle (shape.Paths, scale, offsetX, offsetY);
						if (rectangle.Left < 0 || rectangle.Top < 0 || rectangle.Right > textureWidth || rectangle.Bottom > textureHeight)
							throw new NotSupportedException ("Recorded SVG clipped coverage requires native rasterization.");
						foreach (var previous in rectangles)
							if (rectangle.Left < previous.Right && rectangle.Right > previous.Left && rectangle.Top < previous.Bottom && rectangle.Bottom > previous.Top)
								throw new NotSupportedException ("Recorded SVG overlap requires native rasterization.");
						rectangles.Add (rectangle);
						coveredArea += (double)rectangle.Width * rectangle.Height;
						using (var path = ReadPath (shape.Paths, shape.FillRule))
						using (var paint = ReadPaint (shape.Fill, shape.Opacity, opaqueOnly)) canvas.DrawPath (path, paint);
					}
					// NanoSVG writes defringed RGB into transparent uncovered pixels. Exact
					// reconstruction is only established when the rectangles tile the image.
					if (coveredArea != (double)textureWidth * textureHeight)
						throw new NotSupportedException ("Recorded SVG uncovered pixels require native rasterization.");
					return recorder.EndRecording ();
				}
			} finally { SvgNativeMethods.nsvgDelete (image); }
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
			if (pointer == IntPtr.Zero) throw new NotSupportedException ("Recorded SVG geometry requires native rasterization.");
			Path path = Marshal.PtrToStructure<Path> (pointer);
			if (path.Count != 13 || path.Closed == 0 || path.Next != IntPtr.Zero || path.Points == IntPtr.Zero)
				throw new NotSupportedException ("Recorded SVG geometry requires native rasterization.");
			float* points = (float*)path.Points;
			float left = float.PositiveInfinity, top = float.PositiveInfinity, right = float.NegativeInfinity, bottom = float.NegativeInfinity;
			for (int segment = 0; segment < 4; segment++) {
				int start = segment * 6, end = start + 6;
				float x = points[start], y = points[start + 1], nextX = points[end], nextY = points[end + 1];
				bool vertical = x == nextX, horizontal = y == nextY;
				if (vertical == horizontal) throw new NotSupportedException ("Recorded SVG geometry requires native rasterization.");
				for (int i = start + 2; i < end; i += 2)
					if ((vertical && points[i] != x) || (horizontal && points[i + 1] != y) || points[i] < Math.Min (x, nextX) || points[i] > Math.Max (x, nextX) || points[i + 1] < Math.Min (y, nextY) || points[i + 1] > Math.Max (y, nextY))
						throw new NotSupportedException ("Recorded SVG geometry requires native rasterization.");
				left = Math.Min (left, x); right = Math.Max (right, x); top = Math.Min (top, y); bottom = Math.Max (bottom, y);
			}
			if (points[24] != points[0] || points[25] != points[1] || left >= right || top >= bottom)
				throw new NotSupportedException ("Recorded SVG geometry requires native rasterization.");
			for (int corner = 0; corner < 4; corner++) {
				for (int previous = 0; previous < corner; previous++)
					if (points[corner * 6] == points[previous * 6] && points[corner * 6 + 1] == points[previous * 6 + 1])
						throw new NotSupportedException ("Recorded SVG geometry requires native rasterization.");
				if ((points[corner * 6] != left && points[corner * 6] != right) || (points[corner * 6 + 1] != top && points[corner * 6 + 1] != bottom))
					throw new NotSupportedException ("Recorded SVG geometry requires native rasterization.");
			}
			var result = new SKRect (left * scale + offsetX, top * scale + offsetY, right * scale + offsetX, bottom * scale + offsetY);
			if (!float.IsFinite (result.Left) || !float.IsFinite (result.Top) || !float.IsFinite (result.Right) || !float.IsFinite (result.Bottom) ||
				result.Left != MathF.Floor (result.Left) || result.Top != MathF.Floor (result.Top) || result.Right != MathF.Floor (result.Right) || result.Bottom != MathF.Floor (result.Bottom))
				throw new NotSupportedException ("Recorded SVG fractional coverage requires native rasterization.");
			return result;
		}

		static SKPaint ReadPaint (Paint source, float opacity, bool matchNativeTextureUpload)
		{
			uint packed = (uint)source.Value.ToInt64 ();
			byte red = (byte)packed, green = (byte)(packed >> 8), blue = (byte)(packed >> 16), alpha = (byte)(packed >> 24);
			// SvgLoader.rasterizeSvg swaps red and blue before uploading its byte
			// buffer as GL_RGBA. Preserve that texture output for LoadSvg while
			// DrawSvg continues to match Cairo's native ARGB32 interpretation.
			if (matchNativeTextureUpload) (red, blue) = (blue, red);
			int sourceAlpha = alpha * (int)(Math.Clamp (opacity, 0, 1) * 256) >> 8;
			var paint = new SKPaint { IsAntialias = false };
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
