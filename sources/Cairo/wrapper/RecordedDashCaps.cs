using System;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Retains a zero-length on-dash cap at an open contour's terminal point.</summary>
	internal static class RecordedDashCaps
	{
		internal static void Append(SKPath path, SKPath outline, SKPaint paint, float[] intervals, float offset)
		{
			if (intervals == null || paint.StrokeCap == SKStrokeCap.Butt) return;
			bool hasZero = false;
			double period = 0;
			for (int i = 0; i < intervals.Length; i++) {
				period += intervals[i];
				if ((i & 1) == 0 && intervals[i] == 0) hasZero = true;
			}
			if (!hasZero || period <= 0) return;
			using var caps = new SKPath();
			using var measure = new SKPathMeasure(path, false);
			do {
				if (measure.IsClosed || measure.Length <= 0) continue;
				double phase = (offset + measure.Length) % period;
				if (phase < 0) phase += period;
				double position = 0;
				bool endsInDot = false;
				for (int i = 0; i < intervals.Length; i++) {
					if ((i & 1) == 0 && intervals[i] == 0 && Math.Min(Math.Abs(position - phase), Math.Abs(period - Math.Abs(position - phase))) < 1e-5)
						endsInDot = true;
					position += intervals[i];
				}
				if (!endsInDot || !measure.GetPositionAndTangent(measure.Length, out SKPoint point, out SKPoint tangent)) continue;
				float radius = paint.StrokeWidth / 2;
				if (paint.StrokeCap == SKStrokeCap.Round) caps.AddCircle(point.X, point.Y, radius);
				else {
					float dx = tangent.X * radius, dy = tangent.Y * radius;
					caps.MoveTo(point.X - dx - dy, point.Y - dy + dx);
					caps.LineTo(point.X + dx - dy, point.Y + dy + dx);
					caps.LineTo(point.X + dx + dy, point.Y + dy - dx);
					caps.LineTo(point.X - dx + dy, point.Y - dy - dx);
					caps.Close();
				}
			} while (measure.NextContour());
			if (caps.IsEmpty) return;
			using var union = new SKPath();
			if (!outline.Op(caps, SKPathOp.Union, union)) throw new InvalidOperationException("Skia could not retain terminal dash caps.");
			outline.Reset(); outline.AddPath(union);
		}
	}
}
