using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoUniformFontMatrixHintMetricsTests
{
	const double AdvanceTolerance = 1.0 / 256.0;
	const double ExistingMetricTolerance = 2.0;

	[StructLayout (LayoutKind.Sequential)]
	struct NativePathHeader
	{
		public Status Status;
		public IntPtr Data;
		public int Count;
	}

	readonly record struct Probe (TextExtents Text, FontExtents Font, TextExtents Glyph,
		PointD AfterShow, PointD AfterPath, Rectangle PathBounds, int PathDataCount);

	[Fact]
	public void BundledFontAdvancesMatchCairoAtSupportedUniformMatrices ()
	{
		CairoBundledFonts.Register ();
		bool oldRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		var mismatches = new List<string> ();
		try {
			foreach (object[] row in Cases ()) {
				string family = (string)row[0];
				double size = (double)row[1];
				string text = (string)row[2];
				HintMetrics hintMetrics = (HintMetrics)row[3];
				Probe? native = null;
				if (CairoNativeGuard.NativeAllowed) {
					Surface.ConfigureAutomaticRecording (false);
					native = Run (managed: false, family, size, text, hintMetrics);
				}
				Surface.ConfigureAutomaticRecording (true);
				Probe managed = Run (managed: true, family, size, text, hintMetrics);
				Assert.True (managed.Text.XAdvance > 0, $"{family} {size} {text}: text advance");
				AssertClose (managed.Text.XAdvance, managed.AfterShow.X - 20, $"{family} {size} {text}: ShowText");
				AssertClose (managed.Text.XAdvance, managed.AfterPath.X - 20, $"{family} {size} {text}: TextPath");
				if (text.Trim ().Length > 0) Assert.True (managed.PathDataCount > 0, $"{family} {size} {text}: text path is empty");

				if (native.HasValue) {
					Probe expected = native.Value;
					string scenario = $"{family} {size} '{text}' HintMetrics.{hintMetrics}";
					Check (mismatches, expected.Text.XAdvance, managed.Text.XAdvance, AdvanceTolerance, scenario + " text advance");
					Check (mismatches, expected.AfterShow.X, managed.AfterShow.X, AdvanceTolerance, scenario + " ShowText current x");
					Check (mismatches, expected.AfterShow.Y, managed.AfterShow.Y, AdvanceTolerance, scenario + " ShowText current y");
					Check (mismatches, expected.AfterPath.X, managed.AfterPath.X, AdvanceTolerance, scenario + " TextPath current x");
					Check (mismatches, expected.AfterPath.Y, managed.AfterPath.Y, AdvanceTolerance, scenario + " TextPath current y");
					CheckText (mismatches, expected.Text, managed.Text, ExistingMetricTolerance, scenario + " text extents");
					CheckFont (mismatches, expected.Font, managed.Font, ExistingMetricTolerance, scenario + " font extents");
					CheckText (mismatches, expected.Glyph, managed.Glyph, ExistingMetricTolerance, scenario + " glyph extents");
					Check (mismatches, expected.PathBounds.X, managed.PathBounds.X, ExistingMetricTolerance, scenario + " path bounds x");
					Check (mismatches, expected.PathBounds.Y, managed.PathBounds.Y, ExistingMetricTolerance, scenario + " path bounds y");
					Check (mismatches, expected.PathBounds.Width, managed.PathBounds.Width, ExistingMetricTolerance, scenario + " path bounds width");
					Check (mismatches, expected.PathBounds.Height, managed.PathBounds.Height, ExistingMetricTolerance, scenario + " path bounds height");
				}
			}
			Assert.True (mismatches.Count == 0, string.Join (Environment.NewLine, mismatches));
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
		} finally {
			Surface.ConfigureAutomaticRecording (oldRecording);
		}
	}

	static IEnumerable<object[]> Cases ()
	{
		string[] families = { "sans-serif", "Montserrat", "Lora" };
		double[] sizes = { 10, 13.25, 24 };
		string[] texts = { "Wg", "iiii", "AV", "ffi", " ", "café e\u0301" };
		foreach (string family in families)
			foreach (double size in sizes)
				foreach (string text in texts)
					foreach (HintMetrics hintMetrics in new[] { HintMetrics.Off, HintMetrics.On })
						yield return new object[] { family, size, text, hintMetrics };
	}

	static Probe Run (bool managed, string family, double size, string text, HintMetrics hintMetrics)
	{
		using var surface = managed
			? ImageSurface.CreateForRecordedGui (Format.Argb32, 256, 96)
			: new ImageSurface (Format.Argb32, 256, 96);
		using var context = new Context (surface);
		context.SelectFontFace (family, FontSlant.Normal, FontWeight.Normal);
		context.SetFontSize (10);
		using (var options = new FontOptions {
			Antialias = Antialias.Gray,
			HintStyle = HintStyle.Full,
			HintMetrics = hintMetrics
		}) context.FontOptions = options;
		context.FontMatrix = new Matrix (size, 0, 0, size, 0, 0);

		TextExtents extents = context.TextExtents (text);
		FontExtents fontExtents = context.FontExtents;
		Glyph[] glyphs = Glyphs (family, size, text);
		TextExtents glyphExtents;
		using (var scaled = context.GetScaledFont ()) glyphExtents = scaled.GlyphExtents (glyphs);
		context.MoveTo (20, 32);
		context.ShowText (text);
		PointD afterShow = context.CurrentPoint;
		context.NewPath ();
		context.MoveTo (20, 64);
		context.TextPath (text);
		PointD afterPath = context.CurrentPoint;
		Rectangle pathBounds = context.FillExtents ();
		int pathDataCount;
		using (Cairo.Path path = context.CopyPath ())
			pathDataCount = Marshal.PtrToStructure<NativePathHeader> (path.Handle).Count;
		return new Probe (extents, fontExtents, glyphExtents, afterShow, afterPath, pathBounds, pathDataCount);
	}

	static Glyph[] Glyphs (string family, double size, string text)
	{
		using var typeface = SKTypeface.FromFamilyName (family);
		Assert.NotNull (typeface);
		using var font = new SKFont (typeface, (float)size);
		ushort[] indices = font.GetGlyphs (text);
		var result = new Glyph[indices.Length];
		for (int i = 0; i < indices.Length; i++) result[i] = new Glyph (indices[i], 0, 0);
		return result;
	}

	static void AssertTextNear (TextExtents expected, TextExtents actual, string scenario)
	{
		AssertInRange (expected.XBearing, actual.XBearing, ExistingMetricTolerance, scenario + " x bearing");
		AssertInRange (expected.YBearing, actual.YBearing, ExistingMetricTolerance, scenario + " y bearing");
		AssertInRange (expected.Width, actual.Width, ExistingMetricTolerance, scenario + " width");
		AssertInRange (expected.Height, actual.Height, ExistingMetricTolerance, scenario + " height");
		AssertInRange (expected.XAdvance, actual.XAdvance, ExistingMetricTolerance, scenario + " x advance");
		AssertInRange (expected.YAdvance, actual.YAdvance, ExistingMetricTolerance, scenario + " y advance");
	}

	static void AssertFontNear (FontExtents expected, FontExtents actual, string scenario)
	{
		AssertInRange (expected.Ascent, actual.Ascent, ExistingMetricTolerance, scenario + " ascent");
		AssertInRange (expected.Descent, actual.Descent, ExistingMetricTolerance, scenario + " descent");
		AssertInRange (expected.Height, actual.Height, 3, scenario + " height");
		AssertInRange (expected.MaxXAdvance, actual.MaxXAdvance, ExistingMetricTolerance, scenario + " max x advance");
		AssertInRange (expected.MaxYAdvance, actual.MaxYAdvance, ExistingMetricTolerance, scenario + " max y advance");
	}

	static void AssertClose (double expected, double actual, string scenario)
		=> AssertInRange (expected, actual, AdvanceTolerance, scenario);

	static void AssertInRange (double expected, double actual, double tolerance, string scenario)
	{
		Assert.True (Math.Abs (expected - actual) <= tolerance + 1e-9 * Math.Abs (expected),
			$"{scenario}: native {expected:R}, managed {actual:R}, allowed {tolerance:R}");
	}

	static void Check (List<string> failures, double expected, double actual, double tolerance, string scenario)
	{
		if (Math.Abs (expected - actual) > tolerance + 1e-9 * Math.Abs (expected))
			failures.Add ($"{scenario}: native {expected:R}, managed {actual:R}, allowed {tolerance:R}");
	}

	static void CheckText (List<string> failures, TextExtents expected, TextExtents actual, double tolerance, string scenario)
	{
		Check (failures, expected.XBearing, actual.XBearing, tolerance, scenario + " x bearing");
		Check (failures, expected.YBearing, actual.YBearing, tolerance, scenario + " y bearing");
		Check (failures, expected.Width, actual.Width, tolerance, scenario + " width");
		Check (failures, expected.Height, actual.Height, tolerance, scenario + " height");
		Check (failures, expected.XAdvance, actual.XAdvance, tolerance, scenario + " x advance");
		Check (failures, expected.YAdvance, actual.YAdvance, tolerance, scenario + " y advance");
	}

	static void CheckFont (List<string> failures, FontExtents expected, FontExtents actual, double tolerance, string scenario)
	{
		Check (failures, expected.Ascent, actual.Ascent, tolerance, scenario + " ascent");
		Check (failures, expected.Descent, actual.Descent, tolerance, scenario + " descent");
		Check (failures, expected.Height, actual.Height, 3, scenario + " height");
		Check (failures, expected.MaxXAdvance, actual.MaxXAdvance, tolerance, scenario + " max x advance");
		Check (failures, expected.MaxYAdvance, actual.MaxYAdvance, tolerance, scenario + " max y advance");
	}
}
