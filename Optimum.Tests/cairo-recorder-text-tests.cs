using System;
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Cairo;
using Vintagestory.API.Config;
using Xunit;

/// <summary>
/// Text capture keeps Cairo font selection, measurement and glyph layout. Approved Gray and
/// Subpixel faces use Skia glyph runs; unsupported Gray faces keep Cairo outlines and
/// unsupported Subpixel faces materialize exactly. Local-AA policy allows flat pixels within
/// one level and filtered edge difference at most 96.
/// </summary>
[Collection ("Cairo materialization metrics")]
public sealed class CairoRecorderTextTests
{
	static CairoRecorderTextTests () { _ = CairoAPI.Version; CairoBundledFonts.Register (); }

	static Action<Context> Text (string family, FontWeight weight, double size, double x, double y, string text, Antialias antialias = Antialias.Gray, FontSlant slant = FontSlant.Normal)
	{
		return context => {
			context.SetSourceRGBA (.12, .18, .24, .9);
			context.Rectangle (0, 0, 400, 400);
			context.Fill ();
			context.SetFontSize (size);
			context.SelectFontFace (family, slant, weight);
			using (var options = new FontOptions { Antialias = antialias }) context.FontOptions = options;
			context.SetSourceRGBA (.94, .93, .89, 1);
			context.MoveTo (x, y);
			context.ShowText (text);
			context.SetSourceRGBA (1, .6, .2, .8);
			context.ShowText (" +stack 64"); // continues from Cairo's advanced current point
		};
	}




	static void AssertCurrentPointMatchesCairo (Context recorded, Context reference)
	{
		Assert.Equal (reference.HasCurrentPoint, recorded.HasCurrentPoint);
		PointD expected = reference.CurrentPoint, actual = recorded.CurrentPoint;
		Assert.Equal (expected.X, actual.X, 10);
		Assert.Equal (expected.Y, actual.Y, 10);
	}



















	static void OutlinedText (Context context, string text, double strokeWidth = 1)
	{
		var stroke = new Color (0, 0, 0, .8);
		var fill = new Color (.9, .8, .1, 1);
		context.TextPath (text);
		context.SetSourceColor (stroke);
		context.LineWidth = strokeWidth;
		context.StrokePreserve ();
		context.SetSourceColor (fill);
		context.Fill ();
	}


	[Theory]
	[InlineData (FontWeight.Normal, .75)]
	[InlineData (FontWeight.Bold, 1.0)]
	[InlineData (FontWeight.Bold, 1.25)]
	[InlineData (FontWeight.Bold, 1.5)]
	[InlineData (FontWeight.Bold, 2.0)]
	public void OutlinedTextSkiaPathMatchesCairoTextPathWithinLocalAntialiasPolicy (FontWeight weight, double strokeWidth)
	{
		CairoRecorderParityTests.AssertLocalAaParity (200, 40, context => {
			context.SelectFontFace ("sans-serif", FontSlant.Normal, weight);
			context.SetFontSize (18);
			using (var options = new FontOptions { Antialias = Antialias.Subpixel }) context.FontOptions = options;
			context.MoveTo (3.3, 26.2);
			OutlinedText (context, "Outline", strokeWidth);
		});
	}

	[Fact]
	public void TextPathAndFontStateDoNotMaterialize ()
	{
		CairoRecorderParityTests.AssertLocalAaParity (200, 40, context => {
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Bold);
			context.SetFontSize (18);
			_ = context.FontExtents;
			_ = context.TextExtents ("measure");
			context.MoveTo (3, 26);
			context.TextPath ("Outline");
			context.SetSourceRGBA (.9, .8, .1, 1);
			context.FillPreserve ();
			context.SetSourceRGBA (0, 0, 0, .8);
			context.LineWidth = 1;
			context.Stroke ();
		});
	}
}
