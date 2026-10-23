using System;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;

[Collection("GUI metrics")]
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
		actualContext.Antialias = Antialias.None;
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
			context.Antialias = Antialias.None;
			context.SetSourceRGBA (.2, .7, .4, .65);
			context.Rectangle (2.25, 3.5, 11.5, 8.25);
			context.Fill ();
			context.Restore ();
		}
		Assert.Equal (expected.Data, actual.Data);
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
	}

	[Fact]
	public void RecordedMaskSurfaceMatchesNativeCairo()
	{
		const int width = 8, height = 4;
		using var actual = new ImageSurface (Format.Argb32, width, height);
		actual.BeginRecording ();
		using var mask = new ImageSurface (Format.A8, 4, 2);
		byte[] alpha = new byte[mask.Height * mask.Stride];
		for (int y = 0; y < mask.Height; y++) {
			alpha[y * mask.Stride] = 0;
			alpha[y * mask.Stride + 1] = 64;
			alpha[y * mask.Stride + 2] = 128;
			alpha[y * mask.Stride + 3] = 255;
		}
		Marshal.Copy (alpha, 0, mask.DataPtr, alpha.Length);
		mask.MarkDirty ();
		using (var context = new Context (actual)) {
			context.SetSourceRGBA (.8, .3, .1, .75);
			context.Rectangle (0, 0, width, height);
			context.MaskSurface (mask, 2, 1);
		}
		using var bitmap = new SKBitmap (new SKImageInfo (width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
		using (var canvas = new SKCanvas (bitmap)) Assert.True (actual.TryDrawRecordedCommands (canvas));
		byte[] skiaPixels = new byte[bitmap.ByteCount];
		Marshal.Copy (bitmap.GetPixels (), skiaPixels, 0, skiaPixels.Length);

		using var expected = new ImageSurface (Format.Argb32, width, height);
		using (var context = new Context (expected)) {
			context.SetSourceRGBA (.8, .3, .1, .75);
			context.Rectangle (0, 0, width, height);
			context.MaskSurface (mask, 2, 1);
		}
		byte[] cairoPixels = expected.Data;
		int maxChannelDelta = 0;
		for (int i = 0; i < skiaPixels.Length; i++) maxChannelDelta = Math.Max (maxChannelDelta, Math.Abs (skiaPixels[i] - cairoPixels[i]));
		Assert.InRange (maxChannelDelta, 0, 10);
		Assert.Equal (cairoPixels, actual.Data);
	}

	[Fact]
	public void RecordedGradientMaskMatchesNativeCairo()
	{
		const int width = 16, height = 8;
		using var actual = new ImageSurface (Format.Argb32, width, height);
		actual.BeginRecording ();
		using var actualMask = new LinearGradient (0, 0, width, 0);
		actualMask.AddColorStop (0, new Color (1, 1, 1, 0));
		actualMask.AddColorStop (1, new Color (1, 1, 1, 1));
		using (var context = new Context (actual)) {
			context.SetSourceRGBA (.1, .6, .9, 1);
			context.Rectangle (0, 0, width, height);
			context.Mask (actualMask);
		}
		using var bitmap = new SKBitmap (new SKImageInfo (width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
		using (var canvas = new SKCanvas (bitmap)) Assert.True (actual.TryDrawRecordedCommands (canvas));
		byte[] skiaPixels = new byte[bitmap.ByteCount];
		Marshal.Copy (bitmap.GetPixels (), skiaPixels, 0, skiaPixels.Length);

		using var expected = new ImageSurface (Format.Argb32, width, height);
		using var cairoMask = new LinearGradient (0, 0, width, 0);
		cairoMask.AddColorStop (0, new Color (1, 1, 1, 0));
		cairoMask.AddColorStop (1, new Color (1, 1, 1, 1));
		using (var context = new Context (expected)) {
			context.SetSourceRGBA (.1, .6, .9, 1);
			context.Rectangle (0, 0, width, height);
			context.Mask (cairoMask);
		}
		byte[] cairoPixels = expected.Data;
		int maxChannelDelta = 0;
		for (int i = 0; i < skiaPixels.Length; i++) maxChannelDelta = Math.Max (maxChannelDelta, Math.Abs (skiaPixels[i] - cairoPixels[i]));
		Assert.InRange (maxChannelDelta, 0, 10);
		Assert.Equal (cairoPixels, actual.Data);
	}

	[Fact]
	public void RecordedSurfacePatternMaskMatchesNativeCairo()
	{
		const int width = 8, height = 4;
		using var actual = new ImageSurface (Format.Argb32, width, height);
		actual.BeginRecording ();
		using var mask = new ImageSurface (Format.Argb32, width, height);
		byte[] maskPixels = new byte[mask.Height * mask.Stride];
		for (int y = 0; y < height; y++) for (int x = 4; x < width; x++) maskPixels[y * mask.Stride + x * 4 + 3] = 255;
		Marshal.Copy (maskPixels, 0, mask.DataPtr, maskPixels.Length);
		mask.MarkDirty ();
		using var actualMask = new SurfacePattern (mask);
		using (var context = new Context (actual)) {
			context.SetSourceRGBA (.8, .2, .4, 1);
			context.Rectangle (0, 0, width, height);
			context.Mask (actualMask);
		}
		using var bitmap = new SKBitmap (new SKImageInfo (width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
		using (var canvas = new SKCanvas (bitmap)) Assert.True (actual.TryDrawRecordedCommands (canvas));
		byte[] skiaPixels = new byte[bitmap.ByteCount];
		Marshal.Copy (bitmap.GetPixels (), skiaPixels, 0, skiaPixels.Length);

		using var expected = new ImageSurface (Format.Argb32, width, height);
		using var expectedMask = new SurfacePattern (mask);
		using (var context = new Context (expected)) {
			context.SetSourceRGBA (.8, .2, .4, 1);
			context.Rectangle (0, 0, width, height);
			context.Mask (expectedMask);
		}
		byte[] cairoPixels = expected.Data;
		int maxChannelDelta = 0;
		for (int i = 0; i < skiaPixels.Length; i++) maxChannelDelta = Math.Max (maxChannelDelta, Math.Abs (skiaPixels[i] - cairoPixels[i]));
		Assert.InRange (maxChannelDelta, 0, 10);
		Assert.Equal (cairoPixels, actual.Data);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ArcPathConstructionStaysRecordedAndMatchesCairo(bool negative)
	{
		using var actual = new ImageSurface (Format.Argb32, 24, 20);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.Antialias = Antialias.Best;
			context.SetSourceRGBA (.2, .65, .9, .8);
			if (negative) context.ArcNegative (12, 10, 7, 0, -Math.PI * 1.5);
			else context.Arc (12, 10, 7, 0, Math.PI * 1.5);
			context.LineTo (12, 10);
			context.ClosePath ();
			context.Fill ();
		}

		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		using var bitmap = new SKBitmap (new SKImageInfo (24, 20, SKColorType.Bgra8888, SKAlphaType.Premul));
		using (var canvas = new SKCanvas (bitmap)) Assert.True (actual.TryDrawRecordedCommands (canvas));
		byte[] gpuPixels = new byte[bitmap.ByteCount];
		Marshal.Copy (bitmap.GetPixels (), gpuPixels, 0, gpuPixels.Length);

		using var expected = new ImageSurface (Format.Argb32, 24, 20);
		using (var context = new Context (expected)) {
			context.Antialias = Antialias.Best;
			context.SetSourceRGBA (.2, .65, .9, .8);
			if (negative) context.ArcNegative (12, 10, 7, 0, -Math.PI * 1.5);
			else context.Arc (12, 10, 7, 0, Math.PI * 1.5);
			context.LineTo (12, 10);
			context.ClosePath ();
			context.Fill ();
		}
		byte[] expectedPixels = expected.Data;
		int maxChannelDelta = 0;
		for (int i = 0; i < gpuPixels.Length; i++)
			maxChannelDelta = Math.Max (maxChannelDelta, Math.Abs (gpuPixels[i] - expectedPixels[i]));
		Assert.InRange (maxChannelDelta, 0, 20);
		Assert.Equal (expectedPixels, actual.Data);
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ContextArcsMatchEquivalentManagedCubicSegments(bool negative)
	{
		using var actual = new ImageSurface (Format.Argb32, 24, 20);
		using (var context = new Context (actual)) {
			context.Antialias = Antialias.Best;
			context.SetSourceRGBA (.2, .65, .9, .8);
			if (negative) context.ArcNegative (12, 10, 7, 0, -Math.PI / 2);
			else context.Arc (12, 10, 7, 0, Math.PI / 2);
			context.LineTo (12, 10);
			context.ClosePath ();
			context.Fill ();
		}

		using var expected = new ImageSurface (Format.Argb32, 24, 20);
		using (var context = new Context (expected)) {
			context.Antialias = Antialias.Best;
			context.SetSourceRGBA (.2, .65, .9, .8);
			context.MoveTo (19, 10);
			if (negative)
				context.CurveTo (19, 6.134006751184447, 15.865993248815553, 3, 12, 3);
			else
				context.CurveTo (19, 13.865993248815553, 15.865993248815553, 17, 12, 17);
			context.LineTo (12, 10);
			context.ClosePath ();
			context.Fill ();
		}

		Assert.Equal (expected.Data, actual.Data);
	}

	[Theory]
	[InlineData(false, 0.0, 0.0, 7.0, 3.0)]
	[InlineData(true, 0.0, 0.0, 7.0, 3.0)]
	[InlineData(false, 4.71238898038469, 3.141592653589793, -3.0, 3.0)]
	[InlineData(true, 0.0, 1.5707963267948966, 2.0, 8.0)]
	public void ArcSweepNormalizationAndZeroSweepKeepCairoCurrentPoint(bool negative, double angle1, double angle2, double expectedX, double expectedY)
	{
		using var surface = new ImageSurface (Format.Argb32, 16, 16);
		using var context = new Context (surface);
		if (negative) context.ArcNegative (2, 3, 5, angle1, angle2);
		else context.Arc (2, 3, 5, angle1, angle2);
		Assert.True (context.HasCurrentPoint);
		PointD point = context.CurrentPoint;
		Assert.InRange (Math.Abs (point.X - expectedX), 0, 1e-10);
		Assert.InRange (Math.Abs (point.Y - expectedY), 0, 1e-10);
	}

	[Theory]
	[InlineData(false, 0.0)]
	[InlineData(false, -2.0)]
	[InlineData(true, 0.0)]
	[InlineData(true, -2.0)]
	public void ArcWithNonPositiveRadiusKeepsCairoCenterPointBehavior(bool negative, double radius)
	{
		using var surface = new ImageSurface (Format.Argb32, 16, 16);
		using var context = new Context (surface);
		if (negative) context.ArcNegative (2, 3, radius, 0, Math.PI / 2);
		else context.Arc (2, 3, radius, 0, Math.PI / 2);
		Assert.True (context.HasCurrentPoint);
		Assert.Equal (new PointD (2, 3), context.CurrentPoint);
	}

	[Fact]
	public void SolidColorSettersStayRecordedAndMatchCairoAndSkia()
	{
		Action<Context>[] setColors = {
			context => context.SetSourceColor (new Color (.23, .61, .87, .72)),
			context => context.SetSourceRGB (.23, .61, .87),
			context => context.SetSourceRGBA (.23, .61, .87, .72),
			context => context.SetSourceRGBA (new[] { .23, .61, .87, .72 }),
			context => context.SetSourceRGBA (new[] { .23f, .61f, .87f, .72f })
		};

		foreach (Action<Context> setColor in setColors) {
			using var actual = new ImageSurface (Format.Argb32, 18, 14);
			var recorder = actual.BeginRecording ();
			using (var context = new Context (actual)) {
				context.Antialias = Antialias.None;
				context.Operator = Operator.Source;
				context.SetSourceRGBA (0, 0, 0, 0);
				context.Paint ();
				context.Save ();
				setColor (context);
				context.Rectangle (2, 2, 12, 9);
				context.Fill ();
				context.Restore ();
			}
			Assert.Equal (SurfaceRecordingState.Recording, recorder.State);

			using var bitmap = new SKBitmap (new SKImageInfo (18, 14, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (var canvas = new SKCanvas (bitmap)) Assert.True (actual.TryDrawRecordedCommands (canvas));
			byte[] gpuPixels = new byte[bitmap.ByteCount];
			Marshal.Copy (bitmap.GetPixels (), gpuPixels, 0, gpuPixels.Length);

			using var expected = new ImageSurface (Format.Argb32, 18, 14);
			using (var context = new Context (expected)) {
				context.Antialias = Antialias.None;
				context.Operator = Operator.Source;
				context.SetSourceRGBA (0, 0, 0, 0);
				context.Paint ();
				context.Save ();
				context.SetSourceRGBA (.23, .61, .87, setColor == setColors[1] ? 1 : .72);
				context.Rectangle (2, 2, 12, 9);
				context.Fill ();
				context.Restore ();
			}
			byte[] expectedPixels = expected.Data;
			for (int i = 0; i < gpuPixels.Length; i++)
				Assert.InRange (Math.Abs (gpuPixels[i] - expectedPixels[i]), 0, 1);
			Assert.Equal (expected.Data, actual.Data);
			Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
		}
	}

	[Fact]
	public void ArrayColorSettersCaptureValuesAndKeepTheirArgumentExceptions()
	{
		foreach (bool useFloat in new[] { false, true }) {
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			var recorder = surface.BeginRecording ();
			using (var context = new Context (surface)) {
				if (useFloat) {
					var color = new[] { .2f, .5f, .8f, .6f };
					context.SetSourceRGBA (color);
					Array.Fill (color, 1f);
				} else {
					var color = new[] { .2, .5, .8, .6 };
					context.SetSourceRGBA (color);
					Array.Fill (color, 1d);
				}
				context.Antialias = Antialias.None;
				context.Rectangle (1, 1, 6, 6);
				context.Fill ();
			}
			Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
			using var expected = new ImageSurface (Format.Argb32, 8, 8);
			using (var context = new Context (expected)) {
				context.Antialias = Antialias.None;
				context.SetSourceRGBA (.2, .5, .8, .6);
				context.Rectangle (1, 1, 6, 6);
				context.Fill ();
			}
			Assert.Equal (expected.Data, surface.Data);
		}

		using var invalidSurface = new ImageSurface (Format.Argb32, 4, 4);
		var invalidRecorder = invalidSurface.BeginRecording ();
		using (var context = new Context (invalidSurface)) {
			Assert.Throws<NullReferenceException> (() => context.SetSourceRGBA ((double[])null));
			Assert.Throws<IndexOutOfRangeException> (() => context.SetSourceRGBA (new[] { 1d, 0d }));
			Assert.Throws<NullReferenceException> (() => context.SetSourceRGBA ((float[])null));
			Assert.Throws<IndexOutOfRangeException> (() => context.SetSourceRGBA (new[] { 1f, 0f }));
		}
		Assert.Equal (SurfaceRecordingState.Recording, invalidRecorder.State);

		var disposed = new Context (new ImageSurface (Format.Argb32, 2, 2));
		disposed.Dispose ();
		Assert.Throws<ObjectDisposedException> (() => disposed.SetSourceRGBA ((double[])null));
		Assert.Throws<ObjectDisposedException> (() => disposed.SetSourceRGBA ((float[])null));
	}

	[Fact]
	public void DisposingRecordedContextLeavesDisplayListForGpuUploadOrLaterCairoReplay()
	{
		using var actual = new ImageSurface (Format.Argb32, 12, 10);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.SetSourceRGBA (.15, .65, .35, .7);
			context.Rectangle (1.25, 2.5, 8.5, 6);
			context.Antialias = Antialias.None;
			context.Fill ();
		}

		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		using var expected = new ImageSurface (Format.Argb32, 12, 10);
		using (var context = new Context (expected)) {
			context.SetSourceRGBA (.15, .65, .35, .7);
			context.Rectangle (1.25, 2.5, 8.5, 6);
			context.Antialias = Antialias.None;
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
		context.Antialias = Antialias.None;
		context.Fill ();
		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		_ = context.Handle;
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
	}

	[Fact]
	public void ManagedGradientMaskPreservesPrefixAndReplaysToCairo()
	{
		using var actual = new ImageSurface (Format.Argb32, 20, 20);
		var recorder = actual.BeginRecording ();
		// Both solid and gradient masks are captured as managed alpha sources.
		using var mask = new LinearGradient (0, 0, 20, 0);
		mask.AddColorStop (0, new Color (0, 0, 0, 0)); mask.AddColorStop (1, new Color (0, 0, 0, 1));
		using (var context = new Context (actual)) {
			context.SetSourceRGBA (.8, .1, .2, .55);
			context.Rectangle (2, 2, 9, 7);
			context.Fill ();
			context.Mask (mask);
		}

		using var expected = new ImageSurface (Format.Argb32, 20, 20);
		using (var context = new Context (expected)) {
			context.SetSourceRGBA (.8, .1, .2, .55);
			context.Rectangle (2, 2, 9, 7);
			context.Fill ();
			context.Mask (mask);
		}

		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		Assert.Equal (expected.Data, actual.Data);
	}

	[Fact]
	public void PushGroupMaterializesRecordedPrefixBeforeNativeGroupSetup()
	{
		using var surface = new ImageSurface (Format.Argb32, 8, 8);
		var recorder = surface.BeginRecording ();
		using var context = new Context (surface);
		context.PushGroup ();

		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
		Assert.Equal ("Context.PushGroup", recorder.FirstMaterializationTrigger);
	}

	[Theory]
	[InlineData (Antialias.Default)]
	[InlineData (Antialias.None)]
	[InlineData (Antialias.Gray)]
	[InlineData (Antialias.Subpixel)]
	[InlineData (Antialias.Fast)]
	[InlineData (Antialias.Good)]
	[InlineData (Antialias.Best)]
	public void AntialiasModesStayOnSkiaRecorder (Antialias antialias)
	{
		// Cairo and Skia assign exact pixel-center boundary ties differently in aliased mode.
		// Keep this parity fixture off those ties; fractional/tie behavior needs a separate policy fixture.
		double x = antialias == Antialias.None ? 2 : 2.25;
		double y = antialias == Antialias.None ? 3 : 3.25;
		double width = antialias == Antialias.None ? 12 : 11.5;
		double height = antialias == Antialias.None ? 8 : 8.25;
		using var actual = new ImageSurface (Format.Argb32, 18, 14);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.SetSourceRGBA (.2, .7, .4, .65);
			context.Rectangle (x, y, width, height);
			context.Antialias = antialias;
			Assert.Equal (antialias, context.Antialias);
			context.Fill ();
		}

		using var expected = new ImageSurface (Format.Argb32, 18, 14);
		using (var context = new Context (expected)) {
			context.SetSourceRGBA (.2, .7, .4, .65);
			context.Rectangle (x, y, width, height);
			context.Antialias = antialias;
			context.Fill ();
		}

		AssertSkiaParity (expected.Data, RenderRecorded (actual, 18, 14), 20);
		Assert.Equal (SurfaceRecordingState.Sealed, recorder.State);
		Assert.Null (actual.FirstMaterializationTrigger);
	}

	[Theory]
	[InlineData (2.0, 2.0)]
	[InlineData (2.5, 2.0)]
	[InlineData (3.5, 2.0)]
	[InlineData (2.0, 2.5)]
	[InlineData (2.0, 4.5)]
	[InlineData (2.5, 2.5)]
	[InlineData (3.5, 4.5)]
	public void AntialiasNoneMatchesCairoAtPixelCenterTies (double x, double y)
	{
		using var actual = new ImageSurface (Format.Argb32, 12, 10);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.Antialias = Antialias.None;
			context.SetSourceRGBA (1, 1, 1, 1);
			context.Rectangle (x, y, 4, 3);
			context.Fill ();
		}
		using var expected = new ImageSurface (Format.Argb32, 12, 10);
		using (var context = new Context (expected)) {
			context.Antialias = Antialias.None;
			context.SetSourceRGBA (1, 1, 1, 1);
			context.Rectangle (x, y, 4, 3);
			context.Fill ();
		}

		Assert.Equal (expected.Data, RenderRecorded (actual, 12, 10));
		Assert.Equal (SurfaceRecordingState.Sealed, recorder.State);
		Assert.Null (actual.FirstMaterializationTrigger);
	}

	[Theory]
	[InlineData (2.0, 1.25, 1.25, 0.0, 0.0)]
	[InlineData (2.0, .75, .75, 1.0, 1.0)]
	public void AntialiasNoneMatchesCairoAtPixelCenterTiesUnderTransform (double scale, double x, double y, double translateX, double translateY)
	{
		using var actual = new ImageSurface (Format.Argb32, 16, 14);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.Antialias = Antialias.None;
			context.Translate (translateX, translateY);
			context.Scale (scale, scale);
			context.SetSourceRGBA (1, 1, 1, 1);
			context.Rectangle (x, y, 2, 1.5);
			context.Fill ();
		}
		using var expected = new ImageSurface (Format.Argb32, 16, 14);
		using (var context = new Context (expected)) {
			context.Antialias = Antialias.None;
			context.Translate (translateX, translateY);
			context.Scale (scale, scale);
			context.SetSourceRGBA (1, 1, 1, 1);
			context.Rectangle (x, y, 2, 1.5);
			context.Fill ();
		}

		Assert.Equal (expected.Data, RenderRecorded (actual, 16, 14));
		Assert.Equal (SurfaceRecordingState.Sealed, recorder.State);
		Assert.Null (actual.FirstMaterializationTrigger);
	}

	[Fact]
	public void DefaultAntialiasFillStaysOnSkiaRecorder ()
	{
		using var actual = new ImageSurface (Format.Argb32, 18, 14);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.SetSourceRGBA (.2, .7, .4, .65);
			context.Rectangle (2.25, 3.5, 11.5, 8.25);
			context.Fill ();
		}

		using var expected = new ImageSurface (Format.Argb32, 18, 14);
		using (var context = new Context (expected)) {
			context.SetSourceRGBA (.2, .7, .4, .65);
			context.Rectangle (2.25, 3.5, 11.5, 8.25);
			context.Fill ();
		}

		AssertSkiaParity (expected.Data, RenderRecorded (actual, 18, 14), 20);
		Assert.Equal (SurfaceRecordingState.Sealed, recorder.State);
		Assert.Null (actual.FirstMaterializationTrigger);
	}

	[Fact]
	public void RestoreOfDefaultAntialiasKeepsSubsequentFillOnSkia ()
	{
		using var actual = new ImageSurface (Format.Argb32, 18, 14);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.Save ();
			context.Antialias = Antialias.None;
			context.Restore ();
			context.SetSourceRGBA (.2, .7, .4, .65);
			context.Rectangle (2.25, 3.5, 11.5, 8.25);
			context.Fill ();
		}

		using var expected = new ImageSurface (Format.Argb32, 18, 14);
		using (var context = new Context (expected)) {
			context.Save ();
			context.Antialias = Antialias.None;
			context.Restore ();
			context.SetSourceRGBA (.2, .7, .4, .65);
			context.Rectangle (2.25, 3.5, 11.5, 8.25);
			context.Fill ();
		}

		AssertSkiaParity (expected.Data, RenderRecorded (actual, 18, 14), 20);
		Assert.Equal (SurfaceRecordingState.Sealed, recorder.State);
		Assert.Null (actual.FirstMaterializationTrigger);
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
	public void RemainingPartialPrimitiveFallbackCasesMatchNativeOutput()
	{
		using var actual = new ImageSurface (Format.Argb32, 112, 88);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) DrawAdditionalFallbackOperations (context);

		using var expected = new ImageSurface (Format.Argb32, 112, 88);
		using (var context = new Context (expected)) DrawAdditionalFallbackOperations (context);

		byte[] skiaPixels = RenderRecorded (actual, 112, 88);
		AssertSkiaParity (expected.Data, skiaPixels, 32);
		Assert.Equal (SurfaceRecordingState.Sealed, recorder.State);
		Assert.Null (actual.FirstMaterializationTrigger);
		Assert.Equal (0, skiaPixels[43 * 112 * 4 + 67 * 4 + 3]);
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
	public void EmptyCombiningAndFallbackTextRenderFromSkiaWithoutMaterialization()
	{
		using var actual = new ImageSurface (Format.Argb32, 160, 56);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) DrawTextFallbackCases (context);

		byte[] pixels = RenderRecorded (actual, 160, 56);
		Assert.Equal (SurfaceRecordingState.Sealed, recorder.State);
		Assert.Null (actual.FirstMaterializationTrigger);
		Assert.Contains (pixels, pixel => pixel != 0);
	}

	[Fact]
	public void SubpixelTextUsesSkiaLcdEdgingWithoutMaterialization()
	{
		using var actual = new ImageSurface (Format.Argb32, 144, 36);
		var recorder = actual.BeginRecording ();
		DrawSubpixelText (actual, Antialias.Subpixel);
		byte[] subpixel = RenderRecorded (actual, 144, 36);

		using var grayscale = new ImageSurface (Format.Argb32, 144, 36);
		grayscale.BeginRecording ();
		DrawSubpixelText (grayscale, Antialias.Best);
		byte[] antialias = RenderRecorded (grayscale, 144, 36);

		Assert.Equal (SurfaceRecordingState.Sealed, recorder.State);
		Assert.Null (actual.FirstMaterializationTrigger);
		Assert.NotEqual (antialias, subpixel);
	}

	[Fact]
	public void SubpixelGlyphsUseSkiaLcdEdgingWithoutMaterialization()
	{
		using var actual = new ImageSurface (Format.Argb32, 64, 36);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.SetSourceColor (new Color (.94, .93, .89, 1));
			context.SelectFontFace ("DejaVu Sans", FontSlant.Normal, FontWeight.Normal);
			context.SetFontSize (18);
			using var options = new FontOptions { Antialias = Antialias.Subpixel };
			context.FontOptions = options;
			Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
			Assert.Null (actual.FirstMaterializationTrigger);
			context.ShowGlyphs (new[] { new Glyph (36, 3.375, 23.625) });
		}
		byte[] subpixel = RenderRecorded (actual, 64, 36);

		using var grayscale = new ImageSurface (Format.Argb32, 64, 36);
		grayscale.BeginRecording ();
		using (var context = new Context (grayscale)) {
			context.SetSourceColor (new Color (.94, .93, .89, 1));
			context.SelectFontFace ("DejaVu Sans", FontSlant.Normal, FontWeight.Normal);
			context.SetFontSize (18);
			using var options = new FontOptions { Antialias = Antialias.Best };
			context.FontOptions = options;
			context.ShowGlyphs (new[] { new Glyph (36, 3.375, 23.625) });
		}
		byte[] antialias = RenderRecorded (grayscale, 64, 36);

		Assert.Equal (SurfaceRecordingState.Sealed, recorder.State);
		Assert.Null (actual.FirstMaterializationTrigger);
		Assert.NotEqual (antialias, subpixel);
	}

	static void DrawSubpixelText (ImageSurface surface, Antialias antialias)
	{
		using var context = new Context (surface);
		context.Antialias = Antialias.Best;
		context.SetSourceRGBA (.12, .18, .24, 1);
		context.Rectangle (0, 0, surface.Width, surface.Height);
		context.Fill ();
		context.SetSourceColor (new Color (.94, .93, .89, 1));
		context.SelectFontFace ("DejaVu Sans", FontSlant.Normal, FontWeight.Normal);
		context.SetFontSize (15);
		using var options = new FontOptions { Antialias = antialias };
		context.FontOptions = options;
		context.MoveTo (3.375, 23.625);
		context.ShowText ("Subpixel: ffi 0123");
	}

	static byte[] RenderRecorded (ImageSurface surface, int width, int height)
	{
		using var bitmap = new SKBitmap (new SKImageInfo (width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
		using var properties = new SKSurfaceProperties (SKPixelGeometry.RgbHorizontal);
		using var target = SKSurface.Create (bitmap.Info, bitmap.GetPixels (), bitmap.RowBytes, properties)
			?? throw new InvalidOperationException ("Skia could not create an LCD-capable raster surface.");
		Assert.True (surface.TryDrawRecordedCommands (target.Canvas));
		target.Flush (true);
		byte[] pixels = new byte[bitmap.ByteCount];
		Marshal.Copy (bitmap.GetPixels (), pixels, 0, pixels.Length);
		return pixels;
	}

	static void AssertSkiaParity (byte[] expected, byte[] actual, int maximumChannelDelta)
	{
		Assert.Equal (expected.Length, actual.Length);
		int largestDelta = 0;
		int largestIndex = -1;
		for (int i = 0; i < expected.Length; i++)
			if (Math.Abs (expected[i] - actual[i]) > largestDelta) { largestDelta = Math.Abs (expected[i] - actual[i]); largestIndex = i; }
		Assert.True (largestDelta <= maximumChannelDelta,
			$"largest channel delta {largestDelta} at pixel {largestIndex / 4}, channel {largestIndex % 4}");
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
				Assert.Null (actual.FirstMaterializationTrigger);
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
		context.Antialias = Antialias.None;
		context.Fill ();
		context.SetTarget (newSurface);
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, oldRecorder.State);
		context.SetSourceRGBA (.1, .4, .9, .7);
		context.Rectangle (4, 3, 6, 7);
		context.Antialias = Antialias.None;
		context.Fill ();
		Assert.Equal (SurfaceRecordingState.Recording, newRecorder.State);

		using var expected = new ImageSurface (Format.Argb32, 12, 12);
		using (var native = new Context (expected)) {
			native.SetSourceRGBA (.1, .4, .9, .7);
			native.Rectangle (4, 3, 6, 7);
			native.Antialias = Antialias.None;
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
	public void InvalidRestoreLatchesTheErrorAndKeepsRecording()
	{
		using var surface = new ImageSurface (Format.Argb32, 8, 8);
		var recorder = surface.BeginRecording ();
		var context = new Context (surface);
		context.SetSourceRGBA (1, 0, 0, .5);
		context.Rectangle (1, 1, 4, 4);
		context.Restore ();
		context.Fill ();

		Assert.Equal (Status.InvalidRestore, context.Status);
		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);

		// Cairo ignores the fill after the error: reading the pixels replays the same program natively.
		using var expected = new ImageSurface (Format.Argb32, 8, 8);
		using (var reference = new Context (expected)) {
			reference.SetSourceRGBA (1, 0, 0, .5);
			reference.Rectangle (1, 1, 4, 4);
			reference.Restore ();
			reference.Fill ();
		}
		Assert.Equal (expected.Data, surface.Data);
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
