using System;
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using System.Runtime.InteropServices;
using Cairo;
using Vintagestory.API.Config;
using Xunit;

/// <summary>
/// Text capture keeps Cairo font selection, measurement and glyph layout. Gray text remains
/// a fill of Cairo's hinted glyph outlines. Subpixel font and weight cases that pass the
/// local fixture matrix use Skia LCD glyph runs; unsupported faces materialize to Cairo
/// exactly. The local-AA visual-parity policy applies to Skia output (flat pixels within
/// one level, filtered edge
/// difference at most 96). Native materialization must reproduce Cairo's bytes exactly.
/// </summary>
[Collection ("Cairo materialization metrics")]
public sealed class CairoRecorderTextTests
{
	static CairoRecorderTextTests () { _ = CairoAPI.Version; BundledFonts.Register (); }

	internal static class BundledFonts
	{
		[DllImport ("libfontconfig.so.1")] static extern int FcConfigAppFontAddDir (IntPtr config, string dir);
		static bool registered;
		internal static bool Available { get; private set; }

		internal static void Register ()
		{
			if (registered) return;
			registered = true;
			string root = Environment.GetEnvironmentVariable ("VINTAGE_STORY");
			if (string.IsNullOrEmpty (root)) root = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.UserProfile), ".local", "share", "vintagestory");
			string fonts = Path.Combine (root, "assets", "game", "fonts");
			if (!Directory.Exists (fonts)) return;
			try { Available = FcConfigAppFontAddDir (IntPtr.Zero, fonts) != 0; } catch (DllNotFoundException) { }
		}
	}

	static Action<Context> Text (string family, FontWeight weight, double size, double x, double y, string text, Antialias antialias = Antialias.Gray)
	{
		return context => {
			context.SetSourceRGBA (.12, .18, .24, .9);
			context.Rectangle (0, 0, 400, 400);
			context.Fill ();
			context.SetFontSize (size);
			context.SelectFontFace (family, FontSlant.Normal, weight);
			using (var options = new FontOptions { Antialias = antialias }) context.FontOptions = options;
			context.SetSourceRGBA (.94, .93, .89, 1);
			context.MoveTo (x, y);
			context.ShowText (text);
			context.SetSourceRGBA (1, .6, .2, .8);
			context.ShowText (" +stack 64"); // continues from Cairo's advanced current point
		};
	}

	[Theory]
	[InlineData ("sans-serif", FontWeight.Normal, 14.0, 3.0, 20.0)]
	[InlineData ("sans-serif", FontWeight.Bold, 14.0, 3.3, 20.6)]
	[InlineData ("sans-serif", FontWeight.Normal, 19.2, 2.5, 24.25)]
	[InlineData ("sans-serif", FontWeight.Bold, 28.0, 1.0, 30.0)]
	[InlineData ("Montserrat", FontWeight.Normal, 14.0, 3.0, 20.0)]
	[InlineData ("Montserrat", FontWeight.Bold, 21.0, 2.75, 25.5)]
	[InlineData ("Lora", FontWeight.Normal, 16.0, 4.4, 22.2)]
	[InlineData ("Lora", FontWeight.Bold, 24.0, 3.0, 28.0)]
	[InlineData ("Almendra", FontWeight.Normal, 18.0, 3.6, 24.0)]
	public void GrayTextRecordsAndMatchesCairoWithinLocalAntialiasPolicy (string family, FontWeight weight, double size, double x, double y)
	{
		if (family != "sans-serif" && !BundledFonts.Available) return; // bundled fonts not installed on this host
		CairoRecorderParityTests.AssertLocalAaParity (240, 40, Text (family, weight, size, x, y, "Inventory 0123 Wg"));
	}

	[Fact]
	public void TextUnderScaleAndClipRecords ()
	{
		CairoRecorderParityTests.AssertLocalAaParity (160, 48, context => {
			context.Save ();
			context.Rectangle (4, 4, 90, 30);
			context.Clip ();
			context.Scale (1.5, 1.5);
			context.SetFontSize (12);
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal);
			using (var options = new FontOptions { Antialias = Antialias.Gray }) context.FontOptions = options;
			context.SetSourceRGBA (.2, .8, .3, .9);
			context.MoveTo (2.2, 16.3);
			context.ShowText ("Scaled & clipped text");
			context.Restore ();
		});
	}

	[Fact]
	public void CurrentPointAndMeasurementMatchCairoWithoutMaterializing ()
	{
		using var surface = new ImageSurface (Format.Argb32, 200, 40);
		var recorder = surface.BeginRecording ();
		using var native = new ImageSurface (Format.Argb32, 200, 40);
		using var context = new Context (surface);
		using var reference = new Context (native);
		foreach (Context c in new[] { context, reference }) {
			c.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal);
			c.SetFontSize (15);
			using (var options = new FontOptions { Antialias = Antialias.Gray }) c.FontOptions = options;
			c.MoveTo (2.5, 22.25);
		}
		Assert.Equal (reference.TextExtents ("Width").XAdvance, context.TextExtents ("Width").XAdvance);
		Assert.Equal (reference.FontExtents.Height, context.FontExtents.Height);
		context.ShowText ("Hello, world");
		reference.ShowText ("Hello, world");
		Assert.Equal (reference.CurrentPoint.X, context.CurrentPoint.X);
		Assert.Equal (reference.CurrentPoint.Y, context.CurrentPoint.Y);
		context.ShowText (string.Empty);
		reference.ShowText (string.Empty);
		Assert.Equal (reference.CurrentPoint.X, context.CurrentPoint.X);
		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
	}

	[Theory]
	[InlineData (Antialias.Default)]
	[InlineData (Antialias.None)]
	public void UnsupportedTextAntialiasMaterializesToCairo (Antialias antialias)
	{
		CairoRecorderParityTests.AssertMaterializesWith (240, 40, Text ("sans-serif", FontWeight.Normal, 14, 3, 20, "Fringes", antialias),
			"Recorder.ShowText.Unsupported:text-antialias-" + antialias);
	}

	[Theory]
	[InlineData ("sans-serif", FontWeight.Normal, 14.0, 3.3, 20.0)]
	[InlineData ("Montserrat", FontWeight.Normal, 14.0, 3.0, 20.0)]
	public void SubpixelTextUsesSkiaAndMatchesCairoWithinLocalAntialiasPolicy (string family, FontWeight weight, double size, double x, double y)
	{
		if (family != "sans-serif" && !BundledFonts.Available) return;
		CairoRecorderParityTests.AssertLocalAaParity (240, 40, Text (family, weight, size, x, y, "LCD Fringes 0123 Wg", Antialias.Subpixel));
	}

	[Theory]
	[InlineData ("Lora", FontWeight.Normal, 16.0, 4.4, 22.2)]
	[InlineData ("Lora", FontWeight.Bold, 17.0, 4.25, 24.25)]
	[InlineData ("Almendra", FontWeight.Normal, 18.0, 3.6, 24.0)]
	public void ExpandedSubpixelCandidatesMatchLocalAntialiasPolicy (string family, FontWeight weight, double size, double x, double y)
	{
		if (family != "sans-serif" && !BundledFonts.Available) return;
		CairoRecorderParityTests.AssertLocalAaParity (240, 40, Text (family, weight, size, x, y, "LCD Fringes 0123 Wg", Antialias.Subpixel));
	}

	[Theory]
	[InlineData ("sans-serif", FontWeight.Bold, 18.0, 2.75, 23.5)]
	public void UnsupportedSubpixelFontStylesKeepExactCairoFallback (string family, FontWeight weight, double size, double x, double y)
	{
		CairoRecorderParityTests.AssertMaterializesWith (240, 40, Text (family, weight, size, x, y, "LCD Fringes 0123 Wg", Antialias.Subpixel),
			"Recorder.ShowText.Unsupported:text-antialias-Subpixel");
	}

	[Fact]
	public void RotatedTextMaterializes ()
	{
		CairoRecorderParityTests.AssertMaterializesWith (60, 60, context => {
			context.SetFontSize (14);
			using (var options = new FontOptions { Antialias = Antialias.Gray }) context.FontOptions = options;
			context.Translate (30, 30);
			context.Rotate (.4);
			context.ShowText ("Tilt");
		}, "Recorder.ShowText.Unsupported:text-transform");
	}

	[Fact]
	public void SubpixelRequestIsPreservedAndDrawnBySkiaWhileRecordingIsEligible ()
	{
		bool requested = OptimumConfig.GuiGpuRenderEnabled;
		OptimumConfig.GuiGpuRenderEnabled = true;
		OptimumConfig.SetGuiGpuCertificationPassed (true, "v", "r", "4.6", 4, 6, 1, 0);
		try {
			Assert.True (OptimumConfig.GuiGpuRecordingEligible);
			using var surface = new ImageSurface (Format.Argb32, 240, 40);
			var recorder = surface.BeginRecording ();
			using var context = new Context (surface);
			Action<Context> draw = Text ("sans-serif", FontWeight.Normal, 14, 3.3, 20, "LCD bytes", Antialias.Subpixel);
			draw (context);
			Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
			Assert.Null (surface.FirstMaterializationTrigger);
			byte[] reference = CairoRecorderParityTests.RenderNative (240, 40, draw);
			byte[] actual = CairoRecorderParityTests.RenderSkia (surface, 240, 40);
			CairoRecorderParityTests.AssertLocalAa (reference, actual, 240, 40);
			Assert.True (OptimumConfig.GuiGpuRecordingEligible, "A successful Subpixel surface fallback must not disable GPU recording for other surfaces.");
		} finally {
			OptimumConfig.RevokeGuiGpuCertification ();
			OptimumConfig.GuiGpuRenderEnabled = requested;
		}
	}

	[Fact]
	public void VanillaSubpixelBytesAreUnchanged ()
	{
		// The wrapper must hand Cairo exactly the requested options: compare against direct
		// native calls with LCD options.
		byte[] wrapper = CairoRecorderParityTests.RenderNative (240, 40, Text ("sans-serif", FontWeight.Normal, 14, 3.3, 20, "LCD bytes", Antialias.Subpixel));
		using var direct = new ImageSurface (Format.Argb32, 240, 40);
		using (var context = new Context (direct)) {
			context.SetSourceRGBA (.12, .18, .24, .9);
			context.Rectangle (0, 0, 400, 400);
			context.Fill ();
			context.SetFontSize (14);
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal);
			using (var options = new FontOptions { Antialias = Antialias.Subpixel }) NativeMethods.cairo_set_font_options (context.Handle, options.Handle);
			context.SetSourceRGBA (.94, .93, .89, 1);
			context.MoveTo (3.3, 20);
			context.ShowText ("LCD bytes");
			context.SetSourceRGBA (1, .6, .2, .8);
			context.ShowText (" +stack 64");
		}
		Assert.Equal (direct.Data, wrapper);
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
