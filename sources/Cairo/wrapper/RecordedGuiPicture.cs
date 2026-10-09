using System;
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
		internal RecordedGuiPicture(SKPicture picture, int x, int y, int width, int height, int? tint, Action<Context> replay, bool textureColorOrder = false)
		{ Picture = picture; X = x; Y = y; Width = width; Height = height; Tint = tint; TextureColorOrder = textureColorOrder; NativeReplay = replay; }
		public void Dispose() => Picture.Dispose();
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
		[StructLayout(LayoutKind.Sequential)] struct Gradient {
			internal fixed float Transform[6]; internal byte Spread;
			internal float FocusX, FocusY; internal int Count;
		}
		[StructLayout(LayoutKind.Sequential)] struct Stop { internal uint Color; internal float Offset; }

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
					while (pointer != IntPtr.Zero) {
						if (++shapeCount > 100000) throw new NotSupportedException("Recorded SVG has too many shapes.");
						Shape shape = Marshal.PtrToStructure<Shape>(pointer); pointer = shape.Next;
						if ((shape.Flags & 1) == 0) continue;
						using (var path = ReadPath(shape.Paths, shape.FillRule)) {
							using (var paint = ReadPaint(shape.Fill, shape.Opacity)) if (paint != null) canvas.DrawPath(path, paint);
							using (var paint = ReadPaint(shape.Stroke, shape.Opacity)) if (paint != null && shape.StrokeWidth > 0) {
								paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = shape.StrokeWidth;
								paint.StrokeMiter = shape.Miter; paint.StrokeJoin = (SKStrokeJoin)shape.Join; paint.StrokeCap = (SKStrokeCap)shape.Cap;
								SKPathEffect dash = null;
								try {
									if (shape.DashCount > 0) {
										if (shape.DashCount > 8) throw new NotSupportedException("Recorded SVG dash count exceeds its native array.");
										var intervals = new float[shape.DashCount % 2 == 0 ? shape.DashCount : shape.DashCount * 2];
										for (int i = 0; i < intervals.Length; i++) intervals[i] = shape.Dash[i % shape.DashCount];
										dash = SKPathEffect.CreateDash(intervals, shape.DashOffset); paint.PathEffect = dash;
									}
									canvas.DrawPath(path, paint);
								} finally { dash?.Dispose(); }
							}
						}
					}
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

		static SKColor Color(uint color) => new SKColor((byte)color, (byte)(color >> 8), (byte)(color >> 16), (byte)(color >> 24));
		static SKPaint ReadPaint(Paint source, float opacity)
		{
			if (source.Type == 0) return null;
			var paint = new SKPaint { IsAntialias = true };
			try {
				if (source.Type == 1) { var color = Color((uint)source.Value.ToInt64()); paint.Color = color.WithAlpha((byte)Math.Clamp((int)(color.Alpha * opacity), 0, 255)); return paint; }
				if (source.Type != 2 && source.Type != 3) throw new NotSupportedException("Recorded SVG paint type: " + source.Type);
				Gradient gradient = Marshal.PtrToStructure<Gradient>(source.Value);
				if (gradient.Count < 1 || gradient.Count > 4096) throw new NotSupportedException("Recorded SVG gradient has an invalid stop count.");
				var colors = new SKColor[Math.Max(2, gradient.Count)]; var positions = new float[colors.Length];
				for (int i = 0; i < gradient.Count; i++) { var stop = Marshal.PtrToStructure<Stop>(IntPtr.Add(source.Value, sizeof(Gradient) + i * sizeof(Stop))); colors[i] = Color(stop.Color); positions[i] = stop.Offset; }
				if (gradient.Count == 1) { colors[1] = colors[0]; positions[0] = 0; positions[1] = 1; }
				var matrix = new SKMatrix(gradient.Transform[0], gradient.Transform[2], gradient.Transform[4], gradient.Transform[1], gradient.Transform[3], gradient.Transform[5], 0, 0, 1);
				if (!matrix.TryInvert(out SKMatrix inverse)) throw new NotSupportedException("Recorded SVG has a singular gradient transform.");
				var tile = gradient.Spread == 1 ? SKShaderTileMode.Mirror : gradient.Spread == 2 ? SKShaderTileMode.Repeat : SKShaderTileMode.Clamp;
				using (var shader = source.Type == 2 ? SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 1), colors, positions, tile)
					: SKShader.CreateRadialGradient(new SKPoint(0, 0), 1, colors, positions, tile))
				using (var transformed = shader.WithLocalMatrix(inverse)) paint.Shader = transformed;
				paint.Color = SKColors.White.WithAlpha((byte)Math.Clamp((int)(255 * opacity), 0, 255));
				return paint;
			} catch { paint.Dispose(); throw; }
		}
	}
}
