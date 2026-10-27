using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoAffineFontMatrixTests
{
	const double AdvanceTolerance = 1.0 / 256.0;
	const double MetricTolerance = 2.0;

	[StructLayout (LayoutKind.Sequential)]
	struct NativePathHeader
	{
		public Status Status;
		public IntPtr Data;
		public int Count;
	}

	readonly record struct Probe (
		TextExtents Text, FontExtents Font, TextExtents Glyph,
		PointD AfterShow, PointD AfterPath, Rectangle PathBounds,
		int PathDataCount, Status Status);

	[Theory]
	[InlineData ("sans-serif", 20.0, 10.0, 0.0, 0.0, HintMetrics.Off)]
	[InlineData ("sans-serif", 20.0, 10.0, 0.0, 0.0, HintMetrics.On)]
	[InlineData ("sans-serif", 12.0, 24.0, 0.0, 0.0, HintMetrics.Off)]
	[InlineData ("sans-serif", 12.0, 24.0, 0.0, 0.0, HintMetrics.On)]
	[InlineData ("Montserrat", 24.0, 12.0, 10.0, 5.0, HintMetrics.Off)]
	[InlineData ("Montserrat", 24.0, 12.0, 10.0, 5.0, HintMetrics.On)]
	[InlineData ("Lora", 15.0, 30.0, 0.0, 0.0, HintMetrics.Off)]
	[InlineData ("Lora", 15.0, 30.0, 0.0, 0.0, HintMetrics.On)]
	[InlineData ("sans-serif", 24.0, 12.0, -5.0, 15.0, HintMetrics.On)]
	public void NonUniformFontMatrixMatchesCairoNativeAndDeny (
		string family, double xx, double yy, double x0, double y0, HintMetrics hintMetrics)
	{
		CairoBundledFonts.Register ();
		bool oldRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Probe? native = null;
			if (CairoNativeGuard.NativeAllowed) {
				Surface.ConfigureAutomaticRecording (false);
				native = Run (managed: false, family, xx, yy, x0, y0, "Wg", hintMetrics);
			}

			Surface.ConfigureAutomaticRecording (true);
			Probe managed = Run (managed: true, family, xx, yy, x0, y0, "Wg", hintMetrics);

			Assert.Equal (Status.Success, managed.Status);
			Assert.True (managed.Text.XAdvance > 0, "managed advance > 0");
			Assert.True (managed.Text.Width > 0, "managed width > 0");
			Assert.True (managed.Text.Height > 0, "managed height > 0");
			AssertClose (managed.Text.XAdvance, managed.AfterShow.X - 20, "ShowText advance");
			AssertClose (managed.Text.XAdvance, managed.AfterPath.X - 20, "TextPath advance");

			if (native.HasValue) {
				Probe expected = native.Value;
				string scenario = $"{family} ({xx},{yy},{x0},{y0}) HintMetrics.{hintMetrics}";
				AssertClose (expected.Text.XAdvance, managed.Text.XAdvance, scenario + " advance");
				AssertClose (expected.AfterShow.X, managed.AfterShow.X, scenario + " show x");
				AssertClose (expected.AfterShow.Y, managed.AfterShow.Y, scenario + " show y");
				AssertClose (expected.AfterPath.X, managed.AfterPath.X, scenario + " path x");
				AssertClose (expected.AfterPath.Y, managed.AfterPath.Y, scenario + " path y");

				Assert.InRange (Math.Abs (expected.Text.XBearing - managed.Text.XBearing), 0, MetricTolerance);
				Assert.InRange (Math.Abs (expected.Text.YBearing - managed.Text.YBearing), 0, MetricTolerance);
				Assert.InRange (Math.Abs (expected.Text.Width - managed.Text.Width), 0, MetricTolerance);
				Assert.InRange (Math.Abs (expected.Text.Height - managed.Text.Height), 0, MetricTolerance);

				Assert.InRange (Math.Abs (expected.Font.Ascent - managed.Font.Ascent), 0, MetricTolerance);
				Assert.InRange (Math.Abs (expected.Font.Descent - managed.Font.Descent), 0, MetricTolerance);
				Assert.InRange (Math.Abs (expected.Font.Height - managed.Font.Height), 0, MetricTolerance + 1);
			}

			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
		} finally {
			Surface.ConfigureAutomaticRecording (oldRecording);
		}
	}

	[Fact]
	public void FontMatrixTranslationShiftsExtentsAndPathWithoutAffectingAdvance ()
	{
		CairoBundledFonts.Register ();
		bool oldRecording = Surface.AutomaticRecordingEnabled;
		try {
			Surface.ConfigureAutomaticRecording (true);
			Probe baseline = Run (managed: true, "sans-serif", 16, 16, 0, 0, "AV", HintMetrics.Off);
			Probe translated = Run (managed: true, "sans-serif", 16, 16, 25, 15, "AV", HintMetrics.Off);

			Assert.Equal (Status.Success, baseline.Status);
			Assert.Equal (Status.Success, translated.Status);

			// Advance and extents size must be identical
			AssertClose (baseline.Text.XAdvance, translated.Text.XAdvance, "advance must be identical");
			AssertClose (baseline.Text.Width, translated.Text.Width, "width must be identical");
			AssertClose (baseline.Text.Height, translated.Text.Height, "height must be identical");

			// Bearings must shift by exactly (x0, y0)
			AssertClose (baseline.Text.XBearing + 25, translated.Text.XBearing, "x bearing shift");
			AssertClose (baseline.Text.YBearing + 15, translated.Text.YBearing, "y bearing shift");

			// Path bounds must shift by (x0, y0)
			AssertClose (baseline.PathBounds.X + 25, translated.PathBounds.X, "path bounds x shift");
			AssertClose (baseline.PathBounds.Y + 15, translated.PathBounds.Y, "path bounds y shift");

			// AfterShow and AfterPath end positions must NOT include (x0, y0)
			AssertClose (baseline.AfterShow.X, translated.AfterShow.X, "after show x");
			AssertClose (baseline.AfterShow.Y, translated.AfterShow.Y, "after show y");
			AssertClose (baseline.AfterPath.X, translated.AfterPath.X, "after path x");
			AssertClose (baseline.AfterPath.Y, translated.AfterPath.Y, "after path y");
		} finally {
			Surface.ConfigureAutomaticRecording (oldRecording);
		}
	}

	[Fact]
	public void ZeroFontMatrixProducesZeroExtentsAndNoOps ()
	{
		CairoBundledFonts.Register ();
		bool oldRecording = Surface.AutomaticRecordingEnabled;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = ImageSurface.CreateForRecordedGui (Format.Argb32, 100, 100);
			using var cr = new Context (surface);
			cr.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal);
			cr.FontMatrix = new Matrix (0, 0, 0, 0, 10, 20);

			TextExtents textExt = cr.TextExtents ("Test");
			Assert.Equal (0, textExt.Width);
			Assert.Equal (0, textExt.Height);
			Assert.Equal (0, textExt.XAdvance);
			Assert.Equal (0, textExt.YAdvance);

			FontExtents fontExt = cr.FontExtents;
			Assert.Equal (0, fontExt.Ascent);
			Assert.Equal (0, fontExt.Descent);
			Assert.Equal (0, fontExt.Height);

			cr.MoveTo (15, 25);
			cr.ShowText ("Test");
			PointD pt = cr.CurrentPoint;
			Assert.Equal (15, pt.X);
			Assert.Equal (25, pt.Y);

			cr.TextPath ("Test");
			pt = cr.CurrentPoint;
			Assert.Equal (15, pt.X);
			Assert.Equal (25, pt.Y);

			Assert.Equal (Status.Success, cr.Status);

			using var sf = cr.GetScaledFont ();
			Assert.Equal (Status.Success, sf.Status);
			Assert.Equal (0, sf.FontExtents.Height);
		} finally {
			Surface.ConfigureAutomaticRecording (oldRecording);
		}
	}

	[Theory]
	[InlineData (1, 0, 0, 0)]
	[InlineData (0, 0, 0, 1)]
	[InlineData (2, 4, 1, 2)]
	[InlineData (3, 3, 3, 3)]
	public void SingularFontMatrixLatchesInvalidMatrix (double xx, double yx, double xy, double yy)
	{
		CairoBundledFonts.Register ();
		bool oldRecording = Surface.AutomaticRecordingEnabled;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = ImageSurface.CreateForRecordedGui (Format.Argb32, 100, 100);
			using var cr = new Context (surface);
			cr.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal);
			cr.FontMatrix = new Matrix (xx, yx, xy, yy, 0, 0);

			Assert.Equal (Status.Success, cr.Status);

			// Reading ScaledFont yields an in-error scaled font
			using var sf = cr.GetScaledFont ();
			Assert.Equal (Status.InvalidMatrix, sf.Status);

			// Calling TextExtents on context with singular matrix latches Status.InvalidMatrix
			TextExtents textExt = cr.TextExtents ("Test");
			Assert.Equal (Status.InvalidMatrix, cr.Status);
			Assert.Equal (0, textExt.XAdvance);

			// Calling constructor directly on singular matrix yields Status.InvalidMatrix
			using var face = new FontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal);
			using var sfDirect = new ScaledFont (face, new Matrix (xx, yx, xy, yy, 0, 0), new Matrix (), new FontOptions ());
			Assert.Equal (Status.InvalidMatrix, sfDirect.Status);
			Assert.Equal (0, sfDirect.FontExtents.Height);
		} finally {
			Surface.ConfigureAutomaticRecording (oldRecording);
		}
	}

	[Fact]
	public void NonFiniteFontMatrixLatchesInvalidMatrix ()
	{
		CairoBundledFonts.Register ();
		bool oldRecording = Surface.AutomaticRecordingEnabled;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = ImageSurface.CreateForRecordedGui (Format.Argb32, 100, 100);
			using var cr = new Context (surface);
			cr.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal);
			cr.FontMatrix = new Matrix (double.NaN, 0, 0, 10, 0, 0);

			_ = cr.TextExtents ("Test");
			Assert.Equal (Status.InvalidMatrix, cr.Status);

			using var cr2 = new Context (surface);
			cr2.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal);
			cr2.FontMatrix = new Matrix (double.PositiveInfinity, 0, 0, 10, 0, 0);
			_ = cr2.FontExtents;
			Assert.Equal (Status.InvalidMatrix, cr2.Status);
		} finally {
			Surface.ConfigureAutomaticRecording (oldRecording);
		}
	}

	[Fact]
	public void NegativeScaleXProducesReflectedExtentsMatchingCairo ()
	{
		CairoBundledFonts.Register ();
		bool oldRecording = Surface.AutomaticRecordingEnabled;
		try {
			Probe? native = null;
			if (CairoNativeGuard.NativeAllowed) {
				Surface.ConfigureAutomaticRecording (false);
				native = Run (managed: false, "sans-serif", -14.0, 14.0, 0.0, 0.0, "Hello", HintMetrics.Off);
			}

			Surface.ConfigureAutomaticRecording (true);
			Probe managed = Run (managed: true, "sans-serif", -14.0, 14.0, 0.0, 0.0, "Hello", HintMetrics.Off);

			Assert.Equal (Status.Success, managed.Status);
			Assert.True (managed.Text.XAdvance < 0, "reflected advance is negative");
			Assert.True (managed.Text.XBearing < 0, "reflected bearing is negative");

			if (native.HasValue) {
				AssertClose (native.Value.Text.XAdvance, managed.Text.XAdvance, "negative advance");
				Assert.InRange (Math.Abs (native.Value.Text.XBearing - managed.Text.XBearing), 0, MetricTolerance);
				Assert.InRange (Math.Abs (native.Value.Text.Width - managed.Text.Width), 0, MetricTolerance);
				Assert.InRange (Math.Abs (native.Value.Text.Height - managed.Text.Height), 0, MetricTolerance);
			}
		} finally {
			Surface.ConfigureAutomaticRecording (oldRecording);
		}
	}

	static Probe Run (bool managed, string family, double xx, double yy, double x0, double y0, string text, HintMetrics hintMetrics)
	{
		using var surface = managed
			? ImageSurface.CreateForRecordedGui (Format.Argb32, 200, 120)
			: new ImageSurface (Format.Argb32, 200, 120);
		using var cr = new Context (surface);
		cr.SelectFontFace (family, FontSlant.Normal, FontWeight.Normal);
		using var options = new FontOptions ();
		options.Antialias = Antialias.Gray;
		options.HintStyle = HintStyle.Full;
		options.HintMetrics = hintMetrics;
		cr.FontOptions = options;
		cr.FontMatrix = new Matrix (xx, 0, 0, yy, x0, y0);

		TextExtents textExtents = cr.TextExtents (text);
		FontExtents fontExtents = cr.FontExtents;
		TextExtents glyphExtents = cr.GlyphExtents (new[] {
			new Glyph (1, 0, 0),
			new Glyph (2, 10, 0)
		});

		cr.MoveTo (20, 40);
		cr.ShowText (text);
		PointD afterShow = cr.CurrentPoint;

		cr.NewPath ();
		cr.MoveTo (20, 40);
		cr.TextPath (text);
		PointD afterPath = cr.CurrentPoint;

		Rectangle pathBounds = cr.FillExtents ();
		int pathDataCount = 0;
		using (Path path = cr.CopyPath ()) {
			if (path != null && path.Handle != IntPtr.Zero) {
				var header = Marshal.PtrToStructure<NativePathHeader> (path.Handle);
				pathDataCount = header.Count;
			}
		}

		return new Probe (textExtents, fontExtents, glyphExtents, afterShow, afterPath, pathBounds, pathDataCount, cr.Status);
	}

	static void AssertClose (double expected, double actual, string what)
	{
		Assert.True (Math.Abs (expected - actual) <= AdvanceTolerance + 1e-9 * Math.Abs (expected),
			$"{what}: expected {expected:R}, actual {actual:R}");
	}
}
