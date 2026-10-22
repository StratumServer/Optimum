using System;
using SkiaSharp;

namespace Cairo
{
	// The child image contains numeric stops, never rasterized gradient pixels.
	internal static class RecordedCairoGradient
	{
		static readonly Lazy<SKRuntimeEffect> effect = new Lazy<SKRuntimeEffect>(() => {
			var value = SKRuntimeEffect.CreateShader(Source, out string errors);
			return value ?? throw new InvalidOperationException(errors);
		});

		internal static SKShader Create(IntPtr handle, PatternType type, Extend extend, SKMatrix toDevice, Matrix fromDevice, int width, int height, out SKImage stops, ManagedGradientSource managed = null)
		{
			int count;
			if (managed == null) NativeMethods.cairo_pattern_get_color_stop_count(handle, out count);
			else count = managed.StopCount;
			stops = null;
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
			using (var bitmap = new SKBitmap(256, checked((count + 63) / 64), SKColorType.Rgba8888, SKAlphaType.Opaque)) {
				for (int i = 0; i < count; i++) {
					double offset, r, g, b, a;
					if (managed == null) NativeMethods.cairo_pattern_get_color_stop_rgba(handle, i, out offset, out r, out g, out b, out a);
					else { var stop = managed.Stops[i]; offset = stop.Offset; r = stop.Red; g = stop.Green; b = stop.Blue; a = stop.Alpha; }
					int position = (int)Math.Round(offset * 65536);
					int column = (i % 64) * 4, row = i / 64;
					int red = Short(r), green = Short(g), blue = Short(b), opacity = Short(a);
					bitmap.SetPixel(column + 0, row, new SKColor((byte)red, (byte)green, (byte)blue, 255));
					bitmap.SetPixel(column + 1, row, new SKColor((byte)(red >> 8), (byte)(green >> 8), (byte)(blue >> 8), 255));
					bitmap.SetPixel(column + 2, row, new SKColor((byte)opacity, (byte)(opacity >> 8), (byte)position, 255));
					bitmap.SetPixel(column + 3, row, new SKColor((byte)(position >> 8), (byte)(position >> 16), 0, 255));
				}
				stops = SKImage.FromBitmap(bitmap);
			}
			// Cairo fits geometry and its pattern transform together before fixed16 conversion.
			double magnitude = Math.Max(Math.Max(Math.Abs(x0), Math.Abs(y0)), Math.Max(Math.Abs(x1), Math.Abs(y1)));
			magnitude = Math.Max(magnitude, Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)));
			magnitude = Math.Max(magnitude, Math.Max(Math.Max(r0, r1), Math.Abs(r1 - r0)));
			double factor = magnitude > 16383 ? 16383 / magnitude : 1;
			x0 = Fixed(x0 * factor); y0 = Fixed(y0 * factor); r0 = Fixed(r0 * factor);
			x1 = Fixed(x1 * factor); y1 = Fixed(y1 * factor); r1 = Fixed(r1 * factor);
			fromDevice = new Matrix(fromDevice.Xx * factor, fromDevice.Yx * factor, fromDevice.Xy * factor, fromDevice.Yy * factor, fromDevice.X0 * factor, fromDevice.Y0 * factor);
			fromDevice = FixedMatrix(fromDevice, width * .5, height * .5);
			fromDevice.X0 -= x0; fromDevice.Y0 -= y0;
			x1 -= x0; y1 -= y0; x0 = y0 = 0;
			double dx = x1 - x0, dy = y1 - y0, den = dx * dx + dy * dy;
			double stepX = den == 0 ? 0 : (dx * fromDevice.Xx + dy * fromDevice.Yx) * 65536 / den;
			double stepY = den == 0 ? 0 : (dx * fromDevice.Xy + dy * fromDevice.Yy) * 65536 / den;
			double start = den == 0 ? 0 : ((fromDevice.X0 - x0) * dx + (fromDevice.Y0 - y0) * dy) * 65536 / den + (stepX + stepY) * .5;
			var uniforms = new SKRuntimeEffectUniforms(effect.Value) {
				["deviceX"] = new float[] { toDevice.ScaleX, toDevice.SkewX, toDevice.TransX },
				["deviceY"] = new float[] { toDevice.SkewY, toDevice.ScaleY, toDevice.TransY },
				["patternX"] = new float[] { (float)fromDevice.Xx, (float)fromDevice.Xy, (float)fromDevice.X0 },
				["patternY"] = new float[] { (float)fromDevice.Yx, (float)fromDevice.Yy, (float)fromDevice.Y0 },
				["linear"] = new float[] { (float)stepX, (float)stepY, (float)start },
				["count"] = count, ["kind"] = type == PatternType.Linear ? 0 : 1,
				["extend"] = extend == Extend.None ? 0 : extend == Extend.Pad ? 1 : extend == Extend.Repeat ? 2 : 3,
				["circle0"] = new float[] { (float)x0, (float)y0, (float)r0 },
				["circle1"] = new float[] { (float)x1, (float)y1, (float)r1 }
			};
			using (var data = stops.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest))) {
				var children = new SKRuntimeEffectChildren(effect.Value) { ["stops"] = data };
				return effect.Value.ToShader(uniforms, children) ?? throw new InvalidOperationException("Cairo gradient shader creation failed.");
			}
		}
		static int Short(double value) => (int)(value * 65535 + .5);
		static double Fixed(double value) => Math.Round(value * 65536) / 65536;
		static Matrix FixedMatrix(Matrix matrix, double xc, double yc)
		{
			var value = new Matrix(Fixed(matrix.Xx), Fixed(matrix.Yx), Fixed(matrix.Xy), Fixed(matrix.Yy), Fixed(matrix.X0), Fixed(matrix.Y0));
			for (int i = 0; i < 5; i++) {
				double dx = Fixed(value.Xx * xc + value.Xy * yc + value.X0) - (matrix.Xx * xc + matrix.Xy * yc + matrix.X0);
				double dy = Fixed(value.Yx * xc + value.Yy * yc + value.Y0) - (matrix.Yx * xc + matrix.Yy * yc + matrix.Y0);
				dx = Fixed(dx); dy = Fixed(dy);
				value.X0 -= dx; value.Y0 -= dy;
				if (dx == 0 && dy == 0) break;
			}
			return value;
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
			byte alpha = Byte(sum[3]);
			if (alpha == 0) return SKShader.CreateColor(SKColors.Transparent);
			// A degenerate Cairo gradient is an analytic solid, not a sampled image.
			using var colorSpace = SKColorSpace.CreateSrgb();
			return SKShader.CreateColor(new SKColorF(Byte(sum[0] * sum[3]) / (float)alpha, Byte(sum[1] * sum[3]) / (float)alpha, Byte(sum[2] * sum[3]) / (float)alpha, alpha / 255f), colorSpace);
		}
		static byte Byte(double value) => (byte)((int)(value * 65535 + .5) >> 8);

		internal const string Source = @"
uniform shader stops;
uniform int count;
uniform int kind;
uniform int extend;
uniform float3 deviceX;
uniform float3 deviceY;
uniform float3 linear;
uniform float3 patternX;
uniform float3 patternY;
uniform float3 circle0;
uniform float3 circle1;
float2 stopPoint(int index, float column) {
    return float2(float(index - (index / 64) * 64) * 4 + column, float(index / 64) + .5);
}
float position(int index) {
    float4 a = floor(float4(stops.eval(stopPoint(index, 2.5))) * 255 + .5);
    float4 b = floor(float4(stops.eval(stopPoint(index, 3.5))) * 255 + .5);
    return a.b + b.r * 256 + b.g * 65536;
}
float4 color(int index) {
    float3 low = floor(float3(stops.eval(stopPoint(index, .5)).rgb) * 255 + .5);
    float3 high = floor(float3(stops.eval(stopPoint(index, 1.5)).rgb) * 255 + .5);
    float2 alpha = floor(float2(stops.eval(stopPoint(index, 2.5)).rg) * 255 + .5);
    return float4(low + high * 256, alpha.r + alpha.g * 256) / 65535;
}
bool valid(float t) {
    return extend == 0 ? t >= 0 && t <= 1 : circle0.z + t * (circle1.z - circle0.z) >= 0;
}
float4 main(float2 p) {
    float2 device = floor(float2(dot(deviceX, float3(p, 1)), dot(deviceY, float3(p, 1))));
    float3 center = float3(device + .5, 1);
    p = floor(float2(dot(patternX, center), dot(patternY, center)) * 65536 + .5) / 65536;
    float2 dc = circle1.xy - circle0.xy;
    float2 q = p - circle0.xy;
    float dr = circle1.z - circle0.z;
    float t;
    if (kind == 0) {
        t = float(int(linear.z + linear.y * device.y)) + float(int(linear.x * device.x));
    } else {
        float A = dot(dc, dc) - dr * dr;
        float B = dot(q, dc) + circle0.z * dr;
        float C = dot(q, q) - circle0.z * circle0.z;
        if (A == 0) {
            if (B == 0) return float4(0);
            t = C / (2 * B);
            if (!valid(t)) return float4(0);
        } else {
            float D = B * B - A * C;
            if (D < 0) return float4(0);
            t = (B + sqrt(D)) / A;
            if (!valid(t)) {
                t = (B - sqrt(D)) / A;
                if (!valid(t)) return float4(0);
            }
        }
    }
    if (kind != 0) t = float(int(t * 65536));
    if (extend == 2) t -= floor(t / 65536) * 65536;
    if (extend == 3) {
        t -= floor(t / 131072) * 131072;
        if (t >= 65536) t = 131072 - t;
    }
    float first = position(0), last = position(count - 1);
    if (extend == 0 && (t < first || t >= last)) return float4(0);
    int low = 0, high = count;
    for (int search = 0; search < 32; search++) {
        if (low >= high) break;
        int middle = (low + high) / 2;
        if (position(middle) <= t) low = middle + 1; else high = middle;
    }
    float left, right;
    float4 lc, rc;
    if (low == 0) {
        lc = color(extend == 2 ? count - 1 : 0); rc = color(0);
        left = extend == 2 ? last - 65536 : -2147483648.0; right = first;
    } else if (low == count) {
        lc = color(count - 1); rc = color(extend == 2 ? 0 : count - 1);
        left = last; right = extend == 2 ? first + 65536 : 2147483647.0;
    } else {
        left = position(low - 1); right = position(low); lc = color(low - 1); rc = color(low);
    }
    float width = right - left;
    float fraction = clamp((t - left) / width, 0, 1);
    float4 c = mix(lc, rc, fraction);
    c.rgb *= c.a;
    return floor(c * 255 + .5) / 255;
}";
	}
}
