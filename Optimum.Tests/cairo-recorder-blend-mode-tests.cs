using System;
using Cairo;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoRecorderBlendModeTests
{
	[Fact]
	public void ScreenKeepsRecordedFillAndClippedPaint ()
	{
		// Each cell has a different source/destination alpha and colour pair, including
		// transparent and opaque ends. Check every pixel within the existing flat limit.
		double[] alphas = { 0, .125, .5, .875, 1 };
		CairoRecorderParityTests.AssertParity (80, 80, context => {
			context.Antialias = Antialias.None;
			for (int y = 0; y < alphas.Length; y++) {
				for (int x = 0; x < alphas.Length; x++) {
					context.Operator = Operator.Source;
					context.SetSourceRGBA (x / 4.0, .37, 1 - y / 4.0, alphas[y]);
					context.Rectangle (x * 16, y * 16, 16, 16);
					context.Fill ();
					context.Operator = Operator.Screen;
					context.SetSourceRGBA (1 - y / 4.0, .73, x / 4.0, alphas[x]);
					context.Rectangle (x * 16 + 2, y * 16 + 2, 6, 12);
					context.Fill ();
					context.Save ();
					context.Rectangle (x * 16 + 9, y * 16 + 2, 5, 12);
					context.Clip ();
					context.Paint ();
					context.Restore ();
				}
			}
		}, edgeAllowance: false);
	}

	[Fact]
	public void ScreenPreservesAntialiasedGeometry ()
	{
		CairoRecorderParityTests.AssertLocalAaParity (64, 48, context => {
			context.SetSourceRGBA (.21, .48, .76, .64);
			context.Paint ();
			context.Operator = Operator.Screen;
			context.SetSourceRGBA (.81, .36, .19, .72);
			context.Arc (23.25, 22.5, 16.75, 0, Math.PI * 2);
			context.Fill ();
			context.SetSourceRGBA (.34, .82, .57, .46);
			context.LineWidth = 3.5;
			context.MoveTo (8.25, 40.5);
			context.CurveTo (21, 8, 35, 46, 56.75, 10.25);
			context.Stroke ();
		});
	}

	[Fact]
	public void ScreenCoversVariedPremultipliedColourPairs ()
	{
		CairoRecorderParityTests.AssertParity (128, 128, context => {
			var random = new Random (130);
			context.Antialias = Antialias.None;
			for (int y = 0; y < 32; y++) {
				for (int x = 0; x < 32; x++) {
					foreach (Operator blend in new[] { Operator.Source, Operator.Screen }) {
						context.Operator = blend;
						context.SetSourceRGBA (random.NextDouble (), random.NextDouble (), random.NextDouble (), random.NextDouble ());
						context.Rectangle (x * 4, y * 4, 4, 4);
						context.Fill ();
					}
				}
			}
		}, edgeAllowance: false);
	}

	[Theory]
	[InlineData (Operator.Multiply)]
	[InlineData (Operator.Darken)]
	[InlineData (Operator.Lighten)]
	public void BlendModesMatchCairoWithoutFallback (Operator op)
	{
		CairoRecorderParityTests.AssertParity (16, 16, context => {
			context.SetSourceRGBA (.21, .48, .76, .64);
			context.Paint ();
			context.Operator = op;
			context.SetSourceRGBA (.81, .36, .19, .72);
			context.Rectangle (2, 2, 10, 10);
			context.Fill ();
		});
	}

	[Theory (Skip = "Known difference: Skia's Difference and Exclusion formulas land two channel values from Cairo's; the flat tolerance is one.")]
	[InlineData (Operator.Difference)]
	[InlineData (Operator.Exclusion)]
	public void DifferenceAndExclusionMatchCairoWithoutFallback (Operator op)
	{
		CairoRecorderParityTests.AssertParity (16, 16, context => {
			context.SetSourceRGBA (.21, .48, .76, .64);
			context.Paint ();
			context.Operator = op;
			context.SetSourceRGBA (.81, .36, .19, .72);
			context.Rectangle (2, 2, 10, 10);
			context.Fill ();
		});
	}

	[Fact]
	public void ScreenSurfacePatternPreservesNearestSamplingAndClip ()
	{
		using var tile = new ImageSurface (Format.Argb32, 4, 4);
		using (var tileContext = new Context (tile)) {
			tileContext.SetSourceRGBA (.74, .28, .15, .63);
			tileContext.Paint ();
			tileContext.SetSourceRGBA (.13, .51, .86, .47);
			tileContext.Rectangle (0, 0, 2, 2);
			tileContext.Fill ();
		}
		CairoRecorderParityTests.AssertParity (32, 24, context => {
			context.Antialias = Antialias.None;
			context.SetSourceRGBA (.21, .48, .76, .64);
			context.Paint ();
			context.Rectangle (3, 2, 25, 18);
			context.Clip ();
			context.Operator = Operator.Screen;
			using var pattern = new SurfacePattern (tile) { Extend = Extend.Repeat, Filter = Filter.Nearest };
			context.SetSource (pattern);
			context.Paint ();
		}, edgeAllowance: false);
	}

	[Theory (Skip = "Known difference: Screen-blended text is rasterized by Skia without the LCD compositor, so glyph edges differ from Cairo by up to 82 channel values. There is no Cairo fallback on this branch.")]
	[InlineData (Antialias.Gray)]
	[InlineData (Antialias.Subpixel)]
	public void ScreenTextMatchesCairoWithoutFallback (Antialias antialias)
	{
		CairoRecorderParityTests.AssertParity (120, 32, context => {
			context.SetSourceRGBA (.21, .48, .76, 1);
			context.Paint ();
			context.Operator = Operator.Screen;
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal);
			context.SetFontSize (14);
			using var options = new FontOptions { Antialias = antialias };
			context.FontOptions = options;
			context.SetSourceRGBA (.81, .36, .19, 1);
			context.MoveTo (3.375, 22.625);
			context.ShowText ("Screen ffi 123");
		});
	}

	[Fact]
	public void BlendModePaintWithAlphaMatchesCairoWithoutFallback ()
	{
		CairoRecorderParityTests.AssertParity (16, 16, context => {
			context.SetSourceRGBA (.25, .5, .75, .6);
			context.Paint ();
			context.Operator = Operator.Screen;
			context.SetSourceRGBA (.85, .35, .15, .7);
			context.PaintWithAlpha (.45);
		});
	}
}
