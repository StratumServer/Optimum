using System;
using System.IO;
using Path = System.IO.Path;
using Cairo;
using Cairo.Util;
using SkiaSharp;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoFileFontTests
{
	static string FontPath ()
	{
		string game = Environment.GetEnvironmentVariable ("VINTAGE_STORY");
		if (!string.IsNullOrEmpty (game)) {
			string path = Path.Combine (game, "assets", "game", "fonts", "Almendra-Regular.ttf");
			if (File.Exists (path)) return path;
		}
		for (var root = new DirectoryInfo (AppContext.BaseDirectory); root != null; root = root.Parent)
			foreach (string rid in new[] { "linux-x64", "win-x64" }) {
				string path = Path.Combine (root.FullName, ".vanilla", rid, "vintagestory", "assets", "game", "fonts", "Almendra-Regular.ttf");
				if (File.Exists (path)) return path;
			}
		throw new FileNotFoundException ("Bootstrap the game fonts before running file-font compatibility tests.");
	}

	[Theory]
	[InlineData (0)]
	[InlineData (2)]
	[InlineData (32)]
	[InlineData (65536)]
	public void FileFontRetainsItsDataAcrossContextAndScaledFontLifetimes (int loadOptions)
	{
		int deniedBefore = CairoNativeGuard.DeniedCount;
		string copy = Path.Combine (Path.GetTempPath (), "optimum-file-font-" + Guid.NewGuid ().ToString ("N") + ".ttf");
		File.Copy (FontPath (), copy);
		try {
			using var face = FreeTypeFontFace.Create (copy, loadOptions);
			Assert.Equal (FontType.FreeType, face.FontType);
			Assert.Equal (Status.Success, face.Status);
			using var surface = ImageSurface.CreateForRecordedGui (Format.Argb32, 320, 80);
			using var context = new Context (surface);
			context.SetContextFontFace (face);
			context.SetFontSize (28);
			using (var options = new FontOptions { Antialias = Antialias.Gray, HintStyle = HintStyle.Full }) context.FontOptions = options;
			context.Save ();
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal);
			context.Restore ();
			File.Delete (copy);
			face.Dispose ();
			using var retainedFace = context.GetContextFontFace ();
			Assert.Equal (FontType.FreeType, retainedFace.FontType);
			using var scaled = context.GetScaledFont ();
			Assert.Equal (FontType.FreeType, scaled.FontType);
			context.SetScaledFont (scaled);
			const string text = "File ffi Åg 15";
			Assert.True (context.TextExtents (text).XAdvance > 100);
			context.SetSourceRGBA (.9, .8, .7, 1);
			context.MoveTo (4, 32);
			context.ShowText (text);
			context.ShowGlyphs (new[] { new Glyph (15, 180, 32), new Glyph (42, 202, 32) });
			context.MoveTo (4, 68);
			context.TextPath (text);
			context.Fill ();
			using var raster = SKSurface.Create (new SKImageInfo (320, 80, SKColorType.Bgra8888, SKAlphaType.Premul));
			raster.Canvas.Clear (SKColors.Transparent);
			Assert.True (surface.TryDrawRecordedCommands (raster));
			using var image = raster.Snapshot ();
			using var bitmap = SKBitmap.FromImage (image);
			int visible = 0;
			foreach (SKColor pixel in bitmap.Pixels) if (pixel.Alpha > 0) visible++;
			Assert.True (visible > 100);
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			if (!CairoNativeGuard.NativeAllowed && CairoNativeGuard.LoadedLibcairoMappings () is string[] mappings) Assert.Empty (mappings);
		} finally { if (File.Exists (copy)) File.Delete (copy); }
	}

	[Theory]
	[InlineData (0)]
	[InlineData (2)]
	[InlineData (32)]
	[InlineData (65536)]
	public void FileFontSkiaAndRetainedNativeReplayMatchCairo (int loadOptions)
	{
		if (!CairoNativeGuard.NativeAllowed) return;
		byte[] bytes = File.ReadAllBytes (FontPath ());
		string copy = Path.Combine (Path.GetTempPath (), "optimum-file-font-oracle-" + Guid.NewGuid ().ToString ("N") + ".ttf");
		TextExtents? oracle = null;
		FontExtents? fontOracle = null;
		TextExtents? glyphOracle = null;
		try {
			CairoRecorderParityTests.AssertLocalAaParity (400, 90, context => {
				File.WriteAllBytes (copy, bytes);
				using var face = FreeTypeFontFace.Create (copy, loadOptions);
				context.SetContextFontFace (face);
				context.SetFontSize (28);
				using (var options = new FontOptions { Antialias = Antialias.Gray, HintStyle = HintStyle.Full }) context.FontOptions = options;
				File.Delete (copy);
				face.Dispose ();
				context.SetSourceRGBA (.9, .8, .7, 1);
				context.MoveTo (4, 33);
				var glyphs = new[] { new Glyph (15, 3, 5), new Glyph (42, 24, 6) };
				TextExtents measuredGlyph = context.GlyphExtents (glyphs);
				using (var scaled = context.GetScaledFont ()) {
					TextExtents scaledGlyph = scaled.GlyphExtents (glyphs);
					Assert.Equal (measuredGlyph.XAdvance, scaledGlyph.XAdvance, 4);
					Assert.Equal (measuredGlyph.Width, scaledGlyph.Width, 4);
				}
				if (glyphOracle is TextExtents expectedGlyph) {
					Assert.Equal (expectedGlyph.XBearing, measuredGlyph.XBearing, 4);
					Assert.Equal (expectedGlyph.YBearing, measuredGlyph.YBearing, 4);
					Assert.Equal (expectedGlyph.Width, measuredGlyph.Width, 4);
					Assert.Equal (expectedGlyph.Height, measuredGlyph.Height, 4);
					Assert.Equal (expectedGlyph.XAdvance, measuredGlyph.XAdvance, 4);
				} else glyphOracle = measuredGlyph;
				FontExtents measuredFont = context.FontExtents;
				if (fontOracle is FontExtents expectedFont) {
					Assert.Equal (expectedFont.Ascent, measuredFont.Ascent, 4);
					Assert.Equal (expectedFont.Descent, measuredFont.Descent, 4);
					Assert.Equal (expectedFont.Height, measuredFont.Height, 4);
					Assert.Equal (expectedFont.MaxXAdvance, measuredFont.MaxXAdvance, 4);
				} else fontOracle = measuredFont;
				var extents = context.TextExtents ("File ffi Åg 15");
				if (oracle is TextExtents expected) {
					Assert.Equal (expected.XBearing, extents.XBearing, 4);
					Assert.Equal (expected.YBearing, extents.YBearing, 4);
					Assert.Equal (expected.Width, extents.Width, 4);
					Assert.Equal (expected.Height, extents.Height, 4);
					Assert.Equal (expected.XAdvance, extents.XAdvance, 4);
				} else oracle = extents;
				context.ShowText ("File ffi Åg 15");
				context.ShowGlyphs (new[] { new Glyph (15, 180, 33), new Glyph (42, 202, 33) });
				context.MoveTo (4, 75);
				context.TextPath ("File ffi Åg 15");
				context.LineWidth = .75;
				context.StrokePreserve ();
				context.Fill ();
			});
		} finally { if (File.Exists (copy)) File.Delete (copy); }
	}

	[Fact]
	public void InvalidFontFilesKeepTheLegacyFactoryExceptionWithoutLoadingCairo ()
	{
		int deniedBefore = CairoNativeGuard.DeniedCount;
		string path = Path.Combine (Path.GetTempPath (), "optimum-invalid-font-" + Guid.NewGuid ().ToString ("N") + ".ttf");
		Assert.Equal (path, Assert.Throws<Exception> (() => FreeTypeFontFace.Create (path, 0)).Message);
		File.WriteAllText (path, "not a font");
		try { Assert.Equal (path, Assert.Throws<Exception> (() => FreeTypeFontFace.Create (path, 0)).Message); }
		finally { File.Delete (path); }
		Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
		if (!CairoNativeGuard.NativeAllowed && CairoNativeGuard.LoadedLibcairoMappings () is string[] mappings) Assert.Empty (mappings);
	}
}
