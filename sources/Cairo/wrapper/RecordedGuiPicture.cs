using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NanoSvg;
using SkiaSharp;

namespace Cairo
{
	internal sealed class RecordedGuiPicture : IDisposable
	{
		internal readonly SKPicture Picture;
		internal readonly int X, Y, Width, Height;
		internal readonly int? Tint;
		internal readonly bool TextureColorOrder;
		internal readonly Action<Context> NativeReplay;
		readonly IDisposable owner;
		internal RecordedGuiPicture(SKPicture picture, int x, int y, int width, int height, int? tint, Action<Context> replay, bool textureColorOrder = false, IDisposable owner = null)
		{ this.owner = owner; Picture = picture; X = x; Y = y; Width = width; Height = height; Tint = tint; TextureColorOrder = textureColorOrder; NativeReplay = replay; }
		public void Dispose() { if (owner != null) owner.Dispose(); else Picture.Dispose(); }
	}

	// The game's NanoSVG parser remains the geometry authority. Its public ABI is
	// defined by anegostudios/nanosvg/include/nanosvg.h (NSVGimage includes viewBox).
	// Parsing and path recording do not rasterize pixels.
	internal static unsafe class RecordedSvgGeometry
	{
		[StructLayout(LayoutKind.Sequential)] struct Paint { internal byte Type; internal IntPtr Value; }
		[StructLayout(LayoutKind.Sequential)] struct Shape {
			internal fixed byte Id[64]; internal Paint Fill, Stroke;
			internal float Opacity, StrokeWidth, DashOffset; internal fixed float Dash[8];
			internal byte DashCount, Join, Cap; internal float Miter;
			internal byte FillRule, Flags; internal fixed float Bounds[4]; internal IntPtr Paths, Next;
		}
		[StructLayout(LayoutKind.Sequential)] struct Path {
			internal IntPtr Points; internal int Count; internal byte Closed;
			internal fixed float Bounds[4]; internal IntPtr Next;
		}


		internal static RecordedPreparationCache.Picture Acquire(string text, int textureWidth, int textureHeight, int width, int height)
		{
			RecordedPreparationCache.Key key = RecordedPreparationCache.Enabled && (text?.Length ?? 0) <= RecordedPreparationCache.MaxKeyBytes / 4
				? new RecordedPreparationCache.Key(5, new long[] { textureWidth, textureHeight, width, height }, text: text) : null;
			var cached = RecordedPreparationCache.Acquire<RecordedPreparationCache.Picture>(key); if (cached != null) return cached;
			var result = new RecordedPreparationCache.Picture(Parse(text, textureWidth, textureHeight, width, height));
			try { RecordedPreparationCache.Store(key, result); return result; } catch { result.Dispose(); throw; }
		}

		internal static SKPicture Parse(string text, int textureWidth, int textureHeight, int width, int height)
		{
			IntPtr image = SvgNativeMethods.nsvgParse(text, "px", 96);
			if (image == IntPtr.Zero) throw new NotSupportedException("NanoSVG could not parse the recorded SVG.");
			try {
				var size = Marshal.PtrToStructure<NsvgSize>(SvgNativeMethods.nsvgImageGetSize(image));
				if (size.width <= 0 || size.height <= 0) throw new NotSupportedException("Recorded SVG has no positive viewport.");
				float scale = width == 0 && height == 0 ? 1 : width == 0 ? height / size.height : height == 0 ? width / size.width : Math.Min(width / size.width, height / size.height);
				int offsetX = width != 0 && height != 0 ? (int)(textureWidth - size.width * scale) / 2 : 0;
				int offsetY = width != 0 && height != 0 ? (int)(textureHeight - size.height * scale) / 2 : 0;
				using (var recorder = new SKPictureRecorder()) {
					var canvas = recorder.BeginRecording(SKRect.Create(textureWidth, textureHeight));
					canvas.Translate(offsetX, offsetY); canvas.Scale(scale);
					IntPtr pointer = Marshal.ReadIntPtr(image, 24);
					int shapeCount = 0;
					var rectangles = new List<SKRect>();
					double coveredArea = 0;
					while (pointer != IntPtr.Zero) {
						if (++shapeCount > 100000) throw new NotSupportedException("Recorded SVG has too many shapes.");
						Shape shape = Marshal.PtrToStructure<Shape>(pointer); pointer = shape.Next;
						if ((shape.Flags & 1) == 0) continue;
						if (shape.Fill.Type == 0 && (shape.Stroke.Type == 0 || shape.StrokeWidth <= 0)) continue;
						if (rectangles.Count >= 256)
							throw new NotSupportedException("Recorded SVG rectangle budget requires native rasterization.");
						if (shape.Stroke.Type != 0 && shape.StrokeWidth > 0)
							throw new NotSupportedException("Recorded SVG stroke requires native rasterization.");
						SKRect rectangle = PixelRectangle(shape.Paths, scale, offsetX, offsetY);
						if (rectangle.Left < 0 || rectangle.Top < 0 || rectangle.Right > textureWidth || rectangle.Bottom > textureHeight)
							throw new NotSupportedException("Recorded SVG clipped coverage requires native rasterization.");
						var color = Color((uint)shape.Fill.Value.ToInt64());
						if (shape.Fill.Type == 1 && (color.Alpha * (int)(Math.Clamp(shape.Opacity, 0, 1) * 256) >> 8) == 0 &&
							(rectangle.Left != 0 || rectangle.Top != 0 || rectangle.Right != textureWidth || rectangle.Bottom != textureHeight))
							throw new NotSupportedException("Recorded SVG transparent coverage requires native rasterization.");
						foreach (var previous in rectangles)
							if (rectangle.Left < previous.Right && rectangle.Right > previous.Left && rectangle.Top < previous.Bottom && rectangle.Bottom > previous.Top)
								throw new NotSupportedException("Recorded SVG overlap requires native rasterization.");
						rectangles.Add(rectangle);
						coveredArea += (double)rectangle.Width * rectangle.Height;
						using (var path = ReadPath(shape.Paths, shape.FillRule)) {
							using (var paint = ReadPaint(shape.Fill, shape.Opacity)) if (paint != null) canvas.DrawPath(path, paint);
						}
					}
					// NanoSVG defringes RGB into uncovered alpha-zero pixels.
					if (coveredArea != (double)textureWidth * textureHeight)
						throw new NotSupportedException("Recorded SVG uncovered pixels require native rasterization.");
					return recorder.EndRecording();
				}
			} finally { SvgNativeMethods.nsvgDelete(image); }
		}

		static SKPath ReadPath(IntPtr pointer, byte fillRule)
		{
			var result = new SKPath { FillType = fillRule == 1 ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
			try {
				int pathCount = 0;
				while (pointer != IntPtr.Zero) {
					if (++pathCount > 100000) throw new NotSupportedException("Recorded SVG has too many paths.");
					Path path = Marshal.PtrToStructure<Path>(pointer); pointer = path.Next;
					if (path.Count <= 0 || path.Count > 1000000 || (path.Count - 1) % 3 != 0 || path.Points == IntPtr.Zero)
						throw new NotSupportedException("Recorded SVG has an invalid cubic path.");
					float* points = (float*)path.Points;
					result.MoveTo(points[0], points[1]);
					for (int i = 1; i < path.Count; i += 3) result.CubicTo(points[i * 2], points[i * 2 + 1], points[i * 2 + 2], points[i * 2 + 3], points[i * 2 + 4], points[i * 2 + 5]);
					if (path.Closed != 0) result.Close();
				}
				return result;
			} catch { result.Dispose(); throw; }
		}

		static SKRect PixelRectangle(IntPtr pointer, float scale, int offsetX, int offsetY)
		{
			if (pointer == IntPtr.Zero) throw new NotSupportedException("Recorded SVG geometry requires native rasterization.");
			Path path = Marshal.PtrToStructure<Path>(pointer);
			if (path.Count != 13 || path.Closed == 0 || path.Next != IntPtr.Zero || path.Points == IntPtr.Zero)
				throw new NotSupportedException("Recorded SVG geometry requires native rasterization.");
			float* points = (float*)path.Points;
			float left = float.PositiveInfinity, top = float.PositiveInfinity, right = float.NegativeInfinity, bottom = float.NegativeInfinity;
			for (int segment = 0; segment < 4; segment++) {
				int start = segment * 6, end = start + 6;
				float x = points[start], y = points[start + 1], nextX = points[end], nextY = points[end + 1];
				bool vertical = x == nextX, horizontal = y == nextY;
				if (vertical == horizontal) throw new NotSupportedException("Recorded SVG geometry requires native rasterization.");
				for (int i = start + 2; i < end; i += 2)
					if ((vertical && points[i] != x) || (horizontal && points[i + 1] != y) || points[i] < Math.Min(x, nextX) || points[i] > Math.Max(x, nextX) || points[i + 1] < Math.Min(y, nextY) || points[i + 1] > Math.Max(y, nextY))
						throw new NotSupportedException("Recorded SVG geometry requires native rasterization.");
				left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
			}
			if (points[24] != points[0] || points[25] != points[1]) throw new NotSupportedException("Recorded SVG geometry requires native rasterization.");
			if (left >= right || top >= bottom) throw new NotSupportedException("Recorded SVG geometry requires native rasterization.");
			for (int corner = 0; corner < 4; corner++)
				for (int previous = 0; previous < corner; previous++)
					if (points[corner * 6] == points[previous * 6] && points[corner * 6 + 1] == points[previous * 6 + 1])
						throw new NotSupportedException("Recorded SVG geometry requires native rasterization.");
			for (int corner = 0; corner < 4; corner++)
				if ((points[corner * 6] != left && points[corner * 6] != right) || (points[corner * 6 + 1] != top && points[corner * 6 + 1] != bottom))
					throw new NotSupportedException("Recorded SVG geometry requires native rasterization.");
			var result = new SKRect(left * scale + offsetX, top * scale + offsetY, right * scale + offsetX, bottom * scale + offsetY);
			if (!float.IsFinite(result.Left) || !float.IsFinite(result.Top) || !float.IsFinite(result.Right) || !float.IsFinite(result.Bottom) || result.Left != MathF.Floor(result.Left) || result.Top != MathF.Floor(result.Top) || result.Right != MathF.Floor(result.Right) || result.Bottom != MathF.Floor(result.Bottom))
				throw new NotSupportedException("Recorded SVG fractional coverage requires native rasterization.");
			return result;
		}

		static SKColor Color(uint color) => new SKColor((byte)color, (byte)(color >> 8), (byte)(color >> 16), (byte)(color >> 24));
		static SKPaint ReadPaint(Paint source, float opacity)
		{
			if (source.Type == 0) return null;
			// NanoSVG samples a quantized gradient table at integer coordinates
			// and clamps all spread modes. Skia gradients do not preserve it.
			if (source.Type == 2 || source.Type == 3)
				throw new NotSupportedException("Recorded SVG gradient requires native rasterization.");
			if (source.Type != 1) throw new NotSupportedException("Recorded SVG paint type: " + source.Type);
			var paint = new SKPaint { IsAntialias = true };
			try {
				var color = Color((uint)source.Value.ToInt64());
				int alpha = color.Alpha * (int)(Math.Clamp(opacity, 0, 1) * 256) >> 8;
				// Match NanoSVG's integer premultiplication; SVG overlay separately
				// truncates its conversion back to the native straight color bytes.
				paint.ColorF = alpha == 0 ? new SKColorF(0, 0, 0, 0) : new SKColorF(
					Premultiply(color.Red, alpha) / (float)alpha,
					Premultiply(color.Green, alpha) / (float)alpha,
					Premultiply(color.Blue, alpha) / (float)alpha, alpha / 255f);
				return paint;
			} catch { paint.Dispose(); throw; }
		}
		static int Premultiply(int channel, int alpha) => ((channel * alpha + 1) * 257) >> 16;
	}
}
