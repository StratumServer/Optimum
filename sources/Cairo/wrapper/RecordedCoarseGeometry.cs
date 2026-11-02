using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Scanline coverage for flattened geometry on Cairo's fixed device grids.</summary>
	internal static class RecordedCoarseGeometry
	{
		internal static SKPath QuantizeFast(ManagedGraphicsState state)
		{
			using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.PathCopy);
			var result = new SKPath();
			Matrix matrix = state.Matrix;
			var inverse = (Matrix)matrix.Clone(); inverse.Invert();
			foreach (var element in state.CopyPathFlat()) {
				if (element.Op == ManagedGraphicsState.PathOp.ClosePath) { result.Close(); continue; }
				double x = element.X1, y = element.Y1;
				matrix.TransformPoint(ref x, ref y);
				x = Math.Floor(x * 4) / 4; y = Math.Floor(y * 4) / 4;
				inverse.TransformPoint(ref x, ref y);
				if (element.Op == ManagedGraphicsState.PathOp.MoveTo) result.MoveTo((float)x, (float)y);
				else result.LineTo((float)x, (float)y);
			}
			return result;
		}

		readonly struct Edge
		{
			internal readonly double X1, Y1, X2, Y2;
			internal Edge(SKPoint a, SKPoint b) { X1 = a.X; Y1 = a.Y; X2 = b.X; Y2 = b.Y; }
			internal bool Active(double y) => Math.Min(Y1, Y2) <= y && y < Math.Max(Y1, Y2);
			internal int Winding => Y2 > Y1 ? 1 : -1;
			internal double X(double y) => X1 + (y - Y1) * (X2 - X1) / (Y2 - Y1);
			internal double FixedX(double y, int grid)
			{
				long ax = (long)Math.Floor(X1 * grid + .5), ay = (long)Math.Floor(Y1 * grid + .5);
				long bx = (long)Math.Floor(X2 * grid + .5), by = (long)Math.Floor(Y2 * grid + .5);
				long sy = (long)Math.Floor(y * grid + .5), divisor = by - ay;
				Int128 product = (Int128)(sy - ay) * (bx - ax);
				Int128 quotient = product / divisor;
				if (product % divisor != 0 && (product < 0) != (divisor < 0)) quotient--;
				return (ax + (double)quotient) / grid;
			}
		}

		static List<Edge> Edges(SKPath path, int grid = 256)
		{
			var result = new List<Edge>();
			using var iterator = path.CreateRawIterator();
			var points = new SKPoint[4];
			SKPoint cursor = default, start = default;
			bool open = false;
			SKPathVerb verb;
			while ((verb = iterator.Next(points)) != SKPathVerb.Done) {
				for (int i = 0; i < points.Length; i++) points[i] = new SKPoint((float)(Math.Floor(points[i].X * grid + .5) / grid), (float)(Math.Floor(points[i].Y * grid + .5) / grid));
				if (verb == SKPathVerb.Move) {
					if (open && cursor.Y != start.Y) result.Add(new Edge(cursor, start));
					cursor = start = points[0]; open = true;
				} else if (verb == SKPathVerb.Line) {
					if (cursor.Y != points[1].Y) result.Add(new Edge(cursor, points[1])); cursor = points[1];
				} else if (verb == SKPathVerb.Close) {
					if (open && cursor.Y != start.Y) result.Add(new Edge(cursor, start)); open = false; cursor = start;
				} else return null;
			}
			if (open && cursor.Y != start.Y) result.Add(new Edge(cursor, start));
			return result;
		}

		static List<Edge> Active(List<Edge> edges, double y)
		{
			var active = new List<Edge>();
			foreach (var edge in edges) if (edge.Active(y)) active.Add(edge);
			active.Sort((a, b) => a.X(y).CompareTo(b.X(y)));
			return active;
		}

		internal static SKPath Aliased(SKPath path, Matrix matrix, int width, int height)
		{
			using var device = new SKPath(path); device.Transform(RecordedGuiDrawing.ToSkia(matrix));
			List<Edge> edges = Edges(device);
			if (edges == null) return new SKPath(path);
			var result = new SKPath();
			bool evenOdd = path.FillType == SKPathFillType.EvenOdd;
			int top = (int)Math.Max(0, Math.Floor(device.Bounds.Top)), bottom = (int)Math.Min(height, Math.Ceiling(device.Bounds.Bottom));
			for (int y = top; y < bottom; y++) {
				// The mono converter samples one fixed unit before the exact pixel centre.
				double sample = y + 127.0 / 256;
				var active = new List<Edge>();
				foreach (var edge in edges) if (Math.Floor(Math.Min(edge.Y1, edge.Y2) + 127.0 / 256) <= y && y < Math.Floor(Math.Max(edge.Y1, edge.Y2) + 127.0 / 256)) active.Add(edge);
				active.Sort((a,b) => a.FixedX(sample,256).CompareTo(b.FixedX(sample,256)));
				int winding = 0, start = 0;
				var spans = new List<(int Left, int Right)>();
				foreach (var edge in active) {
					bool before = evenOdd ? (winding & 1) != 0 : winding != 0;
					winding += edge.Winding;
					bool after = evenOdd ? (winding & 1) != 0 : winding != 0;
					int x = (int)Math.Clamp(Math.Ceiling(edge.FixedX(sample,256) - .5), 0, width);
					if (!before && after) start = x;
					else if (before && !after && x > start) spans.Add((start,x));
				}
				for (int i = 0; i < spans.Count; i++) {
					int begin = spans[i].Left, end = spans[i].Right;
					// Adjacent mono spans retain the one-pixel bridge used by Cairo.
					while (i + 1 < spans.Count && spans[i + 1].Left <= end + 1) { i++; end = Math.Max(end,spans[i].Right); }
					result.AddRect(new SKRect(begin,y,end,y+1));
				}
			}
			var inverse = (Matrix)matrix.Clone(); inverse.Invert(); result.Transform(RecordedGuiDrawing.ToSkia(inverse));
			return result;
		}

		internal static RecordedGeometryCoverage Fast(SKPath path, Matrix matrix, int width, int height)
		{
			using var device = new SKPath(path); device.Transform(RecordedGuiDrawing.ToSkia(matrix));
			List<Edge> edges = Edges(device, 4);
			if (edges == null) return null;
			int left = (int)Math.Max(0, Math.Floor(device.Bounds.Left)), right = (int)Math.Min(width, Math.Ceiling(device.Bounds.Right));
			int top = (int)Math.Max(0, Math.Floor(device.Bounds.Top)), bottom = (int)Math.Min(height, Math.Ceiling(device.Bounds.Bottom));
			if (right <= left || bottom <= top) return null;
			using var bitmap = new SKBitmap(right - left, bottom - top, SKColorType.Bgra8888, SKAlphaType.Premul);
			bool evenOdd = path.FillType == SKPathFillType.EvenOdd;
			var row = new double[right - left];
			for (int y = top; y < bottom; y++) {
				Array.Clear(row);
				for (int sub = 0; sub < 4; sub++) {
					double y0 = y + sub / 4.0, y1 = y0 + .25;
					List<Edge> active = Active(edges, (y0 + y1) / 2);
					active.Sort((a,b) => a.FixedX(y0,4).CompareTo(b.FixedX(y0,4)));
					int winding = 0; Edge first = default;
					foreach (var edge in active) {
						bool before = evenOdd ? (winding & 1) != 0 : winding != 0;
						winding += edge.Winding;
						bool after = evenOdd ? (winding & 1) != 0 : winding != 0;
						if (!before && after) first = edge;
						else if (before && !after) {
							float l0 = (float)first.FixedX(y0,4);
							float r0 = (float)edge.FixedX(y0,4);
							int begin = (int)Math.Max(left, Math.Floor(l0)), end = (int)Math.Min(right, Math.Ceiling(r0));
							for (int x = begin; x < end; x++) row[x - left] += Math.Max(0, Math.Min(r0,x+1) - Math.Max(l0,x)) / 4;
						}
					}
				}
				for (int x = left; x < right; x++) {
					byte alpha = (byte)Math.Clamp((int)Math.Floor(row[x - left] * 32 + .5) * 8, 0, 255);
					bitmap.SetPixel(x - left, y - top, new SKColor(255, 255, 255, alpha));
				}
			}
			return new RecordedGeometryCoverage(SKImage.FromBitmap(bitmap), new SKRect(left, top, right, bottom));
		}
	}
}
