using System;
using System.Linq;
using Cairo;
using SkiaSharp;
using Vintagestory.API.Config;
using Vintagestory.API.Client;
using Xunit;

[Collection("GUI metrics")]
public sealed class CairoFontSkiaMetricsTests
{
	static CairoFontSkiaMetricsTests () { _ = CairoAPI.Version; CairoBundledFonts.Register (); }

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
	// Unicode text is measured as the glyphs the font maps, and the layout helper agrees with a recording
	// context that measures through the same engine.
	[Theory]
	[InlineData ("A\U0001f600B")]
	[InlineData ("\U0001d11e")]
	[InlineData ("café e\u0301")]
	[InlineData ("  ")]
	public void TextExtentsMeasureTheSameGlyphsAsARecordingContext (string text)
	{
		float previousScale = RuntimeEnv.GUIScale;
		RuntimeEnv.GUIScale = 1;
		try {
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			surface.BeginRecording ();
			using var recording = new Context (surface);
			using var font = new CairoFont (18, "sans-serif");
			font.SetupContext (recording);
			TextExtents expected = recording.TextExtents (text);
			TextExtents actual = font.GetTextExtents (text);

			Assert.Equal (expected.XAdvance, actual.XAdvance, 6);
			Assert.Equal (expected.XBearing, actual.XBearing, 6);
			Assert.Equal (expected.YBearing, actual.YBearing, 6);
			Assert.Equal (expected.Width, actual.Width, 6);
			Assert.Equal (expected.Height, actual.Height, 6);
			Assert.True (actual.XAdvance >= 0);
			if (text.Trim ().Length == 0) { Assert.Equal (0, actual.Width); Assert.Equal (0, actual.Height); }
		} finally {
			RuntimeEnv.GUIScale = previousScale;
		}
	}

	[Theory]
	[InlineData (1f, EnumLinebreakBehavior.AfterWord)]
	[InlineData (1.5f, EnumLinebreakBehavior.AfterWord)]
	[InlineData (2f, EnumLinebreakBehavior.AfterWord)]
	[InlineData (1f, EnumLinebreakBehavior.AfterCharacter)]
	[InlineData (1.5f, EnumLinebreakBehavior.AfterCharacter)]
	[InlineData (2f, EnumLinebreakBehavior.AfterCharacter)]
	[InlineData (1f, EnumLinebreakBehavior.None)]
	[InlineData (1.5f, EnumLinebreakBehavior.None)]
	[InlineData (2f, EnumLinebreakBehavior.None)]
	public void FontLayoutUsesSkiaMetricsWithoutTheSharedCairoContext (float scale, EnumLinebreakBehavior behavior)
	{
		float previousScale = RuntimeEnv.GUIScale;
		Context previousContext = CairoFont.FontMeasuringContext;
		RuntimeEnv.GUIScale = scale;
		CairoFont.FontMeasuringContext = null;
		try {
			using var font = new CairoFont (16, "sans-serif").WithLineHeightMultiplier (1.25);
			using var nativeSurface = new ImageSurface (Format.Argb32, 1, 1);
			using var nativeContext = new Context (nativeSurface);
			font.SetupContext (nativeContext);
			var util = new TextDrawUtil ();
			const string text = "Inventory items and café e\u0301\nSecond line";
			double width = font.GetTextExtents ("Inventory items").Width + 1;
			TextLine[] lines = util.Lineize (font, text, width, behavior);
			TextLine[] nativeLines = new TextDrawUtil ().Lineize (nativeContext, text, width, 1.25f, behavior);

			Assert.NotEmpty (lines);
			TextExtents skiaPrefix = font.GetTextExtents ("Inventory items");
			TextExtents cairoPrefix = nativeContext.TextExtents ("Inventory items");
			string nativeText = string.Join ("|", nativeLines.Select (line => line.Text));
			string skiaText = string.Join ("|", lines.Select (line => line.Text));
			Assert.True (nativeLines.Select (line => line.Text).SequenceEqual (lines.Select (line => line.Text)),
				$"scale={scale}, behavior={behavior}, width={width}, Skia width/advance={skiaPrefix.Width}/{skiaPrefix.XAdvance}, Cairo width/advance={cairoPrefix.Width}/{cairoPrefix.XAdvance}, native=[{nativeText}], skia=[{skiaText}]");
			Assert.Equal (lines.Length, util.GetQuantityTextLines (font, text, width, behavior));
			Assert.Equal (lines.Length * util.GetLineHeight (font), util.GetMultilineTextHeight (font, text, width, behavior), 6);
			for (int i = 0; i < lines.Length; i++) {
				Assert.Equal (nativeLines[i].LeftSpace, lines[i].LeftSpace, 6);
				Assert.Equal (nativeLines[i].RightSpace, lines[i].RightSpace, 6);
				Assert.Equal (nativeLines[i].NextOffsetX, lines[i].NextOffsetX, 6);
				Assert.Equal (nativeLines[i].Bounds.X, lines[i].Bounds.X, 6);
				Assert.InRange (Math.Abs (nativeLines[i].Bounds.Y - lines[i].Bounds.Y), 0, i * 0.6);
				Assert.Equal (nativeLines[i].Bounds.Width, lines[i].Bounds.Width, 6);
				Assert.InRange (Math.Abs (nativeLines[i].Bounds.Height - lines[i].Bounds.Height), 0, 1.0);
				Assert.InRange (Math.Abs (nativeLines[i].Bounds.Ascent - lines[i].Bounds.Ascent), 0, 1.0);
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
