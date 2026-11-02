using System;
using System.Text;
using Cairo;
using Xunit;

// Queries on a recording context answer from the managed state and Skia; they must not create or touch native objects.
public sealed class CairoRecordedContextQueryTests
{
	static CairoRecordedContextQueryTests () { _ = CairoAPI.Version; }

	static (ImageSurface Surface, Context Context) Recorded()
	{
		var surface = new ImageSurface(Format.Argb32, 96, 64);
		surface.BeginRecording();
		return (surface, new Context(surface));
	}

	[Fact]
	public void RecordedQueriesDoNotMaterializeTheSurface()
	{
		var (surface, context) = Recorded();
		using (surface) using (context) {
			context.Scale(2, 2); context.Translate(3, 4); context.LineWidth = 3; context.SetSourceRGBA(.1, .2, .3, .4);
			context.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Normal); context.SetFontSize(12);
			context.Rectangle(1, 2, 20, 10);
			_ = context.Matrix; _ = context.CurrentPoint; _ = context.FillExtents(); _ = context.StrokeExtents(); _ = context.CopyPath().Handle;
			_ = context.CopyPathFlat().Handle; _ = context.TextExtents("Hello"); _ = context.FontExtents; _ = context.FontMatrix; _ = context.FontOptions;
			_ = context.InFill(5, 5); _ = context.InStroke(1, 2); _ = context.InClip(5, 5); _ = context.GetSource();
			double x = 1, y = 2; context.UserToDevice(ref x, ref y); context.DeviceToUser(ref x, ref y);
			Assert.Equal(SurfaceRecordingState.Recording, surface.RecordingState);
		}
	}

	[Fact]
	public void RecordedStateMatchesNativeCairo()
	{
		var (recordedSurface, recorded) = Recorded();
		using var nativeSurface = new ImageSurface(Format.Argb32, 96, 64);
		using var native = new Context(nativeSurface);
		using (recordedSurface) using (recorded) {
			foreach (Context context in new[] { recorded, native }) {
				context.Translate(5, 6); context.Scale(1.5, 2); context.Rotate(0.2);
				context.LineWidth = 4; context.LineCap = LineCap.Round; context.LineJoin = LineJoin.Bevel; context.MiterLimit = 3; context.FillRule = FillRule.EvenOdd;
				context.SetDash(new[] { 3.0, 2.0 }, 1); context.SetSourceRGBA(.25, .5, .75, .5); context.Operator = Operator.Source; context.Antialias = Antialias.None;
				context.MoveTo(2, 3); context.LineTo(30, 4); context.CurveTo(30, 20, 10, 20, 2, 10); context.ClosePath();
				context.Arc(20, 20, 6, 0, Math.PI);
			}
			Assert.Equal(native.Matrix.ToString(), recorded.Matrix.ToString());
			Assert.Equal(native.LineWidth, recorded.LineWidth); Assert.Equal(native.LineCap, recorded.LineCap);
			Assert.Equal(native.LineJoin, recorded.LineJoin); Assert.Equal(native.MiterLimit, recorded.MiterLimit);
			Assert.Equal(native.FillRule, recorded.FillRule); Assert.Equal(native.Operator, recorded.Operator);
			Assert.Equal(native.Antialias, recorded.Antialias); Assert.Equal(native.HasCurrentPoint, recorded.HasCurrentPoint);
			Assert.Equal(native.CurrentPoint.X, recorded.CurrentPoint.X, 9); Assert.Equal(native.CurrentPoint.Y, recorded.CurrentPoint.Y, 9);
			double nx = 7, ny = 9, rx = 7, ry = 9;
			native.UserToDevice(ref nx, ref ny); recorded.UserToDevice(ref rx, ref ry);
			Assert.Equal(nx, rx, 9); Assert.Equal(ny, ry, 9);
			Assert.Equal(native.InFill(10, 8), recorded.InFill(10, 8));
		}
	}

	[Fact]
	public void RecordedGetSourceReturnsTheCurrentSolidColorOrPattern()
	{
		var (surface, context) = Recorded();
		using (surface) using (context) {
			context.SetSourceRGBA(.2, .4, .6, .8);
			using (Pattern solid = context.GetSource()) {
				Assert.Equal(PatternType.Solid, solid.PatternType);
				NativeMethods.cairo_pattern_get_rgba(solid.NativeHandleForRecorder, out double r, out double g, out double b, out double a);
				Assert.Equal(new[] { .2, .4, .6, .8 }, new[] { r, g, b, a });
			}
			using var gradient = new LinearGradient(0, 0, 10, 10);
			gradient.AddColorStop(0, new Color(1, 0, 0, 1)); gradient.AddColorStop(1, new Color(0, 0, 1, 1));
			context.SetSource(gradient);
			using Pattern current = context.GetSource();
			Assert.Equal(PatternType.Linear, current.PatternType);
		}
	}

	[Fact]
	public void RecordedCopiedPathsUseNativeLayoutAndRoundTripThroughAppendPath()
	{
		var (surface, context) = Recorded();
		using (surface) using (context) {
			context.Scale(2, 3);
			context.MoveTo(1, 1); context.LineTo(5, 2); context.CurveTo(6, 6, 2, 7, 1, 4); context.ClosePath();
			using Cairo.Path copy = context.CopyPath();
			context.NewPath();
			context.AppendPath(copy);
			using Cairo.Path again = context.CopyPath();
			using var nativeSurface = new ImageSurface(Format.Argb32, 8, 8);
			using var native = new Context(nativeSurface);
			native.Scale(2, 3); native.AppendPath(again);
			using Cairo.Path viaNative = native.CopyPath();
			Assert.Equal(Describe(again), Describe(viaNative));
		}
	}

	static string Describe(Cairo.Path path)
	{
		var text = new StringBuilder();
		int count = System.Runtime.InteropServices.Marshal.ReadInt32(path.Handle, 16);
		IntPtr data = System.Runtime.InteropServices.Marshal.ReadIntPtr(path.Handle, 8);
		for (int i = 0; i < count; i++) text.Append(System.Runtime.InteropServices.Marshal.ReadInt64(data, i * 16)).Append(',');
		return text.ToString();
	}
}

public sealed class CairoSkiaTextTests
{
	static CairoSkiaTextTests () { _ = CairoAPI.Version; }

	static (ImageSurface Surface, Context Context) Recorded(string family = "Noto Sans", double size = 14)
	{
		var surface = new ImageSurface(Format.Argb32, 200, 80);
		surface.BeginRecording();
		var context = new Context(surface);
		context.SelectFontFace(family, FontSlant.Normal, FontWeight.Normal); context.SetFontSize(size);
		return (surface, context);
	}

	[Theory]
	[InlineData("Noto Sans", 11)]
	[InlineData("Noto Sans", 14)]
	[InlineData("Noto Sans", 24)]
	[InlineData("Noto Sans", 36)]
	public void FontExtentsMatchNativeCairo(string family, double size)
	{
		var (surface, context) = Recorded(family, size);
		using var nativeSurface = new ImageSurface(Format.Argb32, 8, 8);
		using var native = new Context(nativeSurface);
		native.SelectFontFace(family, FontSlant.Normal, FontWeight.Normal); native.SetFontSize(size);
		using (surface) using (context) {
			FontExtents expected = native.FontExtents, actual = context.FontExtents;
			Assert.Equal(expected.Ascent, actual.Ascent, 6); Assert.Equal(expected.Descent, actual.Descent, 6);
			Assert.Equal(expected.Height, actual.Height, 6); Assert.Equal(expected.MaxXAdvance, actual.MaxXAdvance, 6);
		}
	}

	// Advances are exact; the ink box is the box Skia's rasterizer reports, within one pixel of Cairo's outline box.
	[Theory]
	[InlineData("Hello World", 11)]
	[InlineData("Settings", 14)]
	[InlineData("Inventory (3/10)", 24)]
	[InlineData("Ångström", 36)]
	[InlineData("0123456789", 14)]
	public void TextExtentsMatchNativeCairoWithinOnePixel(string text, double size)
	{
		var (surface, context) = Recorded("Noto Sans", size);
		using var nativeSurface = new ImageSurface(Format.Argb32, 8, 8);
		using var native = new Context(nativeSurface);
		native.SelectFontFace("Noto Sans", FontSlant.Normal, FontWeight.Normal); native.SetFontSize(size);
		using (surface) using (context) {
			TextExtents expected = native.TextExtents(text), actual = context.TextExtents(text);
			Assert.Equal(expected.XAdvance, actual.XAdvance, 6);
			Assert.InRange(Math.Abs(expected.XBearing - actual.XBearing), 0, 1); Assert.InRange(Math.Abs(expected.YBearing - actual.YBearing), 0, 1);
			Assert.InRange(Math.Abs(expected.Width - actual.Width), 0, 1); Assert.InRange(Math.Abs(expected.Height - actual.Height), 0, 1);
		}
	}

	[Fact]
	public void TextPathAdvancesTheCurrentPointByTheTextAdvanceAndAddsOutlines()
	{
		var (surface, context) = Recorded();
		using (surface) using (context) {
			context.MoveTo(10, 30);
			double advance = context.TextExtents("Path").XAdvance;
			context.TextPath("Path");
			Assert.True(context.HasCurrentPoint);
			Assert.Equal(10 + advance, context.CurrentPoint.X, 6); Assert.Equal(30, context.CurrentPoint.Y, 6);
			Rectangle area = context.FillExtents();
			Assert.True(area.Width > 0 && area.Height > 0);
			Assert.InRange(area.X, 9, 12); Assert.True(area.Y < 30);
			Assert.Equal(SurfaceRecordingState.Recording, surface.RecordingState);
		}
	}

	[Fact]
	public void EmptyTextPathLeavesPathAndCurrentPointUnchanged()
	{
		var (surface, context) = Recorded();
		using (surface) using (context) {
			context.MoveTo(2, 2); context.LineTo(20, 2);
			PointD before = context.CurrentPoint;
			context.TextPath("");
			Assert.Equal(before.X, context.CurrentPoint.X); Assert.Equal(before.Y, context.CurrentPoint.Y);
			context.TextPath(new byte[] { 0 });
			Assert.Equal(before.X, context.CurrentPoint.X);
		}
	}

	[Fact]
	public void GlyphPathAddsOutlinesAndLeavesTheCurrentPointWhereCairoDoes()
	{
		var (surface, context) = Recorded();
		using var nativeSurface = new ImageSurface(Format.Argb32, 8, 8);
		using var native = new Context(nativeSurface);
		native.SelectFontFace("Noto Sans", FontSlant.Normal, FontWeight.Normal); native.SetFontSize(14);
		using (surface) using (context) {
			var glyphs = new[] { new Glyph(36, 10, 30), new Glyph(37, 25, 30) };
			native.MoveTo(1, 1); native.GlyphPath(glyphs);
			context.MoveTo(1, 1); context.GlyphPath(glyphs);
			// The last outline point differs by the hinting Cairo applies and Skia's canonical outlines lack.
			Assert.InRange(Math.Abs(native.CurrentPoint.X - context.CurrentPoint.X), 0, 0.5); Assert.InRange(Math.Abs(native.CurrentPoint.Y - context.CurrentPoint.Y), 0, 0.5);
			Rectangle area = context.FillExtents(), expected = native.FillExtents();
			Assert.True(area.Width > 0 && area.Height > 0);
			// Skia outlines are unhinted, Cairo's are hinted: boxes agree to within a pixel.
			Assert.InRange(Math.Abs(expected.X - area.X), 0, 1.5); Assert.InRange(Math.Abs(expected.Y - area.Y), 0, 1.5);
			Assert.InRange(Math.Abs(expected.Width - area.Width), 0, 1.5); Assert.InRange(Math.Abs(expected.Height - area.Height), 0, 1.5);
		}
	}

	[Fact]
	public void ShowTextAdvancesThePenLikeTheMeasuredAdvance()
	{
		var (surface, context) = Recorded();
		using (surface) using (context) {
			context.MoveTo(4, 20);
			double advance = context.TextExtents("pen").XAdvance;
			context.ShowText("pen");
			Assert.Equal(4 + advance, context.CurrentPoint.X, 6);
		}
	}
}
