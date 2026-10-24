using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Cairo;
using Xunit;

// Differential tests: ManagedGraphicsState must agree with an installed native Cairo context
// after every operation of a seeded random program.
public sealed class CairoManagedGraphicsStateTests
{
	static CairoManagedGraphicsStateTests () { _ = CairoAPI.Version; }

	[StructLayout(LayoutKind.Sequential)] struct NativePath { public Status Status; public IntPtr Data; public int Count; }

	[Theory]
	[InlineData(1d)]
	[InlineData(1.5d)]
	[InlineData(2d)]
	public void TransformedArcAndBezierFlatPathsMatch(double scale)
	{
		using var surface = new ImageSurface(Format.Argb32, 128, 96);
		using var native = new Context(surface);
		var managed = new ManagedGraphicsState();
		native.Scale(scale,scale); managed.Scale(scale,scale);
		native.Translate(.375,.625); managed.Translate(.375,.625);
		native.Arc(40,16,9.1,0,Math.PI*2); managed.Arc(40,16,9.1,0,Math.PI*2);
		native.NewSubPath(); managed.NewSubPath();
		native.Arc(40,16,4.3,0,Math.PI*2); managed.Arc(40,16,4.3,0,Math.PI*2);
		AssertFlatPathsMatch(native,managed,"transformed arcs");
		native.NewPath(); managed.NewPath();
		native.MoveTo(4,36); managed.MoveTo(4,36);
		native.CurveTo(20,18,32,47,52,32); managed.CurveTo(20,18,32,47,52,32);
		native.ClosePath(); managed.ClosePath();
		AssertFlatPathsMatch(native,managed,"transformed Bezier");
	}

	[Theory]
	[InlineData(LineCap.Round)]
	[InlineData(LineCap.Square)]
	public void TerminalZeroDashCapsRetainStrokeQueries(LineCap cap)
	{
		using var surface = new ImageSurface(Format.Argb32,32,16);
		using var native = new Context(surface);
		var managed = new ManagedGraphicsState();
		native.LineWidth=2; managed.LineWidth=2;
		native.LineCap=cap; managed.LineCap=cap;
		native.SetDash(new[]{0d,4d},0); managed.SetDash(new[]{0d,4d},0);
		native.MoveTo(2,6); managed.MoveTo(2,6);
		native.LineTo(22,6); managed.LineTo(22,6);
		Assert.Equal(native.InStroke(22,6),managed.InStroke(22,6));
		Rectangle expected=native.StrokeExtents(), actual=managed.StrokeExtents();
		Assert.Equal(expected.X,actual.X,4); Assert.Equal(expected.Y,actual.Y,4);
		Assert.Equal(expected.Width,actual.Width,4); Assert.Equal(expected.Height,actual.Height,4);
	}

	static List<ManagedGraphicsState.PathElement> ReadNativePath(Context context)
	{
		var result = new List<ManagedGraphicsState.PathElement>();
		using Cairo.Path path = context.CopyPath();
		NativePath data = Marshal.PtrToStructure<NativePath>(path.Handle);
		if (data.Status != Status.Success) return result;
		for (int index = 0; index < data.Count;) {
			IntPtr p = IntPtr.Add(data.Data, index * 16);
			int kind = Marshal.ReadInt32(p), length = Marshal.ReadInt32(p, 4);
			double[] v = new double[6];
			for (int i = 1; i < length; i++) {
				v[(i - 1) * 2] = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, i * 16));
				v[(i - 1) * 2 + 1] = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, i * 16 + 8));
			}
			result.Add(new ManagedGraphicsState.PathElement((ManagedGraphicsState.PathOp)kind, v[0], v[1], v[2], v[3], v[4], v[5]));
			index += length;
		}
		return result;
	}

	static void AssertClose(double expected, double actual, string what)
	{
		double tolerance = 1e-9 * Math.Max(1, Math.Abs(expected));
		Assert.True(Math.Abs(expected - actual) <= tolerance, what + ": native " + expected.ToString("R") + ", managed " + actual.ToString("R"));
	}

	static void AssertWithin(double expected, double actual, double tolerance, string what)
	{
		Assert.True(Math.Abs(expected - actual) <= tolerance + 1e-9 * Math.Abs(expected), what + ": native " + expected.ToString("R") + ", managed " + actual.ToString("R"));
	}

	static void AssertSame(Context native, ManagedGraphicsState managed, string step)
	{
		Assert.True(native.Status == managed.Status, step + ": status native " + native.Status + ", managed " + managed.Status);
		Assert.True(native.HasCurrentPoint == managed.HasCurrentPoint, step + ": has current point");
		if (native.HasCurrentPoint) {
			Assert.True(managed.TryGetCurrentPoint(out double mx, out double my), step);
			PointD p = native.CurrentPoint;
			AssertClose(p.X, mx, step + " current x"); AssertClose(p.Y, my, step + " current y");
		}
		Matrix m = native.Matrix, g = managed.Matrix;
		AssertClose(m.Xx, g.Xx, step + " xx"); AssertClose(m.Yx, g.Yx, step + " yx"); AssertClose(m.Xy, g.Xy, step + " xy");
		AssertClose(m.Yy, g.Yy, step + " yy"); AssertClose(m.X0, g.X0, step + " x0"); AssertClose(m.Y0, g.Y0, step + " y0");
		AssertClose(native.LineWidth, managed.LineWidth, step + " line width");
		AssertClose(native.MiterLimit, managed.MiterLimit, step + " miter limit");
		AssertClose(native.Tolerance, managed.Tolerance, step + " tolerance");
		Assert.Equal(native.LineCap, managed.LineCap);
		Assert.Equal(native.LineJoin, managed.LineJoin);
		Assert.Equal(native.FillRule, managed.FillRule);
		Assert.Equal(native.Operator, managed.Operator);
		Assert.Equal(native.Antialias, managed.Antialias);

		IntPtr nativeSource = NativeMethods.cairo_get_source(native.Handle);
		NativeMethods.cairo_pattern_get_rgba(nativeSource, out double sr, out double sg, out double sb, out double sa);
		var color = managed.SourceColor;
		Assert.True(managed.SourcePattern == null, step + ": source pattern");
		AssertClose(sr, color.Red, step + " source r"); AssertClose(sg, color.Green, step + " source g");
		AssertClose(sb, color.Blue, step + " source b"); AssertClose(sa, color.Alpha, step + " source a");
		Matrix nativeFont = native.FontMatrix, managedFont = managed.FontMatrix;
		AssertClose(nativeFont.Xx, managedFont.Xx, step + " font xx"); AssertClose(nativeFont.Yx, managedFont.Yx, step + " font yx");
		AssertClose(nativeFont.Xy, managedFont.Xy, step + " font xy"); AssertClose(nativeFont.Yy, managedFont.Yy, step + " font yy");
		using (FontOptions nativeOptions = native.FontOptions)
		using (FontOptions managedOptions = managed.GetFontOptions())
			Assert.True(nativeOptions == managedOptions, step + ": font options");

		Rectangle nf = native.FillExtents(), mf = managed.FillExtents();
		// The managed fill box covers the area a fill paints, so it never exceeds Cairo's box (which also keeps
		// zero-width spikes from partially overlapping collinear edges). CairoManagedFillExtentsTests checks equality.
		string fillContext = " | path " + Describe(ReadNativePath(native)) + " | ctm " + native.Matrix;
		if (mf.Width > 0 && mf.Height > 0) {
			Assert.True(nf.Width > 0 && nf.Height > 0, step + ": managed fill box without native box" + fillContext);
			Matrix toDevice = native.Matrix;
			double[] corners = { mf.X, mf.Y, mf.X + mf.Width, mf.Y + mf.Height, nf.X, nf.Y, nf.X + nf.Width, nf.Y + nf.Height };
			Assert.True(mf.X >= nf.X - 0.02 && mf.Y >= nf.Y - 0.02 && mf.X + mf.Width <= nf.X + nf.Width + 0.02 && mf.Y + mf.Height <= nf.Y + nf.Height + 0.02,
				step + ": managed fill box (" + mf.X + "," + mf.Y + "," + mf.Width + "," + mf.Height + ") not inside native (" + nf.X + "," + nf.Y + "," + nf.Width + "," + nf.Height + ")" + fillContext);
		}

		List<ManagedGraphicsState.PathElement> expected = ReadNativePath(native);
		ManagedGraphicsState.PathElement[] actual = managed.CopyPath();
		Assert.True(expected.Count == actual.Length, step + ": path length native " + expected.Count + ", managed " + actual.Length + "\n" + Describe(expected) + "\n--\n" + Describe(actual));
		for (int i = 0; i < actual.Length; i++) {
			Assert.True(expected[i].Op == actual[i].Op, step + ": op " + i + "\n" + Describe(expected) + "\n--\n" + Describe(actual));
			AssertClose(expected[i].X1, actual[i].X1, step + " e" + i + " x1"); AssertClose(expected[i].Y1, actual[i].Y1, step + " e" + i + " y1");
			AssertClose(expected[i].X2, actual[i].X2, step + " e" + i + " x2"); AssertClose(expected[i].Y2, actual[i].Y2, step + " e" + i + " y2");
			AssertClose(expected[i].X3, actual[i].X3, step + " e" + i + " x3"); AssertClose(expected[i].Y3, actual[i].Y3, step + " e" + i + " y3");
		}
		AssertFlatPathsMatch(native, managed, step);
	}

	static void AssertFlatPathsMatch(Context native, ManagedGraphicsState managed, string step)
	{
		var expected = new List<ManagedGraphicsState.PathElement>();
		using (Cairo.Path path = native.CopyPathFlat()) {
			NativePath data = Marshal.PtrToStructure<NativePath>(path.Handle);
			if (data.Status == Status.Success)
				for (int index = 0; index < data.Count;) {
					IntPtr p = IntPtr.Add(data.Data, index * 16);
					int kind = Marshal.ReadInt32(p), length = Marshal.ReadInt32(p, 4);
					double x = length > 1 ? BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 16)) : 0, y = length > 1 ? BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 24)) : 0;
					expected.Add(new ManagedGraphicsState.PathElement((ManagedGraphicsState.PathOp)kind, x, y));
					index += length;
				}
		}
		// Cairo repeats points when a sub-pixel curve subdivides and appends a degenerate segment after a
		// trailing move; neither changes the polyline, so compare with repeated points removed.
		expected = RemoveRepeatedPoints(expected);
		ManagedGraphicsState.PathElement[] actual = RemoveRepeatedPoints(new List<ManagedGraphicsState.PathElement>(managed.CopyPathFlat())).ToArray();
		if (expected.Count != actual.Length) {
			int first = 0;
			while (first < Math.Min(expected.Count, actual.Length) && expected[first].Op == actual[first].Op && Math.Abs(expected[first].X1 - actual[first].X1) < 1e-9 && Math.Abs(expected[first].Y1 - actual[first].Y1) < 1e-9) first++;
			int from = Math.Max(0, first - 2);
			Assert.Fail(step + ": flat path length native " + expected.Count + ", managed " + actual.Length + ", first difference at " + first + "\nnative:  " + Describe(expected.GetRange(from, Math.Min(5, expected.Count - from))) + "\nmanaged: " + Describe(new List<ManagedGraphicsState.PathElement>(actual).GetRange(from, Math.Min(5, actual.Length - from))));
		}
		for (int i = 0; i < actual.Length; i++) {
			Assert.True(expected[i].Op == actual[i].Op, step + ": flat op " + i);
			AssertClose(expected[i].X1, actual[i].X1, step + " flat " + i + " x"); AssertClose(expected[i].Y1, actual[i].Y1, step + " flat " + i + " y");
		}
	}

	static List<ManagedGraphicsState.PathElement> RemoveRepeatedPoints(List<ManagedGraphicsState.PathElement> path)
	{
		var result = new List<ManagedGraphicsState.PathElement>();
		foreach (var element in path) {
			if (element.Op == ManagedGraphicsState.PathOp.LineTo && result.Count > 0) {
				var previous = result[result.Count - 1];
				if (previous.Op != ManagedGraphicsState.PathOp.ClosePath && previous.X1 == element.X1 && previous.Y1 == element.Y1) continue;
			}
			result.Add(element);
		}
		return result;
	}

	static string Describe(IEnumerable<ManagedGraphicsState.PathElement> path)
	{
		var text = new System.Text.StringBuilder();
		foreach (var e in path) { text.Append(e.Op).Append('(').Append(e.X1.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';').Append(e.Y1.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); if (e.Op == ManagedGraphicsState.PathOp.CurveTo) text.Append(';').Append(e.X2.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';').Append(e.Y2.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';').Append(e.X3.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';').Append(e.Y3.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); text.Append(") "); }
		return text.ToString();
	}

	// Coordinates on a coarse grid plus a few off-grid values: the grid makes collinear and
	// degenerate segments likely, the others exercise 24.8 quantization.
	static double Coordinate(Random random)
	{
		switch (random.Next(8)) {
		case 0: return random.Next(-3, 4) * 8.0;
		case 1: return random.Next(-3, 4) * 8.0 + 0.001953125;
		case 2: return random.NextDouble() * 40 - 20;
		case 3: return 0;
		default: return random.Next(-6, 7) * 4.0;
		}
	}

	static void Step(Random random, Context native, ManagedGraphicsState managed, out string description)
	{
		double a = Coordinate(random), b = Coordinate(random), c = Coordinate(random), d = Coordinate(random), e = Coordinate(random), f = Coordinate(random);
		int op = random.Next(32);
		description = "op" + op + "(" + a + "," + b + "," + c + "," + d + "," + e + "," + f + ")";
		switch (op) {
		case 0: case 1: native.MoveTo(a, b); managed.MoveTo(a, b); break;
		case 2: case 3: case 4: native.LineTo(a, b); managed.LineTo(a, b); break;
		case 5: case 6: native.CurveTo(a, b, c, d, e, f); managed.CurveTo(a, b, c, d, e, f); break;
		case 7: native.RelMoveTo(a, b); managed.RelMoveTo(a, b); break;
		case 8: case 9: native.RelLineTo(a, b); managed.RelLineTo(a, b); break;
		case 10: native.RelCurveTo(a, b, c, d, e, f); managed.RelCurveTo(a, b, c, d, e, f); break;
		case 11: case 12: native.ClosePath(); managed.ClosePath(); break;
		case 13: native.NewSubPath(); managed.NewSubPath(); break;
		case 14: if (random.Next(4) == 0) { native.NewPath(); managed.NewPath(); } else { native.Rectangle(a, b, c, d); managed.Rectangle(a, b, c, d); } break;
		case 15: native.Translate(a, b); managed.Translate(a, b); break;
		case 16: { double s = random.Next(10) == 0 ? 0 : 0.5 + random.Next(5) * 0.5; native.Scale(s, s + 1); managed.Scale(s, s + 1); break; }
		case 17: { double angle = random.Next(6) * Math.PI / 6 + (random.Next(3) == 0 ? 0.1 : 0); native.Rotate(angle); managed.Rotate(angle); break; }
		case 18: native.Save(); managed.Save(); break;
		case 19: native.Restore(); managed.Restore(); break;
		case 20: native.IdentityMatrix(); managed.IdentityMatrix(); break;
		case 21: { var m = new Matrix(1 + random.Next(3), 0, random.Next(3) * 0.5, 1, a, b); native.Transform(m); managed.Transform(m); break; }
		case 22: native.LineWidth = a; managed.LineWidth = a; native.MiterLimit = Math.Abs(b); managed.MiterLimit = Math.Abs(b); break;
		case 23: { var rule = random.Next(2) == 0 ? FillRule.Winding : FillRule.EvenOdd; native.FillRule = rule; managed.FillRule = rule; break; }
		case 24: { double radius = Math.Abs(c) / 2; native.Arc(a, b, radius, d, e); managed.Arc(a, b, radius, d, e); break; }
		case 25: { double radius = Math.Abs(c) / 2; native.ArcNegative(a, b, radius, d, e); managed.ArcNegative(a, b, radius, d, e); break; }
		case 26: { double alpha = random.Next(3) == 0 ? 0 : Math.Abs(f) / 24; native.SetSourceRGBA(Math.Abs(a) / 24, Math.Abs(b) / 24, c / 12, alpha); managed.SetSourceRgba(Math.Abs(a) / 24, Math.Abs(b) / 24, c / 12, alpha); break; }
		case 27: { var dashes = random.Next(3) == 0 ? new double[0] : new[] { Math.Abs(a) + 1, Math.Abs(b) + 0.5 }; native.SetDash(dashes, c); managed.SetDash(dashes, c); break; }
		case 28: { double size = 6 + Math.Abs(a); native.SetFontSize(size); managed.SetFontSize(size); break; }
		case 29: { var m = new Matrix(8 + Math.Abs(a), 0, Math.Abs(b) / 16, 9 + Math.Abs(c), 0, 0); native.FontMatrix = m; managed.SetFontMatrix(m); break; }
		case 30: { using var options = new FontOptions { Antialias = (Antialias)random.Next(7), HintStyle = (HintStyle)random.Next(5), HintMetrics = (HintMetrics)random.Next(3), SubpixelOrder = (SubpixelOrder)random.Next(5) }; native.FontOptions = options; managed.SetFontOptions(options); break; }
		default: native.Tolerance = Math.Abs(a) / 16 + 0.01; managed.Tolerance = Math.Abs(a) / 16 + 0.01; break;
		}
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(5)]
	[InlineData(6)]
	[InlineData(7)]
	[InlineData(8)]
	public void RandomProgramsAgreeWithNativeCairoAfterEveryOperation(int seed)
	{
		var random = new Random(seed);
		for (int program = 0; program < 60; program++) {
			using var surface = new ImageSurface(Format.Argb32, 4, 4);
			using var native = new Context(surface);
			var managed = new ManagedGraphicsState();
			var history = new List<string>();
			AssertSame(native, managed, "initial");
			for (int step = 0; step < 40; step++) {
				Step(random, native, managed, out string description);
				history.Add(description);
				AssertSame(native, managed, "seed " + seed + " program " + program + " step " + step + " [" + string.Join(" ", history) + "]");
				if (native.Status != Status.Success) break;
			}
		}
	}

	[Theory]
	[InlineData(new double[0], 0.0)]
	[InlineData(new[] { 3.0 }, 1.0)]
	[InlineData(new[] { 3.0 }, -1.0)]
	[InlineData(new[] { 3.0, 2.0 }, 7.5)]
	[InlineData(new[] { 3.0, 2.0, 1.0 }, 21.0)]
	[InlineData(new[] { 0.0, 2.0 }, -0.5)]
	[InlineData(new[] { 0.0, 0.0 }, 0.0)]
	[InlineData(new[] { 1.0, -2.0 }, 0.0)]
	public void DashParametersAgreeWithNativeCairo(double[] dashes, double offset)
	{
		using var surface = new ImageSurface(Format.Argb32, 4, 4);
		using var native = new Context(surface);
		var managed = new ManagedGraphicsState();
		native.SetDash(dashes, offset); managed.SetDash(dashes, offset);
		Assert.Equal(native.Status, managed.Status);
		if (native.Status != Status.Success) return;
		int count = NativeMethods.cairo_get_dash_count(native.Handle);
		double[] expected = new double[count];
		IntPtr buffer = Marshal.AllocHGlobal(Math.Max(1, count) * sizeof(double));
		try {
			NativeMethods.cairo_get_dash(native.Handle, buffer, out double nativeOffset);
			if (count > 0) Marshal.Copy(buffer, expected, 0, count);
			Assert.Equal(expected, managed.GetDashes());
			AssertClose(nativeOffset, managed.DashOffset, "dash offset");
		} finally { Marshal.FreeHGlobal(buffer); }
	}

	[Theory]
	[InlineData(-3.0)]
	[InlineData(0.0)]
	[InlineData(1e-9)]
	[InlineData(0.5)]
	[InlineData(100.0)]
	public void LineWidthAndToleranceClampLikeNativeCairo(double value)
	{
		using var surface = new ImageSurface(Format.Argb32, 4, 4);
		using var native = new Context(surface);
		var managed = new ManagedGraphicsState();
		native.LineWidth = value; managed.LineWidth = value;
		native.Tolerance = value; managed.Tolerance = value;
		AssertSame(native, managed, "scalar " + value);
	}

	[Fact]
	public void RestoreWithoutSaveLatchesInvalidRestoreAndIgnoresLaterOperations()
	{
		using var surface = new ImageSurface(Format.Argb32, 4, 4);
		using var native = new Context(surface);
		var managed = new ManagedGraphicsState();
		native.Restore(); managed.Restore();
		native.MoveTo(1, 2); managed.MoveTo(1, 2);
		Assert.Equal(Status.InvalidRestore, native.Status);
		AssertSame(native, managed, "after invalid restore");
	}
}

public sealed class CairoManagedStrokeExtentsTests
{
	static CairoManagedStrokeExtentsTests () { _ = CairoAPI.Version; }

	// Cairo strokes a polygon (round joins and caps are tessellated) while Skia strokes exact arcs,
	// so boxes agree to the flattening tolerance; butt caps with miter or bevel joins are exact.
	[Theory]
	[InlineData(LineCap.Butt, LineJoin.Miter, 0.02)]
	[InlineData(LineCap.Butt, LineJoin.Bevel, 0.02)]
	[InlineData(LineCap.Square, LineJoin.Miter, 0.02)]
	[InlineData(LineCap.Round, LineJoin.Round, 0.15)]
	public void StrokeBoxesAgreeWithNativeCairoForSimpleShapes(LineCap cap, LineJoin join, double tolerance)
	{
		Action<Context>[] shapes = {
			c => { c.MoveTo(3, 4); c.LineTo(10, 4); },
			c => { c.MoveTo(3, 4); c.LineTo(10, 9); },
			c => { c.MoveTo(1, 1); c.LineTo(9, 2); c.LineTo(4, 8); c.ClosePath(); },
			c => c.Rectangle(2, 3, 5, 6),
			c => { c.MoveTo(0, 0); c.LineTo(8, 1); c.LineTo(0, 2); },
			c => { c.Scale(2, 3); c.MoveTo(1, 1); c.LineTo(6, 3); c.LineTo(2, 7); },
			c => { c.Rotate(0.5); c.MoveTo(1, 1); c.LineTo(6, 3); },
		};
		foreach (double width in new[] { 1.0, 2.0, 5.0 })
		foreach (var build in shapes) {
			using var surface = new ImageSurface(Format.Argb32, 4, 4);
			using var native = new Context(surface);
			var managed = new ManagedGraphicsState();
			native.LineCap = cap; managed.LineCap = cap; native.LineJoin = join; managed.LineJoin = join;
			native.LineWidth = width; managed.LineWidth = width;
			build(native);
			Replay(build, managed);
			Rectangle expected = native.StrokeExtents(), actual = managed.StrokeExtents();
			Assert.True(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance
				&& Math.Abs(expected.Width - actual.Width) <= tolerance && Math.Abs(expected.Height - actual.Height) <= tolerance,
				"cap " + cap + " join " + join + " width " + width + ": native (" + expected.X + "," + expected.Y + "," + expected.Width + "," + expected.Height + "), managed (" + actual.X + "," + actual.Y + "," + actual.Width + "," + actual.Height + ")");
		}
	}

	// The shapes above only use a handful of Context calls; replay them on the managed state.
	static void Replay(Action<Context> build, ManagedGraphicsState managed)
	{
		using var surface = new ImageSurface(Format.Argb32, 4, 4);
		using var recorder = new Context(surface);
		var calls = new RecordingForwarder(managed);
		calls.Run(build);
	}

	sealed class RecordingForwarder
	{
		readonly ManagedGraphicsState state;
		internal RecordingForwarder(ManagedGraphicsState state) { this.state = state; }
		internal void Run(Action<Context> build)
		{
			// Capture the native path the shape produces, then rebuild it on the managed state.
			using var surface = new ImageSurface(Format.Argb32, 4, 4);
			using var native = new Context(surface);
			build(native);
			state.Matrix = native.Matrix;
			Matrix ctm = native.Matrix;
			state.IdentityMatrix();
			using Cairo.Path path = native.CopyPath();
			// Path data is in user space of the final CTM; replay it under that CTM.
			state.Matrix = ctm;
			state.AppendNativePath(path.Handle);
		}
	}
}


public sealed class CairoManagedFillExtentsTests
{
	static CairoManagedFillExtentsTests () { _ = CairoAPI.Version; }

	[Fact]
	public void FillBoxesAgreeWithNativeCairoForSimpleShapes()
	{
		Action<Context>[] shapes = {
			c => c.Rectangle(2, 3, 5, 6),
			c => { c.MoveTo(1, 1); c.LineTo(9, 2); c.LineTo(4, 8); c.ClosePath(); },
			c => { c.MoveTo(0, 0); c.LineTo(10, 0); c.LineTo(10, 10); },
			c => { c.MoveTo(0, 0); c.CurveTo(0, 20, 20, 20, 20, 0); },
			c => { c.MoveTo(0, 0); c.LineTo(4, 0); c.LineTo(2, 4); c.ClosePath(); c.MoveTo(10, 10); c.LineTo(14, 10); c.LineTo(12, 14); c.ClosePath(); },
			c => { c.Rotate(0.5); c.Rectangle(2, 3, 5, 6); },
			c => { c.Scale(2, 3); c.Arc(5, 5, 4, 0, 2 * Math.PI); },
			c => { c.MoveTo(0, 0); c.LineTo(10, 0); },
			c => { c.MoveTo(0, 24); c.LineTo(0, 8); c.LineTo(0, 2); },
			c => { c.MoveTo(3, 4); c.LineTo(3, 4); },
		};
		foreach (FillRule rule in new[] { FillRule.Winding, FillRule.EvenOdd })
		foreach (var build in shapes) {
			using var surface = new ImageSurface(Format.Argb32, 4, 4);
			using var native = new Context(surface);
			build(native);
			native.FillRule = rule;
			var managed = new ManagedGraphicsState();
			managed.Matrix = native.Matrix; managed.FillRule = rule;
			using (Cairo.Path path = native.CopyPath()) {
				// Re-append the finished path under the final CTM; user-space coordinates round-trip through fixed point.
				managed.AppendNativePath(path.Handle);
			}
			Rectangle expected = native.FillExtents(), actual = managed.FillExtents();
			Assert.True(Math.Abs(expected.X - actual.X) <= 0.01 && Math.Abs(expected.Y - actual.Y) <= 0.01
				&& Math.Abs(expected.Width - actual.Width) <= 0.01 && Math.Abs(expected.Height - actual.Height) <= 0.01,
				rule + ": native (" + expected.X + "," + expected.Y + "," + expected.Width + "," + expected.Height + "), managed (" + actual.X + "," + actual.Y + "," + actual.Width + "," + actual.Height + ")");
		}
	}
}
