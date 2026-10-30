using System;
using Cairo;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoRecorderBlendModeTests
{
	[Fact]
	public void SmallTextCoverageRetainsInkSizedImagesNotWholeSurfaces () => AssertInkSizedTextCoverage (false);

	[Fact]
	public void SmallHardClippedTextCoverageRetainsInkSizedImagesNotWholeSurfaces () => AssertInkSizedTextCoverage (true);

	static void AssertInkSizedTextCoverage (bool hardClip)
	{
		using var surface = new ImageSurface (Format.Argb32, 2048, 1024);
		surface.BeginRecording ();
		using var context = new Context (surface);
		if (hardClip) {
			context.Antialias = Antialias.None;
			context.Arc (1020, 400, 60, 0, Math.PI * 2); context.Clip ();
			context.Antialias = Antialias.Default;
		}
		context.Operator = Operator.Screen;
		context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal);
		context.SetFontSize (14);
		using var options = new FontOptions { Antialias = Antialias.Subpixel };
		context.FontOptions = options;
		var commandsField = typeof (SurfaceRecorderSnapshot).GetField ("commands", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		long retainedBytes = 0;
		for (int i = 0; i < 1000; i++) {
			context.MoveTo (1000.25, 400.5); context.ShowText ("ll ffi");
			// Check each new allocation immediately: the old full-surface mask
			// must fail on the first iteration, not allocate 8 GiB during RED.
			using var snapshot = surface.CaptureRecordedSource ();
			var commands = (SurfaceRecorder.Command[]) commandsField.GetValue (snapshot);
			var coverage = commands[commands.Length - 1].Drawing.TextCoverage;
			Assert.NotNull (coverage);
			long bytes = (long)coverage.Width * coverage.Height * 4;
			Assert.InRange (bytes, 1, 65536);
			using var pixels = coverage.PeekPixels ();
			Assert.NotNull (pixels);
			Assert.Equal (SkiaSharp.SKColorType.Rgba8888, pixels.ColorType);
			Assert.Equal (SkiaSharp.SKAlphaType.Opaque, pixels.AlphaType);
			Assert.InRange ((long)pixels.RowBytes * pixels.Height, 1, 65536);
			retainedBytes += bytes;
		}
		Assert.InRange (retainedBytes, 1, 65536000);
	}

	[Theory]
	[InlineData (Antialias.Subpixel, 8)]
	[InlineData (Antialias.Subpixel, 64)]
	[InlineData (Antialias.Gray, 64)]
	public void StackedTranslucentScreenTextKeepsNativeRounding (Antialias antialias, int labels)
	{
		CairoRecorderParityTests.AssertParity (160, 80, context => {
			context.SetSourceRGB (.2, .3, .4); context.Paint ();
			context.Operator = Operator.Screen; context.SetSourceRGBA (.8, .5, .1, .7);
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
			using var options = new FontOptions { Antialias = antialias };
			context.FontOptions = options;
			for (int i = 0; i < labels; i++) { context.MoveTo (20.25, 45.5); context.ShowText ("ll ffi"); }
		});
	}

	[Theory]
	[InlineData (0, false)]
	[InlineData (0, true)]
	[InlineData (1, true)]
	[InlineData (2, true)]
	[InlineData (3, false)]
	[InlineData (3, true)]
	[InlineData (4, false)]
	[InlineData (4, true)]
	[InlineData (5, false)]
	[InlineData (5, true)]
	[InlineData (6, false)]
	[InlineData (6, true)]
	[InlineData (7, false)]
	[InlineData (7, true)]
	public void CompoundClipTextKeepsNativeCoverage (int shape, bool scaled)
	{
		CairoRecorderParityTests.AssertParity (160, 80, context => {
			context.SetSourceRGB (0, 0, 0); context.Paint ();
			context.Operator = Operator.Screen; context.SetSourceRGB (1, 1, 1);
			context.Rectangle (8.25, 5.5, 145, 70); context.Clip ();
			if (shape == 0) {
				context.Rectangle (20.25, 19.5, 30, 20); context.Rectangle (25.75, 39.5, 20, 15);
			} else if (shape == 1) {
				context.MoveTo (20, 20); context.LineTo (60, 20); context.LineTo (60, 40);
				context.LineTo (35, 40); context.LineTo (35, 55); context.LineTo (20, 55); context.ClosePath ();
			} else if (shape == 2) {
				context.Rectangle (20, 20, 40, 40); context.Rectangle (30, 30, 10, 10); context.FillRule = FillRule.EvenOdd;
			} else if (shape == 3) {
				context.Rectangle (20.25, 19.5, 30, 20);
			} else if (shape == 4) {
				context.Rectangle (20.25, 19.5, 30, 18); context.Rectangle (25.75, 39.5, 20, 15);
			} else if (shape == 5) {
				// Within 1/512 px of the grid: Cairo's 24.8 boxes are aligned.
				context.Rectangle (20.001, 19.0005, 30, 20); context.Rectangle (25, 39.999, 21, 15);
			} else if (shape == 6) {
				// Both boxes round outward onto pixel column 30.
				context.Rectangle (20.25, 19.5, 10.05, 35); context.Rectangle (30.6, 19.5, 20, 35);
			} else {
				// Fractional edges outside the glyph extents leave the reduced clip aligned.
				context.Rectangle (10.5, 20.5, 100, 40);
			}
			context.Clip (); context.Translate (.375, .125); if (scaled) context.Scale (1.25, .75);
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
			using var options = new FontOptions { Antialias = Antialias.Subpixel, HintMetrics = HintMetrics.On };
			context.FontOptions = options; context.MoveTo (20.25, 45.5); context.ShowText ("jÁ ll ffi");
		});
	}

	[Theory]
	[InlineData (3, Operator.Screen, Antialias.Subpixel, false)]
	[InlineData (6, Operator.Screen, Antialias.Subpixel, false)]
	[InlineData (3, Operator.Difference, Antialias.Subpixel, true)]
	[InlineData (6, Operator.Exclusion, Antialias.Subpixel, true)]
	[InlineData (3, Operator.Screen, Antialias.Gray, true)]
	[InlineData (6, Operator.Difference, Antialias.Gray, false)]
	[InlineData (8, Operator.Screen, Antialias.Subpixel, false)]
	[InlineData (8, Operator.Exclusion, Antialias.Gray, true)]
	public void DistantFractionalBoxClipTextKeepsNativeCoverage (int shape, Operator op, Antialias antialias, bool translucent)
	{
		// Far from the origin, so 1/256 px box scans span thousands of rows and columns.
		CairoRecorderParityTests.AssertParity (480, 240, context => {
			context.SetSourceRGB (.1, .3, .6); context.Paint ();
			context.Operator = op;
			if (translucent) context.SetSourceRGBA (.8, .5, .1, .7); else context.SetSourceRGB (1, 1, 1);
			context.Translate (310.5, 150.25);
			if (shape == 3) {
				context.Rectangle (20.25, 19.5, 30, 20);
			} else if (shape == 6) {
				context.Rectangle (20.25, 19.5, 10.05, 35); context.Rectangle (30.6, 19.5, 20, 35);
			} else {
				// Aliased half-pixel box beside an antialiased fractional one.
				context.Antialias = Antialias.None;
				context.Rectangle (20.5, 19.5, 30, 30); context.Clip ();
				context.Antialias = Antialias.Default;
				context.Rectangle (18.25, 30.75, 40, 30);
			}
			context.Clip ();
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
			using var options = new FontOptions { Antialias = antialias, HintMetrics = HintMetrics.On };
			context.FontOptions = options; context.MoveTo (20.25, 45.5); context.ShowText ("jÁ ll ffi");
		});
	}

	[Theory]
	[InlineData (3, 1, Antialias.Subpixel)]
	[InlineData (6, 2, Antialias.Subpixel)]
	[InlineData (4, 1, Antialias.Subpixel)]
	[InlineData (3, 2, Antialias.Gray)]
	[InlineData (9, 0, Antialias.Subpixel)]
	[InlineData (9, 1, Antialias.Subpixel)]
	[InlineData (3, 0, Antialias.None)]
	[InlineData (6, 1, Antialias.None)]
	public void TransformedFractionalBoxClipTextKeepsNativeCoverage (int shape, int transform, Antialias antialias)
	{
		CairoRecorderParityTests.AssertParity (480, 240, context => {
			context.SetSourceRGB (.1, .3, .6); context.Paint ();
			context.Operator = Operator.Screen; context.SetSourceRGB (1, 1, 1);
			// Clip boxes are set under the transform, so device edges land on .5 and other fractions.
			if (transform == 0) context.Translate (310, 150);
			else if (transform == 1) { context.Translate (300, 100); context.Scale (1.5, 1.5); }
			else { context.Translate (250, 60); context.Scale (2, 2); }
			if (shape == 3) {
				context.Rectangle (20.25, 19.5, 30, 20);
			} else if (shape == 4) {
				context.Rectangle (20.25, 19.5, 30, 18); context.Rectangle (25.75, 39.5, 20, 15);
			} else if (shape == 6) {
				context.Rectangle (20.25, 19.5, 10.05, 35); context.Rectangle (30.6, 19.5, 20, 35);
			} else {
				context.Antialias = Antialias.None;
				context.Rectangle (20.5, 19.5, 30, 30.5);
			}
			context.Clip (); context.Antialias = Antialias.Default;
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
			using var options = new FontOptions { Antialias = antialias, HintMetrics = HintMetrics.On };
			context.FontOptions = options; context.MoveTo (20.25, 45.5); context.ShowText ("jÁ ll ffi");
		});
	}

	[Fact]
	public void WideSurfaceFractionalBoxClipTextKeepsNativeCoverage ()
	{
		// Box edges near x = 3000 put the 1/256 px scan far beyond 16.16 limits.
		CairoRecorderParityTests.AssertParity (3200, 120, context => {
			context.SetSourceRGB (0, 0, 0); context.Paint ();
			context.Operator = Operator.Screen; context.SetSourceRGB (1, 1, 1);
			context.Translate (2950.5, 30.25);
			context.Rectangle (20.25, 19.5, 30, 20); context.Rectangle (25.75, 39.5, 20, 15); context.Clip ();
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
			using var options = new FontOptions { Antialias = Antialias.Subpixel, HintMetrics = HintMetrics.On };
			context.FontOptions = options; context.MoveTo (20.25, 45.5); context.ShowText ("jÁ ll ffi");
		});
	}

	[Theory]
	[InlineData (0)]
	[InlineData (1)]
	[InlineData (2)]
	[InlineData (3)]
	public void FractionalClipEdgeSweepsAcrossGlyphExtents (int side)
	{
		// One fractional edge walks across the glyph ink box; the others stay far outside.
		for (double edge = side < 2 ? 26.25 : side == 2 ? 14.25 : 56.25; edge <= (side < 2 ? 54.25 : side == 2 ? 30.25 : 90.25); edge += .5)
			CairoRecorderParityTests.AssertParity (160, 80, context => {
				context.SetSourceRGB (0, 0, 0); context.Paint ();
				context.Operator = Operator.Screen; context.SetSourceRGB (1, 1, 1);
				if (side == 0) context.Rectangle (4, edge, 150, 70 - edge);
				else if (side == 1) context.Rectangle (4, 4, 150, edge - 4);
				else if (side == 2) context.Rectangle (edge, 4, 150 - edge, 70);
				else context.Rectangle (4, 4, edge - 4, 70);
				context.Clip ();
				context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
				using var options = new FontOptions { Antialias = Antialias.Subpixel, HintMetrics = HintMetrics.On };
				context.FontOptions = options; context.MoveTo (20.25, 45.5); context.ShowText ("jÁ ll ffi");
			});
	}

	[Fact]
	public void HardClippedScaledTextKeepsNativeCoverageAtCroppedBoundary ()
	{
		foreach (var order in new[] { SubpixelOrder.Rgb, SubpixelOrder.Bgr, SubpixelOrder.Vrgb, SubpixelOrder.Vbgr })
			CairoRecorderParityTests.AssertParity (160, 80, context => {
				context.SetSourceRGB (0, 0, 0); context.Paint ();
				context.Operator = Operator.Screen; context.SetSourceRGB (1, 1, 1);
				context.Rectangle (8.25, 5.5, 145, 70); context.Clip ();
				context.Antialias = Antialias.None;
				context.Arc (43, 40, 18.5, 0, Math.PI * 2); context.Clip ();
				context.Antialias = Antialias.Default;
				context.Translate (.375, .125); context.Scale (1.25, .75);
				context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
				using var options = new FontOptions { Antialias = Antialias.Subpixel, HintMetrics = HintMetrics.On, SubpixelOrder = order };
				context.FontOptions = options;
				context.MoveTo (20.25, 45.5); context.ShowText ("jÁ ll ffi");
			});
	}

	[Theory]
	[InlineData (Antialias.None, .25)]
	[InlineData (Antialias.Gray, .5)]
	[InlineData (Antialias.Subpixel, .75)]
	public void OffsetTextCoverageKeepsExactNativePixels (Antialias antialias, double fraction)
	{
		CairoRecorderParityTests.AssertParity (160, 80, context => {
			context.SetSourceRGB (0, 0, 0); context.Paint ();
			context.Operator = Operator.Screen; context.SetSourceRGB (1, 1, 1);
			context.Rectangle (7, 5, 147, 70); context.Clip ();
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
			using var options = new FontOptions { Antialias = antialias };
			context.FontOptions = options;
			context.MoveTo (31 + fraction, 47.5); context.ShowText ("ll jÁ ffi");
		}, edgeAllowance: false, flatTolerance: 0);
	}

	[Theory]
	[InlineData ("", 31.25, 47.5)]
	[InlineData ("   ", 31.5, 47.5)]
	[InlineData ("offscreen", -1000.0, -1000.0)]
	[InlineData ("edge jÁ ffi", -12.75, 17.5)]
	public void TextCoverageKeepsEmptyClippedAndSurfaceEdgeCases (string text, double x, double y)
	{
		foreach (var blend in new[] { Operator.Screen, Operator.Difference, Operator.Exclusion })
			CairoRecorderParityTests.AssertParity (160, 80, context => {
				context.SetSourceRGBA (.2, .3, .4, .7); context.Paint ();
				context.Operator = blend; context.SetSourceRGBA (.8, .5, .1, .7);
				context.Rectangle (0, 0, 140, 70); context.Clip ();
				context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
				using var options = new FontOptions { Antialias = Antialias.Subpixel };
				context.FontOptions = options;
				context.MoveTo (x, y); context.ShowText (text);
			});
	}

	[Theory]
	[InlineData (0)]
	[InlineData (1)]
	[InlineData (2)]
	[InlineData (3)]
	[InlineData (4)]
	public void TextCoverageKeepsClipsOffsetsMetricsAndBlur (int variant)
	{
		Action<Context, ImageSurface> draw = (context, surface) => {
			context.SetSourceRGB (0, 0, 0); context.Paint ();
			context.Operator = Operator.Screen; context.SetSourceRGB (1, 1, 1);
			context.Save ();
			context.Rectangle (8.25, 5.5, 145, 70); context.Clip ();
			context.Arc (43, 40, 18.5, 0, Math.PI * 2); context.Clip ();
			context.Translate (.375, .125); context.Scale (1.25, .75);
			context.SelectFontFace ("sans-serif", variant == 3 ? FontSlant.Italic : FontSlant.Normal,
				variant == 3 ? FontWeight.Bold : FontWeight.Normal); context.SetFontSize (14);
			using var options = new FontOptions { Antialias = Antialias.Subpixel,
				HintMetrics = variant == 2 ? HintMetrics.Off : HintMetrics.On };
			context.FontOptions = options;
			context.MoveTo (20.25, 45.5); context.ShowText ("jÁ ll ffi");
			context.Restore ();
			if (variant == 4) surface.BlurFull (3);
		};
		if (variant != 1) { CairoRecorderParityTests.AssertParity (160, 80, draw); return; }

		var offset = new PointD (-7, -5);
		byte[] expected = CairoRecorderParityTests.RenderNative (160, 80, (context, surface) => {
			surface.DeviceOffset = offset; draw (context, surface);
		});
		// Native-backed setters intentionally materialize; verify that contract too.
		using (var native = new ImageSurface (Format.Argb32, 160, 80)) {
			native.BeginRecording ();
			using var context = new Context (native);
			context.SetSourceRGB (0, 0, 0); context.Paint ();
			native.DeviceOffset = offset;
			Assert.Equal (SurfaceRecordingState.NativeMaterialized, native.RecordingState);
			draw (context, native);
			Assert.Equal (expected, native.Data);
		}
		// Use managed storage directly, without changing the global recording flag.
		var constructor = typeof (ImageSurface).GetConstructor (System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
			null, new[] { typeof (ManagedImageSurfaceStorage), typeof (bool), typeof (Status) }, null);
		Assert.NotNull (constructor);
		var storage = ManagedImageSurfaceStorage.CreateManaged (Format.Argb32, 160, 80);
		using var recorded = (ImageSurface) constructor.Invoke (new object[] { storage, true, Status.Success });
		recorded.DeviceOffset = offset; recorded.BeginRecording ();
		using (var context = new Context (recorded)) draw (context, recorded);
		Assert.Equal (SurfaceRecordingState.Recording, recorded.RecordingState);
		Assert.Null (recorded.FirstMaterializationTrigger);
		CairoRecorderParityTests.AssertWithinTolerance (expected, CairoRecorderParityTests.RenderSkia (recorded, 160, 80), 160, 80, true);
		Assert.Null (recorded.FirstMaterializationTrigger);
	}

	[Theory]
	[InlineData (Operator.Difference, Antialias.None)]
	[InlineData (Operator.Difference, Antialias.Fast)]
	[InlineData (Operator.Difference, Antialias.Gray)]
	[InlineData (Operator.Difference, Antialias.Subpixel)]
	[InlineData (Operator.Exclusion, Antialias.None)]
	[InlineData (Operator.Exclusion, Antialias.Fast)]
	[InlineData (Operator.Exclusion, Antialias.Gray)]
	[InlineData (Operator.Exclusion, Antialias.Subpixel)]
	public void SeparableBlendTextKeepsDestination (Operator blend, Antialias antialias)
	{
		CairoRecorderParityTests.AssertParity (160, 80, context => {
			context.SetSourceRGBA (.2, .3, .4, .7); context.Paint ();
			context.Operator = blend; context.Antialias = antialias;
			context.SetSourceRGBA (.8, .5, .1, .7);
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
			using (var options = new FontOptions { Antialias = antialias }) context.FontOptions = options;
			context.MoveTo (3.375, 70.625); context.ShowText ("Screen ffi 123");
		});
	}

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

	[Theory]
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

	[Theory]
	[InlineData (Operator.Difference)]
	[InlineData (Operator.Exclusion)]
	public void ByteRoundedBlendsCoverVariedPremultipliedColours (Operator op)
	{
		CairoRecorderParityTests.AssertParity (128, 128, context => {
			var random = new Random (130);
			context.Antialias = Antialias.None;
			for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) {
				context.Operator = Operator.Source;
				context.SetSourceRGBA (random.NextDouble (), random.NextDouble (), random.NextDouble (), random.NextDouble ());
				context.Rectangle (x * 4, y * 4, 4, 4);
				context.Fill ();
				context.Operator = op;
				context.SetSourceRGBA (random.NextDouble (), random.NextDouble (), random.NextDouble (), random.NextDouble ());
				context.Rectangle (x * 4, y * 4, 4, 4);
				context.Fill ();
			}
		}, edgeAllowance: false);
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

	[Theory]
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

	[Theory]
	[InlineData (Antialias.Default, 1d)]
	[InlineData (Antialias.Default, 1.5d)]
	[InlineData (Antialias.Default, 2d)]
	[InlineData (Antialias.Gray, 1d)]
	[InlineData (Antialias.Gray, 1.5d)]
	[InlineData (Antialias.Gray, 2d)]
	[InlineData (Antialias.Subpixel, 1d)]
	[InlineData (Antialias.Subpixel, 1.5d)]
	[InlineData (Antialias.Subpixel, 2d)]
	public void ScreenTextPreservesFractionalPhasesAndSourceAlpha (Antialias antialias, double scale)
	{
		CairoRecorderParityTests.AssertParity (240, 160, context => {
			context.SetSourceRGBA (.21, .48, .76, .7); context.Paint ();
			context.Operator = Operator.Screen;
			context.Scale (scale, scale);
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
			using (var options = new FontOptions { Antialias = antialias }) context.FontOptions = options;
			for (int i = 0; i < 3; i++) {
				context.SetSourceRGBA (.81, .36, .19, 1 - i * .35);
				context.MoveTo (3.125 + i * .25, 22.125 + i * 23.25);
				context.ShowText ("Screen ffi 123");
			}
		});
	}

	[Theory]
	[InlineData (SubpixelOrder.Rgb)]
	[InlineData (SubpixelOrder.Bgr)]
	[InlineData (SubpixelOrder.Vrgb)]
	[InlineData (SubpixelOrder.Vbgr)]
	public void ScreenTextHonoursSubpixelOrder (SubpixelOrder order)
	{
		CairoRecorderParityTests.AssertParity (240, 80, context => {
			context.SetSourceRGBA (.21, .48, .76, .7); context.Paint ();
			context.Operator = Operator.Screen;
			context.Scale (1.5, 1.5);
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
			using (var options = new FontOptions { Antialias = Antialias.Subpixel, SubpixelOrder = order }) context.FontOptions = options;
			context.SetSourceRGBA (.81, .36, .19, .65);
			context.MoveTo (3.375, 22.625); context.ShowText ("Screen ffi 123");
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
