using System;
using Cairo;
using Xunit;

public sealed class CairoContextRecorderTests
{
	static CairoContextRecorderTests () { _ = CairoAPI.Version; }

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
	public void UnsupportedContextCallMaterializesPrefixBeforeNativeDrawing()
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

		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
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
	public void NonZeroAndEvenOddFillRulesKeepCairoPathSemantics()
	{
		foreach (FillRule fillRule in new[] { FillRule.Winding, FillRule.EvenOdd }) {
			using var actual = new ImageSurface (Format.Argb32, 20, 20);
			var recorder = actual.BeginRecording ();
			using (var context = new Context (actual)) DrawNestedFillRulePaths (context, fillRule);

			using var expected = new ImageSurface (Format.Argb32, 20, 20);
			using (var context = new Context (expected)) DrawNestedFillRulePaths (context, fillRule);

			Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
			Assert.Equal (expected.Data, actual.Data);
			int centerAlphaIndex = 10 * actual.Stride + 10 * 4 + 3;
			if (fillRule == FillRule.EvenOdd) Assert.Equal (0, actual.Data[centerAlphaIndex]);
			else Assert.True (actual.Data[centerAlphaIndex] > 0);
		}
	}

	[Fact]
	public void EmptyCombiningAndFallbackTextMaterializeAndMatchNativeCairo()
	{
		using var actual = new ImageSurface (Format.Argb32, 160, 56);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) DrawTextFallbackCases (context);

		using var expected = new ImageSurface (Format.Argb32, 160, 56);
		using (var context = new Context (expected)) DrawTextFallbackCases (context);

		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
		Assert.Equal (expected.Data, actual.Data);
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
