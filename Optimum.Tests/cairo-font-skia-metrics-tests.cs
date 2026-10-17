using System;
using Cairo;
using SkiaSharp;
using Vintagestory.API.Config;
using Vintagestory.API.Client;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoFontSkiaMetricsTests
{
	static CairoFontSkiaMetricsTests () { _ = CairoAPI.Version; CairoRecorderTextTests.BundledFonts.Register (); }

	[Theory]
	[InlineData ("sans-serif", FontWeight.Normal, 14.0)]
	[InlineData ("sans-serif", FontWeight.Bold, 19.2)]
	[InlineData ("Montserrat", FontWeight.Normal, 14.0)]
	[InlineData ("Montserrat", FontWeight.Bold, 21.0)]
	[InlineData ("Lora", FontWeight.Normal, 16.0)]
	[InlineData ("Lora", FontWeight.Bold, 24.0)]
	[InlineData ("Almendra", FontWeight.Normal, 18.0)]
	public void CairoFontMetricsUseSkiaAndStayNearNativeCairo (string family, FontWeight weight, double size)
	{
		float previousScale = RuntimeEnv.GUIScale;
		RuntimeEnv.GUIScale = 1;
		try {
		using var surface = new ImageSurface (Format.Argb32, 1, 1);
		using var reference = new Context (surface);
		using var font = new CairoFont (size, family) { FontWeight = weight };
		font.SetupContext (reference);

		TextExtents cairoText = reference.TextExtents ("Inventory 0123 Wg");
		TextExtents skiaText = font.GetTextExtents ("Inventory 0123 Wg");
		FontExtents cairoFont = reference.FontExtents;
		FontExtents skiaFont = font.GetFontExtents ();

		Assert.True (Math.Abs (skiaText.Width - cairoText.Width) <= 2.0,
			$"{family} {weight} {size}: Cairo width={cairoText.Width}, Skia width={skiaText.Width}, Cairo advance={cairoText.XAdvance}, Skia advance={skiaText.XAdvance}");
		Assert.InRange (Math.Abs (skiaText.XAdvance - cairoText.XAdvance), 0, 2.0);
		Assert.InRange (Math.Abs (skiaFont.Ascent - cairoFont.Ascent), 0, 2.0);
		Assert.InRange (Math.Abs (skiaFont.Height - cairoFont.Height), 0, 3.0);
		} finally {
			RuntimeEnv.GUIScale = previousScale;
		}
	}
	[Theory]
	[InlineData ("A\U0001f600B")]
	[InlineData ("\U0001d11e")]
	[InlineData ("café e\u0301")]
	[InlineData ("  ")]
	public void TextExtentsMeasureExactlyTheUnicodeGlyphs (string text)
	{
		float previousScale = RuntimeEnv.GUIScale;
		RuntimeEnv.GUIScale = 1;
		try {
			using var typeface = SKTypeface.FromFamilyName ("sans-serif");
			using var reference = new SKFont (typeface, 18) {
				Edging = SKFontEdging.SubpixelAntialias,
				Subpixel = true,
				Hinting = SKFontHinting.Slight
			};
			float advance = reference.MeasureText (text, out SKRect bounds);
			using var font = new CairoFont (18, "sans-serif");
			TextExtents actual = font.GetTextExtents (text);

			Assert.Equal (advance, actual.XAdvance, 6);
			Assert.Equal (bounds.Left, actual.XBearing, 6);
			Assert.Equal (bounds.Top, actual.YBearing, 6);
			Assert.Equal (bounds.Width, actual.Width, 6);
			Assert.Equal (bounds.Height, actual.Height, 6);
		} finally {
			RuntimeEnv.GUIScale = previousScale;
		}
	}

	[Theory]
	[InlineData (1f, EnumLinebreakBehavior.AfterWord)]
	[InlineData (1.5f, EnumLinebreakBehavior.AfterWord)]
	[InlineData (2f, EnumLinebreakBehavior.AfterCharacter)]
	[InlineData (1f, EnumLinebreakBehavior.None)]
	public void FontLayoutUsesSkiaMetricsWithoutTheSharedCairoContext (float scale, EnumLinebreakBehavior behavior)
	{
		float previousScale = RuntimeEnv.GUIScale;
		Context previousContext = CairoFont.FontMeasuringContext;
		RuntimeEnv.GUIScale = scale;
		CairoFont.FontMeasuringContext = null;
		try {
			using var font = new CairoFont (16, "sans-serif").WithLineHeightMultiplier (1.25);
			var util = new TextDrawUtil ();
			const string text = "Inventory items and café\nSecond line";
			double width = font.GetTextExtents ("Inventory items").Width + 1;
			TextLine[] lines = util.Lineize (font, text, width, behavior);

			Assert.NotEmpty (lines);
			Assert.Equal (lines.Length, util.GetQuantityTextLines (font, text, width, behavior));
			Assert.Equal (lines.Length * util.GetLineHeight (font), util.GetMultilineTextHeight (font, text, width, behavior), 6);
			for (int i = 0; i < lines.Length; i++) {
				Assert.Equal (font.GetTextExtents (lines[i].Text).Width, lines[i].Bounds.Width, 6);
				Assert.Equal (font.GetFontExtents ().Ascent, lines[i].Bounds.Ascent, 6);
				Assert.Equal (util.GetLineHeight (font), lines[i].Bounds.Height, 6);
				Assert.Equal (i * util.GetLineHeight (font), lines[i].Bounds.Y, 6);
			}
		} finally {
			CairoFont.FontMeasuringContext = previousContext;
			RuntimeEnv.GUIScale = previousScale;
		}
	}

}
