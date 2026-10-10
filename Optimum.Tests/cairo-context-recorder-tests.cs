using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;

public sealed class CairoContextRecorderTests
{
	static CairoContextRecorderTests () { _ = CairoAPI.Version; }

	[Fact]
	public void MutatingGradientAfterDrawCannotChangeEarlierRecordedPixels()
	{
		using var actual = new ImageSurface(Format.Argb32, 40, 24);
		using var expected = new ImageSurface(Format.Argb32, 40, 24);
		actual.BeginRecording();
		static void Draw(Context context) {
			using var gradient = new LinearGradient(0, 0, 40, 0) { Extend = Extend.Pad };
			gradient.AddColorStop(0, new Color(1, 0, 0));
			gradient.AddColorStop(1, new Color(0, 0, 1));
			context.SetSource(gradient); context.Rectangle(0, 0, 40, 12); context.Fill();
			gradient.Matrix = new Matrix(1, 0, 0, 1, 17, 0);
			gradient.AddColorStop(.5, new Color(0, 1, 0));
			context.Rectangle(0, 12, 40, 12); context.Fill();
		}
		using (var context = new Context(actual)) Draw(context);
		using (var context = new Context(expected)) Draw(context);
		Assert.Equal(SurfaceRecordingState.Recording, actual.RecordingState);
		Assert.Equal(expected.Data, actual.Data);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RecordedBlurMatchesNativeBlurWhenMaterializedAndWhenDrawnWithSkia(bool full)
	{
		using var actual = new ImageSurface(Format.Argb32, 48, 32);
		using var expected = new ImageSurface(Format.Argb32, 48, 32);
		actual.BeginRecording();
		void Draw(ImageSurface surface) {
			using (var context = new Context(surface)) {
				context.Antialias = Antialias.None;
				context.SetSourceRGBA(1, 0.2, 0.4, 1); context.Rectangle(4, 4, 24, 16); context.Fill();
				context.SetSourceRGBA(0.2, 0.6, 0.8, 1); context.Rectangle(16, 10, 28, 18); context.Fill();
			}
			if (full) surface.BlurFull(3); else surface.BlurPartial(3, 7, 2, 2, 44, 30);
			using (var context = new Context(surface)) {
				context.Antialias = Antialias.None;
				context.SetSourceRGBA(0, 0, 0, 1); context.Rectangle(0, 0, 6, 6); context.Fill();
			}
		}
		Draw(expected);
		using var bitmap = new SkiaSharp.SKBitmap(48, 32, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
		using (var recorded = new ImageSurface(Format.Argb32, 48, 32)) {
			recorded.BeginRecording();
			Draw(recorded);
			Assert.Equal(SurfaceRecordingState.Recording, recorded.RecordingState);
			using (var canvas = new SkiaSharp.SKCanvas(bitmap)) Assert.True(recorded.TryDrawRecordedCommands(canvas));
		}
		Draw(actual);
		Assert.Equal(expected.Data, actual.Data);
		var pixels = new byte[48 * 32 * 4];
		System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
		byte[] native = expected.Data;
		var bad = new System.Text.StringBuilder(); for (int i = 0; i < pixels.Length; i++) if (Math.Abs(pixels[i] - native[i]) > 3) bad.Append($"({i / 4 % 48},{i / 4 / 48})c{i % 4}:{pixels[i]}/{native[i]} ");
		Assert.True(bad.Length == 0, bad.ToString());
	}

	[Fact]
	public void SkiaGradientPreservesSourceBindingAcrossTransformsAndRestore()
	{
		using var actual = new ImageSurface(Format.Argb32, 48, 24);
		using var expected = new ImageSurface(Format.Argb32, 48, 24);
		actual.BeginRecording();
		static void Draw(Context context) {
			context.Antialias = Antialias.None;
			context.Translate(4, 0);
			using var gradient = new LinearGradient(0, 0, 32, 0) { Extend = Extend.Pad };
			gradient.AddColorStop(0, new Color(1, 0, 0)); gradient.AddColorStop(1, new Color(0, 0, 1));
			context.SetSource(gradient);
			context.Save(); context.Translate(8, 0); context.Rectangle(0, 0, 24, 12); context.Fill(); context.Restore();
			context.Rectangle(0, 12, 32, 12); context.Fill();
		}
		using (var context = new Context(actual)) Draw(context);
		using (var context = new Context(expected)) Draw(context);
		using var bitmap = new SkiaSharp.SKBitmap(48, 24, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
		using (var canvas = new SkiaSharp.SKCanvas(bitmap)) Assert.True(actual.TryDrawRecordedCommands(canvas));
		var pixels = new byte[48 * 24 * 4];
		System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
		byte[] native = expected.Data;
		for (int i = 0; i < pixels.Length; i++) Assert.True(Math.Abs(pixels[i] - native[i]) <= 2, $"Pixel {i / 4 % 48},{i / 4 / 48} channel {i % 4}: Skia={pixels[i]} Cairo={native[i]}");
		Assert.Equal(expected.Data, actual.Data);
	}

	[Fact]
	public void SupportedContextOperationsStayRecordedAndMetadataDoesNotMaterialize()
	{
		using var actual = new ImageSurface (Format.Argb32, 18, 14);
		var recorder = actual.BeginRecording ();
		using var actualContext = new Context (actual);
		actualContext.SetSourceRGBA (0, 0, 0, 0);
		actualContext.Paint ();
		actualContext.Save ();
		actualContext.SetSourceRGBA (.2, .7, .4, .65);
		actualContext.Rectangle (2.25, 3.5, 11.5, 8.25);
		actualContext.Fill ();
		actualContext.Restore ();

		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		Assert.Equal (18, actual.Width);
		Assert.Equal (14, actual.Height);
		Assert.Equal (Format.Argb32, actual.Format);
		Assert.True (actual.Stride >= actual.Width * 4);
		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);

		using var expected = new ImageSurface (Format.Argb32, 18, 14);
		using (var context = new Context (expected)) {
			context.SetSourceRGBA (0, 0, 0, 0);
			context.Paint ();
			context.Save ();
			context.SetSourceRGBA (.2, .7, .4, .65);
			context.Rectangle (2.25, 3.5, 11.5, 8.25);
			context.Fill ();
			context.Restore ();
		}
		Assert.Equal (expected.Data, actual.Data);
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
	}

	[Fact]
	public void DisposingRecordedContextLeavesDisplayListForGpuUploadOrLaterCairoReplay()
	{
		using var actual = new ImageSurface (Format.Argb32, 12, 10);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.SetSourceRGBA (.15, .65, .35, .7);
			context.Rectangle (1.25, 2.5, 8.5, 6);
			context.Fill ();
		}

		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		using var expected = new ImageSurface (Format.Argb32, 12, 10);
		using (var context = new Context (expected)) {
			context.SetSourceRGBA (.15, .65, .35, .7);
			context.Rectangle (1.25, 2.5, 8.5, 6);
			context.Fill ();
		}
		Assert.Equal (expected.Data, actual.Data);
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
	}

	[Fact]
	public void ExposingRawContextHandleMaterializesRecordedCommands()
	{
		using var surface = new ImageSurface (Format.Argb32, 10, 10);
		var recorder = surface.BeginRecording ();
		using var context = new Context (surface);
		context.SetSourceRGBA (1, 0, 0, .5);
		context.Rectangle (1, 1, 5, 5);
		context.Fill ();
		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		_ = context.Handle;
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
	}

	[Fact]
	public void ArcsStayRecordedAndCpuEscapeMatchesNativeDrawing()
	{
		using var actual = new ImageSurface (Format.Argb32, 20, 20);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.SetSourceRGBA (.8, .1, .2, .55);
			context.Rectangle (2, 2, 9, 7);
			context.Fill ();
			context.Arc (14, 13, 3, 0, 6.283185307179586);
			context.Fill ();
		}

		using var expected = new ImageSurface (Format.Argb32, 20, 20);
		using (var context = new Context (expected)) {
			context.SetSourceRGBA (.8, .1, .2, .55);
			context.Rectangle (2, 2, 9, 7);
			context.Fill ();
			context.Arc (14, 13, 3, 0, 6.283185307179586);
			context.Fill ();
		}

		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		Assert.Equal (expected.Data, actual.Data);
	}

	[Fact]
	public void CairoOnlyOperationsPreserveOutputAfterRecordedPrefix()
	{
		using var actual = new ImageSurface (Format.Argb32, 96, 64);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) DrawFallbackOperations (context);

		using var expected = new ImageSurface (Format.Argb32, 96, 64);
		using (var context = new Context (expected)) DrawFallbackOperations (context);

		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
		Assert.Equal (expected.Data, actual.Data);
	}

	[Fact]
	public void ExtendedPrimitivesStayRecordedAndCpuEscapeMatchesNativeOutput()
	{
		using var actual = new ImageSurface (Format.Argb32, 112, 88);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) DrawAdditionalFallbackOperations (context);

		using var expected = new ImageSurface (Format.Argb32, 112, 88);
		using (var context = new Context (expected)) DrawAdditionalFallbackOperations (context);

		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		Assert.Equal (expected.Data, actual.Data);
		Assert.Equal (0, actual.Data[43 * actual.Stride + 67 * 4 + 3]);
	}

	[Fact]
	public void StrokeCapsJoinsAndDashPhasesSurviveNativeMaterialization()
	{
		using var actual = new ImageSurface (Format.Argb32, 112, 72);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) DrawStrokeStyleMatrix (context);

		using var expected = new ImageSurface (Format.Argb32, 112, 72);
		using (var context = new Context (expected)) DrawStrokeStyleMatrix (context);

		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		Assert.Equal (expected.Data, actual.Data);
	}

	[Fact]
	public void SelfIntersectingPathsKeepCairoFillRuleSemanticsAfterMaterialization()
	{
		foreach (FillRule fillRule in new[] { FillRule.Winding, FillRule.EvenOdd }) {
			using var actual = new ImageSurface (Format.Argb32, 32, 32);
			var recorder = actual.BeginRecording ();
			using (var context = new Context (actual)) DrawSelfIntersectingPath (context, fillRule);

			using var expected = new ImageSurface (Format.Argb32, 32, 32);
			using (var context = new Context (expected)) DrawSelfIntersectingPath (context, fillRule);

			Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
			Assert.Equal (expected.Data, actual.Data);
		}
	}

	[Fact]
	public void NonZeroAndEvenOddFillRulesKeepCairoPathSemantics()
	{
		foreach (FillRule fillRule in new[] { FillRule.Winding, FillRule.EvenOdd }) {
			using var actual = new ImageSurface (Format.Argb32, 20, 20);
			var recorder = actual.BeginRecording ();
			using (var context = new Context (actual)) DrawNestedFillRulePaths (context, fillRule);

			using var expected = new ImageSurface (Format.Argb32, 20, 20);
			using (var context = new Context (expected)) DrawNestedFillRulePaths (context, fillRule);

			Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
			Assert.Equal (expected.Data, actual.Data);
			int centerAlphaIndex = 10 * actual.Stride + 10 * 4 + 3;
			if (fillRule == FillRule.EvenOdd) Assert.Equal (0, actual.Data[centerAlphaIndex]);
			else Assert.True (actual.Data[centerAlphaIndex] > 0);
		}
	}

	[Fact]
	public void EmptyCombiningAndFallbackTextStayRecordedAndCpuEscapeMatchesNativeCairo()
	{
		using var actual = new ImageSurface (Format.Argb32, 160, 56);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) DrawTextFallbackCases (context);

		using var expected = new ImageSurface (Format.Argb32, 160, 56);
		using (var context = new Context (expected)) DrawTextFallbackCases (context);

		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		Assert.Equal (expected.Data, actual.Data);
	}

	[Theory]
	[InlineData(1f, EnumLinebreakBehavior.AfterWord)]
	[InlineData(1.5f, EnumLinebreakBehavior.AfterWord)]
	[InlineData(2f, EnumLinebreakBehavior.AfterWord)]
	[InlineData(1f, EnumLinebreakBehavior.AfterCharacter)]
	[InlineData(1.5f, EnumLinebreakBehavior.AfterCharacter)]
	[InlineData(2f, EnumLinebreakBehavior.AfterCharacter)]
	[InlineData(1f, EnumLinebreakBehavior.None)]
	[InlineData(1.5f, EnumLinebreakBehavior.None)]
	[InlineData(2f, EnumLinebreakBehavior.None)]
	public void TextLayoutAndSizingStayCairoEquivalentAcrossScales(float guiScale, EnumLinebreakBehavior linebreak)
	{
		float previousScale = RuntimeEnv.GUIScale;
		RuntimeEnv.GUIScale = guiScale;
		try {
			const string text = "A wrapped phrase with café and e\u0301\nsecond line";
			TextFlowPath[] flowPath = { new TextFlowPath (0, 0, 78, 65), new TextFlowPath (17, 65, 137, 180) };
			TextDrawUtil textUtil = new TextDrawUtil ();
			CairoFont font = new CairoFont (16, "DejaVu Sans").WithLineHeightMultiplier (1.2);
			var expectedBounds = ElementBounds.Fixed (0, 0, 1, 1);
			font.AutoBoxSize (text, expectedBounds);
			CairoFont expectedAutoFont = new CairoFont (18, "DejaVu Sans");
			expectedAutoFont.AutoFontSize (text, expectedBounds);
			TextLine[] actualLines;
			using (var actual = new ImageSurface (Format.Argb32, 240, 180)) {
				var recorder = actual.BeginRecording ();
				using (var context = new Context (actual)) {
					font.SetupContext (context);
					actualLines = textUtil.Lineize (context, text, linebreak, flowPath, 0, 0, font.LineHeightMultiplier);
					textUtil.DrawMultilineText (context, font, actualLines, EnumTextOrientation.Left);
				}

				Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
				Assert.NotEmpty (actualLines);
				Assert.Equal (actualLines.Length, textUtil.GetQuantityTextLines (font, text, linebreak, flowPath));
				Assert.Equal (actualLines.Length * textUtil.GetLineHeight (font),
					textUtil.GetMultilineTextHeight (font, text, linebreak, flowPath), 6);

				using var expected = new ImageSurface (Format.Argb32, 240, 180);
				TextLine[] expectedLines;
				using (var context = new Context (expected)) {
					font.SetupContext (context);
					expectedLines = textUtil.Lineize (context, text, linebreak, flowPath, 0, 0, font.LineHeightMultiplier);
					textUtil.DrawMultilineText (context, font, expectedLines, EnumTextOrientation.Left);
				}

				Assert.Equal (expectedLines.Length, actualLines.Length);
				for (int i = 0; i < expectedLines.Length; i++) {
					Assert.Equal (expectedLines[i].Text, actualLines[i].Text);
					Assert.Equal (expectedLines[i].Bounds.X, actualLines[i].Bounds.X, 6);
					Assert.Equal (expectedLines[i].Bounds.Y, actualLines[i].Bounds.Y, 6);
					Assert.Equal (expectedLines[i].Bounds.Width, actualLines[i].Bounds.Width, 6);
					Assert.Equal (expectedLines[i].Bounds.Height, actualLines[i].Bounds.Height, 6);
					Assert.Equal (expectedLines[i].NextOffsetX, actualLines[i].NextOffsetX, 6);
				}
				Assert.Equal (expected.Data, actual.Data);
			}

			var actualBounds = ElementBounds.Fixed (0, 0, 1, 1);
			font.AutoBoxSize (text, actualBounds);
			Assert.Equal (expectedBounds.fixedWidth, actualBounds.fixedWidth, 6);
			Assert.Equal (expectedBounds.fixedHeight, actualBounds.fixedHeight, 6);

			CairoFont actualAutoFont = new CairoFont (18, "DejaVu Sans");
			actualAutoFont.AutoFontSize (text, actualBounds);
			Assert.Equal (expectedAutoFont.UnscaledFontsize, actualAutoFont.UnscaledFontsize, 6);
		}
		finally {
			RuntimeEnv.GUIScale = previousScale;
		}
	}

	[Fact]
	public void CpuEscapeBetweenSourceAndPathPreservesLiveContextState()
	{
		using var actual = new ImageSurface (Format.Argb32, 16, 12);
		using var actualContext = new Context (BeginRecording (actual));
		actualContext.SetSourceRGBA (.7, .2, .9, .6);
		_ = actual.DataPtr;
		actualContext.Rectangle (2, 3, 9, 6);
		actualContext.Fill ();

		using var expected = new ImageSurface (Format.Argb32, 16, 12);
		using (var context = new Context (expected)) {
			context.SetSourceRGBA (.7, .2, .9, .6);
			context.Rectangle (2, 3, 9, 6);
			context.Fill ();
		}
		Assert.Equal (expected.Data, actual.Data);
	}

	[Fact]
	public void SecondContextMaterializesCommandsIntoFirstContext()
	{
		using var surface = new ImageSurface (Format.Argb32, 12, 12);
		BeginRecording (surface);
		using var first = new Context (surface);
		first.SetSourceRGBA (.3, .8, .1, .7);
		first.Rectangle (1, 1, 8, 8);
		using var second = new Context (surface);
		first.Fill ();
		Assert.NotEqual (IntPtr.Zero, second.Handle);

		using var expected = new ImageSurface (Format.Argb32, 12, 12);
		using (var context = new Context (expected)) {
			context.SetSourceRGBA (.3, .8, .1, .7);
			context.Rectangle (1, 1, 8, 8);
			context.Fill ();
		}
		Assert.Equal (expected.Data, surface.Data);
	}

	[Fact]
	public void SetTargetMaterializesOldSurfaceAndAttachesNewRecorder()
	{
		using var oldSurface = new ImageSurface (Format.Argb32, 12, 12);
		using var newSurface = new ImageSurface (Format.Argb32, 12, 12);
		var oldRecorder = oldSurface.BeginRecording ();
		var newRecorder = newSurface.BeginRecording ();
		using var context = new Context (oldSurface);
		context.SetSourceRGBA (.9, .2, .1, .8);
		context.Rectangle (1, 1, 5, 5);
		context.Fill ();
		context.SetTarget (newSurface);
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, oldRecorder.State);
		context.SetSourceRGBA (.1, .4, .9, .7);
		context.Rectangle (4, 3, 6, 7);
		context.Fill ();
		Assert.Equal (SurfaceRecordingState.Recording, newRecorder.State);

		using var expected = new ImageSurface (Format.Argb32, 12, 12);
		using (var native = new Context (expected)) {
			native.SetSourceRGBA (.1, .4, .9, .7);
			native.Rectangle (4, 3, 6, 7);
			native.Fill ();
		}
		Assert.Equal (expected.Data, newSurface.Data);
	}

	[Fact]
	public void NativeContextRemainsUsableAfterSurfaceWrapperDisposal()
	{
		var surface = new ImageSurface (Format.Argb32, 8, 8);
		var context = new Context (surface);
		surface.Dispose ();
		context.SetSourceRGBA (1, 0, 0, 1);
		context.Rectangle (1, 1, 4, 4);
		context.Fill ();
		Assert.NotEqual (IntPtr.Zero, context.Handle);
		context.Dispose ();
	}

	[Fact]
	public void DisposedContextHandlePreservesZeroSentinel()
	{
		using var surface = new ImageSurface (Format.Argb32, 4, 4);
		var context = new Context (surface);
		context.Dispose ();
		Assert.Equal (IntPtr.Zero, context.Handle);
	}

	[Fact]
	public void SurfaceDisposalMaterializesAndDetachesLiveRecordingContext()
	{
		var surface = new ImageSurface (Format.Argb32, 8, 8);
		var recorder = surface.BeginRecording ();
		var context = new Context (surface);
		context.SetSourceRGBA (1, 0, 0, .5);
		context.Rectangle (1, 1, 4, 4);
		surface.Dispose ();
		Assert.Equal (SurfaceRecordingState.Disposed, recorder.State);
		context.Fill ();
		Assert.NotEqual (IntPtr.Zero, context.Handle);
		context.Dispose ();
	}

	[Fact]
	public void FailedMaterializationBeforeDisposeStillAllowsContextCleanup()
	{
		using var surface = new ImageSurface (Format.Argb32, 8, 8);
		var recorder = surface.BeginRecording ();
		var context = new Context (surface);
		context.SetSourceRGBA (1, 0, 0, .5);
		context.Rectangle (1, 1, 4, 4);
		context.Restore (); // Replay fails with Cairo's InvalidRestore status.

		Exception materializationFailure = Record.Exception (() => _ = surface.Data);
		Assert.IsType<InvalidOperationException> (materializationFailure);
		Assert.Equal (SurfaceRecordingState.Failed, recorder.State);
		Assert.Null (Record.Exception (() => context.Dispose ()));
		Assert.Equal (IntPtr.Zero, context.Handle);
	}

	static ImageSurface BeginRecording (ImageSurface surface)
	{
		surface.BeginRecording ();
		return surface;
	}

	static void DrawFallbackOperations (Context context)
	{
		context.SetSourceRGBA (.12, .28, .44, .8);
		context.Rectangle (2, 2, 24, 16);
		context.Fill ();

		context.Save ();
		context.Save ();
		context.SetSourceRGBA (.9, .25, .08, .7);
		context.Rectangle (5, 5, 28, 20);
		context.LineWidth = 2.5;
		context.Stroke ();
		context.Restore ();
		context.Restore ();

		using (var gradient = new LinearGradient (36, 4, 68, 28)) {
			gradient.AddColorStop (0, new Color (.1, .7, .3, .9));
			gradient.AddColorStop (1, new Color (.1, .2, .9, .4));
			gradient.Matrix = new Matrix (1, 0, 0, 1, 2, 1);
			context.SetSource (gradient);
			context.Rectangle (36, 4, 32, 24);
			context.Fill ();
		}

		context.NewPath ();
		context.Arc (80, 15, 10, 0, Math.PI * 1.5);
		using (var path = context.CopyPath ()) {
			context.NewPath ();
			context.AppendPath (path);
			context.ClosePath ();
			context.SetSourceRGBA (.75, .6, .12, .85);
			context.Fill ();
		}

		context.PushGroup ();
		context.SetSourceRGBA (.2, .8, .7, .65);
		context.Arc (18, 44, 9, 0, Math.PI * 2);
		context.Fill ();
		using (var group = context.PopGroup ()) {
			context.SetSource (group);
			context.PaintWithAlpha (.8);
		}

		context.SelectFontFace ("DejaVu Sans", FontSlant.Normal, FontWeight.Bold);
		context.SetFontSize (12);
		context.MoveTo (34, 48);
		context.SetSourceRGBA (.08, .1, .12, 1);
		context.ShowText ("Cairo fallback");

		using var image = new ImageSurface (Format.Argb32, 4, 4);
		using (var imageContext = new Context (image)) {
			imageContext.SetSourceRGBA (.9, .3, .1, 1);
			imageContext.Paint ();
		}
		context.SetSourceSurface (image, 72, 48);
		context.Rectangle (72, 48, 4, 4);
		context.Fill ();
	}

	static void DrawNestedFillRulePaths (Context context, FillRule fillRule)
	{
		context.SetSourceRGBA (.24, .62, .88, .9);
		context.FillRule = fillRule;
		context.Rectangle (2, 2, 16, 16);
		context.Rectangle (6, 6, 8, 8);
		context.Fill ();
	}

	static void DrawAdditionalFallbackOperations (Context context)
	{
		context.SetSourceRGBA (.1, .2, .3, .7);
		context.Rectangle (1, 1, 5, 5);
		context.Fill ();

		context.Save ();
		context.Translate (7.5, 4.25);
		context.Rotate (.12);
		context.Scale (1.1, .9);
		context.SetSourceRGBA (.8, .15, .25, .85);
		context.LineWidth = 3.25;
		context.LineCap = LineCap.Round;
		context.LineJoin = LineJoin.Bevel;
		context.SetDash (new[] { 3.0, 1.5, .75, 1.0 }, .625);
		context.MoveTo (4, 14);
		context.CurveTo (13, 2, 24, 25, 34, 10);
		context.LineTo (40, 17);
		context.Stroke ();
		context.Restore ();

		context.NewPath ();
		context.MoveTo (48, 18);
		context.Arc (57, 18, 9, 0, Math.PI * 1.5);
		context.LineTo (57, 18);
		context.ClosePath ();
		context.SetSourceRGBA (.2, .75, .35, .8);
		context.Fill ();

		context.Save ();
		context.Rectangle (4.25, 32.5, 44.5, 28.25);
		context.Clip ();
		context.NewPath ();
		context.Arc (26, 47, 13, 0, Math.PI * 2);
		context.Clip ();
		context.SetSourceRGBA (.9, .6, .08, .75);
		context.Paint ();
		context.Restore ();

		context.FillRule = FillRule.Winding;
		context.NewPath ();
		context.MoveTo (57, 32);
		context.LineTo (78, 32);
		context.LineTo (78, 54);
		context.LineTo (57, 54);
		context.ClosePath ();
		context.MoveTo (62, 37);
		context.LineTo (62, 49);
		context.LineTo (73, 49);
		context.LineTo (73, 37);
		context.ClosePath ();
		context.SetSourceRGBA (.2, .35, .9, .8);
		context.Fill ();

		using (var radial = new RadialGradient (91, 40, 1, 91, 40, 14)) {
			radial.AddColorStop (0, new Color (.95, .2, .15, .95));
			radial.AddColorStop (1, new Color (.1, .25, .8, .35));
			radial.Extend = Extend.Pad;
			radial.Matrix = new Matrix (1.2, .25, -.15, .85, -2, 1);
			context.SetSource (radial);
			context.Rectangle (77, 26, 31, 29);
			context.Fill ();
		}

		using var tile = new ImageSurface (Format.Argb32, 4, 4);
		using (var tileContext = new Context (tile)) {
			tileContext.SetSourceRGBA (.15, .7, .4, 1);
			tileContext.Rectangle (0, 0, 2, 4);
			tileContext.Fill ();
			tileContext.SetSourceRGBA (.9, .3, .1, 1);
			tileContext.Rectangle (2, 0, 2, 4);
			tileContext.Fill ();
		}
		using (var pattern = new SurfacePattern (tile)) {
			pattern.Extend = Extend.Repeat;
			pattern.Filter = Filter.Nearest;
			pattern.Matrix = new Matrix (.75, .15, -.1, .8, -3, 2);
			context.SetSource (pattern);
			context.Rectangle (5, 66, 102, 18);
			context.Fill ();
		}
	}

	static void DrawStrokeStyleMatrix (Context context)
	{
		context.SetSourceRGBA (.15, .42, .82, .9);
		context.SetDash (new[] { 4.0, 1.5, 1.0, 2.0 }, .75);
		foreach (LineCap cap in new[] { LineCap.Butt, LineCap.Round, LineCap.Square }) {
			foreach (LineJoin join in new[] { LineJoin.Miter, LineJoin.Round, LineJoin.Bevel }) {
				int row = (int)cap * 3 + (int)join;
				context.Save ();
				context.LineWidth = 2.5 + row * .15;
				context.LineCap = cap;
				context.LineJoin = join;
				context.SetDash (new[] { 3.0 + row * .1, 1.25, .75 }, .25 * row);
				context.MoveTo (4, 6 + row * 7);
				context.LineTo (25, 6 + row * 7);
				context.LineTo (34, 10 + row * 7);
				context.CurveTo (39, 3 + row * 7, 44, 15 + row * 7, 50, 7 + row * 7);
				context.Stroke ();
				context.Restore ();
			}
		}
	}

	static void DrawSelfIntersectingPath (Context context, FillRule fillRule)
	{
		context.SetSourceRGBA (.32, .68, .91, .85);
		context.FillRule = fillRule;
		context.MoveTo (4, 4);
		context.LineTo (28, 28);
		context.LineTo (4, 28);
		context.LineTo (28, 4);
		context.ClosePath ();
		context.Fill ();
	}

	static void DrawTextFallbackCases (Context context)
	{
		context.SetSourceRGBA (.15, .25, .35, 1);
		context.Rectangle (1, 1, 3, 3);
		context.Fill ();
		context.SelectFontFace ("DejaVu Sans", FontSlant.Normal, FontWeight.Normal);
		context.SetFontSize (14);
		TextExtents empty = context.TextExtents (string.Empty);
		Assert.Equal (0, empty.Width);
		Assert.Equal (0, empty.XAdvance);
		foreach (string text in new[] { "e\u0301", "fallback 漢字" }) {
			TextExtents extents = context.TextExtents (text);
			Assert.True (double.IsFinite (extents.Width));
			Assert.True (double.IsFinite (extents.XAdvance));
			context.MoveTo (8, 24 + (text[0] == 'e' ? 0 : 20));
			context.ShowText (text);
		}
	}

}
