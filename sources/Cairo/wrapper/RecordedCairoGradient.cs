using System;
using SkiaSharp;

namespace Cairo
{
	internal static class RecordedCairoGradient
	{
		internal static SKShader Create(IntPtr handle, PatternType type, Extend extend, SKMatrix toDevice, Matrix fromDevice, int width, int height, out SKImage stops, ManagedGradientSource managed = null)
		{
			stops = null;
			int count;
			if (managed == null) NativeMethods.cairo_pattern_get_color_stop_count(handle, out count);
			else count = managed.StopCount;
			if (count == 0) return SKShader.CreateColor(SKColors.Transparent);

			double x0, y0, x1, y1, r0 = 0, r1 = 0;
			if (managed != null) { x0 = managed.X0; y0 = managed.Y0; r0 = managed.Radius0; x1 = managed.X1; y1 = managed.Y1; r1 = managed.Radius1; }
			else if (type == PatternType.Linear) NativeMethods.cairo_pattern_get_linear_points(handle, out x0, out y0, out x1, out y1);
			else NativeMethods.cairo_pattern_get_radial_circles(handle, out x0, out y0, out r0, out x1, out y1, out r1);

			const double epsilon = 2.2204460492503131e-16;
			if (type == PatternType.Radial && Math.Abs(r1 - r0) < epsilon &&
				(Math.Min(r0, r1) < epsilon || Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) < 2 * epsilon))
				return SKShader.CreateColor(SKColors.Transparent);
			if (type == PatternType.Linear && Math.Abs(x1 - x0) < epsilon && Math.Abs(y1 - y0) < epsilon)
				return Average(handle, managed, count, extend);

			var colors = new SKColor[count];
			var positions = new float[count];
			for (int i = 0; i < count; i++) {
				double offset, r, g, b, a;
				if (managed == null) NativeMethods.cairo_pattern_get_color_stop_rgba(handle, i, out offset, out r, out g, out b, out a);
				else { var stop = managed.Stops[i]; offset = stop.Offset; r = stop.Red; g = stop.Green; b = stop.Blue; a = stop.Alpha; }
				positions[i] = (float)offset;
				colors[i] = new SKColor(
					(byte)Math.Round(Math.Clamp(r, 0, 1) * 255),
					(byte)Math.Round(Math.Clamp(g, 0, 1) * 255),
					(byte)Math.Round(Math.Clamp(b, 0, 1) * 255),
					(byte)Math.Round(Math.Clamp(a, 0, 1) * 255));
			}

			SKShaderTileMode tile = extend == Extend.Repeat ? SKShaderTileMode.Repeat :
				extend == Extend.Reflect ? SKShaderTileMode.Mirror :
				extend == Extend.Pad ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal;

			SKMatrix localMatrix = SKMatrix.Identity;
			SKMatrix patternMatrix = RecordedGuiDrawing.ToSkia(fromDevice);
			if (!patternMatrix.IsIdentity && patternMatrix.TryInvert(out SKMatrix inverted)) {
				localMatrix = inverted;
			}

			if (type == PatternType.Linear) {
				return SKShader.CreateLinearGradient(
					new SKPoint((float)x0, (float)y0),
					new SKPoint((float)x1, (float)y1),
					colors,
					positions,
					tile,
					localMatrix);
			}

			if (r0 > epsilon) {
				return SKShader.CreateTwoPointConicalGradient(
					new SKPoint((float)x0, (float)y0),
					(float)r0,
					new SKPoint((float)x1, (float)y1),
					(float)r1,
					colors,
					positions,
					tile,
					localMatrix);
			}

			return SKShader.CreateRadialGradient(
				new SKPoint((float)x1, (float)y1),
				(float)r1,
				colors,
				positions,
				tile,
				localMatrix);
		}

		static SKShader Average(IntPtr handle, ManagedGradientSource managed, int count, Extend extend)
		{
			if (extend == Extend.None) return SKShader.CreateColor(SKColors.Transparent);
			var positions = new double[count]; var colors = new double[count, 4];
			for (int i = 0; i < count; i++) {
				if (managed == null) NativeMethods.cairo_pattern_get_color_stop_rgba(handle, i, out positions[i], out colors[i, 0], out colors[i, 1], out colors[i, 2], out colors[i, 3]);
				else { var stop = managed.Stops[i]; positions[i] = stop.Offset; colors[i, 0] = stop.Red; colors[i, 1] = stop.Green; colors[i, 2] = stop.Blue; colors[i, 3] = stop.Alpha; }
			}
			var sum = new double[4];
			for (int i = 0; i < count; i++) {
				double weight = count == 1 ? 2 : extend == Extend.Pad ? (i == 0 || i == count - 1 ? 1 : 0) :
					i == 0 ? (extend == Extend.Repeat ? 1 + positions[1] - positions[count - 1] : positions[0] + positions[1]) :
					i == count - 1 ? (extend == Extend.Repeat ? 1 + positions[0] - positions[i - 1] : 2 - positions[i - 1] - positions[i]) : positions[i + 1] - positions[i - 1];
				for (int c = 0; c < 4; c++) sum[c] += colors[i, c] * weight * .5;
			}
			return SKShader.CreateColor(new SKColor(
				(byte)Math.Round(Math.Clamp(sum[0], 0, 1) * 255),
				(byte)Math.Round(Math.Clamp(sum[1], 0, 1) * 255),
				(byte)Math.Round(Math.Clamp(sum[2], 0, 1) * 255),
				(byte)Math.Round(Math.Clamp(sum[3], 0, 1) * 255)));
		}
	}
}
