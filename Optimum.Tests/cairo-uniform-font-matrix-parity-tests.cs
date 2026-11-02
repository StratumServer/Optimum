using System;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoUniformFontMatrixParityTests
{
	const int Width = 128;
	const int Height = 96;
	const double Tolerance = 1.0 / 256.0;
	const double ExistingTextMetricTolerance = 2.0;
	const double ExistingFontHeightTolerance = 3.0;

	[StructLayout (LayoutKind.Sequential)]
	struct NativePathHeader
	{
		public Status Status;
		public IntPtr Data;
		public int Count;
	}

	readonly record struct Snapshot (
		TextExtents Text, FontExtents Font, TextExtents Glyph,
		PointD AfterShow, PointD AfterTextPath, Rectangle TextPathBounds,
		int PathDataCount, Matrix FontMatrix);

	readonly record struct RunResult (Snapshot Baseline, Snapshot Baseline10, Snapshot Scaled, Snapshot Reset,
		Snapshot Restored, Snapshot SetScaled, byte[] Pixels);

	[Theory]
	[InlineData (HintMetrics.Off, false, 0)]
	[InlineData (HintMetrics.Off, true, 0)]
	[InlineData (HintMetrics.On, false, 0)]
	[InlineData (HintMetrics.On, true, 0)]
	[InlineData (HintMetrics.Off, false, 10)]
	[InlineData (HintMetrics.Off, true, 10)]
	[InlineData (HintMetrics.On, false, 10)]
	[InlineData (HintMetrics.On, true, 10)]
	[InlineData (HintMetrics.Off, false, 7)]
	[InlineData (HintMetrics.Off, true, 7)]
	[InlineData (HintMetrics.On, false, 7)]
	[InlineData (HintMetrics.On, true, 7)]
	public void PositiveUniformFontMatrixMatchesNativeAndRecordedFontRoutes (HintMetrics hintMetrics, bool warm, double initialSize)
	{
		bool oldRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			RunResult? native = null;
			if (CairoNativeGuard.NativeAllowed) {
				Surface.ConfigureAutomaticRecording (false);
				native = Run (managed: false, hintMetrics, warm, initialSize);
			}

			Surface.ConfigureAutomaticRecording (true);
			RunResult managed = Run (managed: true, hintMetrics, warm, initialSize);
			AssertUniformScaleBehavior (managed);
			if (native.HasValue) {
				AssertSnapshotsClose (native.Value.Baseline, managed.Baseline, "baseline");
				AssertSnapshotsClose (native.Value.Baseline10, managed.Baseline10, "size10 baseline");
				AssertSnapshotsClose (native.Value.Scaled, managed.Scaled, "FontMatrix(24)");
				AssertCurrentPointsClose (native.Value.Scaled, managed.Scaled, "FontMatrix(24)");
				AssertSnapshotsClose (native.Value.Reset, managed.Reset, "SetFontSize(10) reset");
				AssertCurrentPointsClose (native.Value.Reset, managed.Reset, "SetFontSize(10) reset");
				AssertSnapshotsClose (native.Value.Restored, managed.Restored, "Restore");
				AssertCurrentPointsClose (native.Value.Restored, managed.Restored, "Restore");
				AssertSnapshotsClose (native.Value.SetScaled, managed.SetScaled, "SetScaledFont");
				AssertCurrentPointsClose (native.Value.SetScaled, managed.SetScaled, "SetScaledFont");
				CairoRecorderParityTests.AssertLocalAa (native.Value.Pixels, managed.Pixels, Width, Height);
			} else {
				int litPixels = 0;
				for (int i = 0; i < managed.Pixels.Length; i += 4)
					if (managed.Pixels[i] > 0 && managed.Pixels[i + 1] > 0 && managed.Pixels[i + 2] > 0) litPixels++;
				Assert.True (litPixels > 50, $"Recorded text rendered only {litPixels} lit pixels.");
			}

			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			if (!CairoNativeGuard.NativeAllowed)
				Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
		} finally {
			Surface.ConfigureAutomaticRecording (oldRecording);
		}
	}

	static RunResult Run (bool managed, HintMetrics hintMetrics, bool warm, double initialSize)
	{
		using var surface = managed
			? ImageSurface.CreateForRecordedGui (Format.Argb32, Width, Height)
			: new ImageSurface (Format.Argb32, Width, Height);
		using var context = new Context (surface);
		context.SetSourceRGB (0, 0, 0);
		context.Paint ();
		context.SelectFontFace ("DejaVu Sans", FontSlant.Normal, FontWeight.Normal);
		if (initialSize > 0) context.SetFontSize (initialSize);
		using (var options = new FontOptions {
			Antialias = Antialias.Gray,
			HintStyle = HintStyle.Full,
			HintMetrics = hintMetrics
		}) context.FontOptions = options;

		Snapshot baseline = CaptureAndDraw (context);
		context.Save ();
		context.SetFontSize (10);
		Snapshot baseline10 = CaptureAndDraw (context);
		context.Restore ();
		context.FontMatrix = new Matrix (24, 0, 0, 24, 0, 0);
		if (warm) {
			context.TextExtents ("Wg");
			context.MoveTo (20, 60);
			context.ShowText ("Wg");
			context.NewPath ();
		}
		Snapshot scaled = CaptureAndDraw (context);
		context.Save ();
		context.SetFontSize (10);
		Snapshot reset = CaptureAndDraw (context);
		context.Restore ();
		Snapshot restored = CaptureAndDraw (context);

		using (var face = context.GetContextFontFace ())
		using (var options = context.FontOptions)
		using (var scaledFont = new ScaledFont (face, new Matrix (24, 0, 0, 24, 0, 0), new Matrix (), options)) {
			context.SetScaledFont (scaledFont);
		}
		Snapshot setScaled = CaptureAndDraw (context);

		byte[] pixels = managed
			? CairoRecorderParityTests.RenderSkia (surface, Width, Height)
			: surface.Data;
		return new RunResult (baseline, baseline10, scaled, reset, restored, setScaled, pixels);
	}

	static Snapshot CaptureAndDraw (Context context)
	{
		TextExtents text = context.TextExtents ("Wg");
		FontExtents font = context.FontExtents;
		TextExtents glyph;
		using (var scaled = context.GetScaledFont ()) {
			using var typeface = SKTypeface.FromFamilyName ("DejaVu Sans");
			Assert.NotNull (typeface);
			using var skFont = new SKFont (typeface, (float)scaled.FontMatrix.Xx);
			ushort[] indices = skFont.GetGlyphs ("Wg");
			var glyphs = new[] { new Glyph (indices[0], 0, 0), new Glyph (indices[1], 0, 0) };
			glyph = scaled.GlyphExtents (glyphs);
		}

		context.SetSourceRGB (1, 1, 1);
		context.MoveTo (20, 32);
		context.ShowText ("Wg");
		PointD afterShow = context.CurrentPoint;
		context.NewPath ();
		context.MoveTo (20, 64);
		context.TextPath ("Wg");
		PointD afterTextPath = context.CurrentPoint;
		Rectangle pathBounds = context.FillExtents ();
		int pathDataCount;
		using (Cairo.Path path = context.CopyPath ())
			pathDataCount = Marshal.PtrToStructure<NativePathHeader> (path.Handle).Count;
		context.Fill ();
		return new Snapshot (text, font, glyph, afterShow, afterTextPath, pathBounds, pathDataCount, context.FontMatrix);
	}

	static void AssertUniformScaleBehavior (RunResult run)
	{
		AssertClose (24, run.Scaled.FontMatrix.Xx, "matrix xx at 24");
		AssertClose (24, run.Scaled.FontMatrix.Yy, "matrix yy at 24");
		AssertClose (0, run.Scaled.FontMatrix.Xy, "matrix xy at 24");
		AssertClose (0, run.Scaled.FontMatrix.Yx, "matrix yx at 24");
		Assert.True (run.Scaled.Text.XAdvance > run.Baseline10.Text.XAdvance * 2.25,
			$"24 point matrix should scale size10 advance: {run.Baseline10.Text.XAdvance} -> {run.Scaled.Text.XAdvance}");
		AssertClose (run.Scaled.Text.XAdvance, run.Scaled.AfterShow.X - 20, "ShowText advance");
		AssertClose (run.Scaled.Text.XAdvance, run.Scaled.AfterTextPath.X - 20, "TextPath advance");
		Assert.True (run.Scaled.PathDataCount > 0, "TextPath should append glyph outlines.");

		AssertClose (10, run.Reset.FontMatrix.Xx, "SetFontSize reset xx");
		AssertClose (10, run.Reset.FontMatrix.Yy, "SetFontSize reset yy");
		AssertSnapshotsClose (run.Baseline10, run.Reset, "SetFontSize returns to size10");
		AssertCurrentPointsClose (run.Baseline10, run.Reset, "SetFontSize returns to size10");
		AssertSnapshotsClose (run.Scaled, run.Restored, "Save/Restore returns to scale24");
		AssertCurrentPointsClose (run.Scaled, run.Restored, "Save/Restore returns to scale24");
		AssertSnapshotsClose (run.Scaled, run.SetScaled, "SetScaledFont scale24");
		AssertCurrentPointsClose (run.Scaled, run.SetScaled, "SetScaledFont scale24");
	}

	static void AssertSnapshotsClose (Snapshot expected, Snapshot actual, string scenario)
	{
		AssertNativeTextClose (expected.Text, actual.Text, scenario + " text");
		AssertNativeFontClose (expected.Font, actual.Font, scenario + " font");
		AssertNativeTextClose (expected.Glyph, actual.Glyph, scenario + " glyph");
		Assert.InRange (Math.Abs (expected.TextPathBounds.X - actual.TextPathBounds.X), 0, ExistingTextMetricTolerance);
		Assert.InRange (Math.Abs (expected.TextPathBounds.Y - actual.TextPathBounds.Y), 0, ExistingTextMetricTolerance);
		Assert.InRange (Math.Abs (expected.TextPathBounds.Width - actual.TextPathBounds.Width), 0, ExistingTextMetricTolerance);
		Assert.InRange (Math.Abs (expected.TextPathBounds.Height - actual.TextPathBounds.Height), 0, ExistingTextMetricTolerance);
		AssertClose (expected.FontMatrix.Xx, actual.FontMatrix.Xx, scenario + " matrix xx");
		AssertClose (expected.FontMatrix.Yy, actual.FontMatrix.Yy, scenario + " matrix yy");
	}

	static void AssertCurrentPointsClose (Snapshot expected, Snapshot actual, string scenario)
	{
		AssertClose (expected.AfterShow.X, actual.AfterShow.X, scenario + " show x");
		AssertClose (expected.AfterShow.Y, actual.AfterShow.Y, scenario + " show y");
		AssertClose (expected.AfterTextPath.X, actual.AfterTextPath.X, scenario + " path x");
		AssertClose (expected.AfterTextPath.Y, actual.AfterTextPath.Y, scenario + " path y");
	}

	static void AssertNativeTextClose (TextExtents expected, TextExtents actual, string scenario)
	{
		Assert.InRange (Math.Abs (expected.XBearing - actual.XBearing), 0, ExistingTextMetricTolerance);
		Assert.InRange (Math.Abs (expected.YBearing - actual.YBearing), 0, ExistingTextMetricTolerance);
		Assert.InRange (Math.Abs (expected.Width - actual.Width), 0, ExistingTextMetricTolerance);
		Assert.InRange (Math.Abs (expected.Height - actual.Height), 0, ExistingTextMetricTolerance);
		Assert.InRange (Math.Abs (expected.XAdvance - actual.XAdvance), 0, ExistingTextMetricTolerance);
		Assert.InRange (Math.Abs (expected.YAdvance - actual.YAdvance), 0, ExistingTextMetricTolerance);
	}

	static void AssertNativeFontClose (FontExtents expected, FontExtents actual, string scenario)
	{
		Assert.InRange (Math.Abs (expected.Ascent - actual.Ascent), 0, ExistingTextMetricTolerance);
		Assert.InRange (Math.Abs (expected.Descent - actual.Descent), 0, ExistingTextMetricTolerance);
		Assert.InRange (Math.Abs (expected.Height - actual.Height), 0, ExistingFontHeightTolerance);
		Assert.InRange (Math.Abs (expected.MaxXAdvance - actual.MaxXAdvance), 0, ExistingTextMetricTolerance);
		Assert.InRange (Math.Abs (expected.MaxYAdvance - actual.MaxYAdvance), 0, ExistingTextMetricTolerance);
	}

	static void AssertTextClose (TextExtents expected, TextExtents actual, string scenario)
	{
		AssertClose (expected.XBearing, actual.XBearing, scenario + " x bearing");
		AssertClose (expected.YBearing, actual.YBearing, scenario + " y bearing");
		AssertClose (expected.Width, actual.Width, scenario + " width");
		AssertClose (expected.Height, actual.Height, scenario + " height");
		AssertClose (expected.XAdvance, actual.XAdvance, scenario + " x advance");
		AssertClose (expected.YAdvance, actual.YAdvance, scenario + " y advance");
	}

	static void AssertFontClose (FontExtents expected, FontExtents actual, string scenario)
	{
		AssertClose (expected.Ascent, actual.Ascent, scenario + " ascent");
		AssertClose (expected.Descent, actual.Descent, scenario + " descent");
		AssertClose (expected.Height, actual.Height, scenario + " height");
		AssertClose (expected.MaxXAdvance, actual.MaxXAdvance, scenario + " max x advance");
		AssertClose (expected.MaxYAdvance, actual.MaxYAdvance, scenario + " max y advance");
	}

	static void AssertClose (double expected, double actual, string what)
	{
		Assert.True (Math.Abs (expected - actual) <= Tolerance + 1e-9 * Math.Abs (expected),
			$"{what}: expected {expected:R}, actual {actual:R}");
	}
}
