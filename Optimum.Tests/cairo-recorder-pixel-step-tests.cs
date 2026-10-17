using System;
using Cairo;
using SkiaSharp;
using Xunit;

/// <summary>DemulAlpha, the game's box blur and the bitmap overlay run as recorded pixel
/// steps: the same managed byte operation on the Skia result and on native replay.</summary>
[Collection ("Cairo materialization metrics")]
public sealed class CairoRecorderPixelStepTests
{
	static CairoRecorderPixelStepTests () { _ = CairoAPI.Version; }

	static void Shapes (Context context)
	{
		context.Antialias = Antialias.None;
		context.SetSourceRGBA (.16, .58, .92, .75);
		context.Rectangle (2.25, 1.5, 11, 9);
		context.Fill ();
		context.SetSourceRGBA (.9, .3, 0, .4); // blue byte 0: DemulAlpha divides by zero there
		context.Rectangle (6, 6, 12, 7);
		context.Fill ();
		context.SetSourceRGBA (.2, .9, .1, 1);
		context.Rectangle (20, 2, 6, 6);
		context.Fill ();
		context.SetSourceRGBA (.5, .5, .5, .02);
		context.Rectangle (20, 10, 6, 4);
		context.Fill ();
	}

	[Fact]
	public void DemulAlphaStaysRecordedAndMatchesVanillaBytes ()
	{
		// Pharos fixture 2 plus edge cases of the game's native-endian demultiply.
		CairoRecorderParityTests.AssertParity (28, 16, (context, surface) => {
			Shapes (context);
			surface.DemulAlpha ();
		}, edgeAllowance: false, flatTolerance: 0);
	}

	[Fact]
	public void BlurPartialAndFullStayRecordedAndMatchVanillaBytes ()
	{
		CairoRecorderParityTests.AssertParity (40, 32, (context, surface) => {
			Shapes (context);
			surface.BlurPartial (3.2, 7, 1, 1, 30, 20);
			context.SetSourceRGBA (.8, .1, .6, .7);
			context.Rectangle (24, 18, 12, 10);
			context.Fill ();
			surface.BlurFull (2);
			surface.BlurPartial (5.2, 10);
			// Skia and Pixman may round Over onto blurred, non-uniform pixels differently by
			// one level; the blur itself is the identical managed code on both paths.
		}, edgeAllowance: false);
	}

	[Fact]
	public void BlurredAntialiasedDialogBackgroundStaysWithinParity ()
	{
		// GuiElementDialogBackground: rounded fill, shaded stroke, partial blur, pattern fill.
		using var tile = new ImageSurface (Format.Argb32, 4, 4);
		using (var c = new Context (tile)) {
			c.SetSourceRGBA (.4, .3, .2, .25); c.Paint ();
			c.SetSourceRGBA (.1, .1, .1, .25); c.Rectangle (0, 0, 2, 2); c.Fill ();
		}
		CairoRecorderParityTests.AssertLocalAaParity (96, 72, (context, surface) => {
			context.Antialias = Antialias.Best;
			RoundRectangle (context, 4, 6, 88, 60, 2);
			context.SetSourceRGBA (.25, .2, .17, .9);
			context.FillPreserve ();
			context.SetSourceRGBA (.5, .4, .35, 1);
			context.LineWidth = 4;
			context.StrokePreserve ();
			surface.BlurPartial (9, 19, 4, 6, 92, 66);
			using var pattern = new SurfacePattern (tile) { Extend = Extend.Repeat, Filter = Filter.Nearest };
			pattern.Matrix = new Matrix (0.125, 0, 0, 0.125, 0, 0);
			context.SetSource (pattern);
			context.FillPreserve ();
			context.SetSourceRGBA (45 / 255.0, 35 / 255.0, 33 / 255.0, 1);
			context.LineWidth = 2;
			context.Stroke ();
		});
	}

	[Fact]
	public void ImageOverlayStaysRecordedAndMatchesVanillaBytes ()
	{
		// Pharos fixture 4: SurfaceDrawImage.Image over recorded content.
		using var bitmap = new SKBitmap (8, 6, SKColorType.Bgra8888, SKAlphaType.Unpremul);
		bitmap.Erase (new SKColor (66, 117, 213, 255));
		bitmap.SetPixel (3, 2, new SKColor (200, 10, 10, 90));
		CairoRecorderParityTests.AssertParity (24, 16, (context, surface) => {
			Shapes (context);
			surface.Image (bitmap, 2, 3, 16, 12);
		}, edgeAllowance: false, flatTolerance: 0);
	}

	[Fact]
	public void PixelStepsOnNativeSurfacesAreUnchanged ()
	{
		using var recorded = new ImageSurface (Format.Argb32, 28, 16);
		using var native = new ImageSurface (Format.Argb32, 28, 16);
		recorded.BeginRecording ();
		foreach (ImageSurface surface in new[] { recorded, native }) {
			using (var context = new Context (surface)) Shapes (context);
			surface.BlurFull (1.5);
			surface.DemulAlpha ();
		}
		Assert.Equal (SurfaceRecordingState.Recording, recorded.RecordingState);
		Assert.Equal (SurfaceRecordingState.Native, native.RecordingState);
		Assert.Equal (native.Data, recorded.Data);
	}

	[Fact]
	public void PixelStepAfterGpuCommitAppendsToThePublishedList ()
	{
		using var surface = new ImageSurface (Format.Argb32, 28, 16);
		var recorder = surface.BeginRecording ();
		using (var context = new Context (surface)) Shapes (context);
		CairoRecorderParityTests.RenderSkia (surface, 28, 16);
		Assert.True (surface.CommitGpuRenderedCommands ());
		surface.DemulAlpha ();
		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		using var reference = new ImageSurface (Format.Argb32, 28, 16);
		using (var context = new Context (reference)) Shapes (context);
		reference.DemulAlpha ();
		Assert.Equal (reference.Data, surface.Data);
	}


	[Fact]
	public void PharosFixture2AliasedRectangleThenDemulAlphaIsExact ()
	{
		CairoRecorderParityTests.AssertParity (16, 12, (context, surface) => {
			context.Antialias = Antialias.None;
			context.SetSourceRGBA (.16, .58, .92, .75);
			context.Rectangle (2.25, 1.5, 11, 9);
			context.Fill ();
			context.Dispose ();
			SurfaceTransformDemulAlpha.DemulAlpha (surface);
		}, edgeAllowance: false, flatTolerance: 0);
	}

	[Fact]
	public void PharosFixture4ScaledImageOnEmptySurfaceIsExact ()
	{
		using var bitmap = new SKBitmap (8, 6, SKColorType.Bgra8888, SKAlphaType.Unpremul);
		bitmap.Erase (new SKColor (66, 117, 213, 255));
		CairoRecorderParityTests.AssertParity (16, 12, (context, surface) => {
			context.Dispose ();
			SurfaceDrawImage.Image (surface, bitmap, 0, 0, 16, 12);
		}, edgeAllowance: false, flatTolerance: 0);
		byte[] native = CairoRecorderParityTests.RenderNative (16, 12, (context, surface) => SurfaceDrawImage.Image (surface, bitmap, 0, 0, 16, 12));
		Assert.Equal (new byte[] { 213, 117, 66, 255 }, native[0..4]);
	}

	[Theory]
	[InlineData (1, 1)]
	[InlineData (7, 5)]
	[InlineData (13, 3)]
	[InlineData (16, 12)]
	public void ReadbackGuardDetectsHostilePixelStoreState (int width, int height)
	{
		// Models glReadPixels under PACK_ROW_LENGTH=37, SKIP_ROWS=2, SKIP_PIXELS=3 (the
		// Pharos state sentinel): rows land past a tightly sized destination.
		Assert.Throws<InvalidOperationException> (() => RecordedSkiaReplay.ReadBackGuarded (width, height, (data, rowBytes) => {
			int rowLength = 37, skipRows = 2, skipPixels = 3;
			for (int y = 0; y < height; y++)
				for (int x = 0; x < width * 4; x++)
					System.Runtime.InteropServices.Marshal.WriteByte (data, ((y + skipRows) * rowLength + skipPixels) * 4 + x, 0x72);
			return true;
		}));
		byte[] tight = RecordedSkiaReplay.ReadBackGuarded (width, height, (data, rowBytes) => {
			Assert.Equal (width * 4, rowBytes);
			for (int i = 0; i < rowBytes * height; i++) System.Runtime.InteropServices.Marshal.WriteByte (data, i, 7);
			return true;
		});
		Assert.True (tight.Length >= width * height * 4);
	}

	[Fact]
	public void RawWriterReceivesDemultipliedBytesUnchangedInCairoRowOrder ()
	{
		// Fixture 2 on the GPU: DemulAlpha leaves colour above alpha. A premultiplied image
		// write-back may clamp or drop such pixels, and a bottom-left target would flip the
		// rows; the raw writer must receive exactly the native bytes, top row first.
		Action<Context, ImageSurface> draw = (context, surface) => {
			context.Antialias = Antialias.None;
			context.SetSourceRGBA (.16, .58, .92, .75);
			context.Rectangle (2.25, 1.5, 11, 9);
			context.Fill ();
			context.Dispose ();
			SurfaceTransformDemulAlpha.DemulAlpha (surface);
		};
		byte[] expected = CairoRecorderParityTests.RenderNative (16, 12, draw);
		const int pixel = (1 * 16 + 2) * 4;
		Assert.Equal (new byte[] { 176, 160, 43, 20 }, expected[pixel..(pixel + 4)]);
		using var surface = new ImageSurface (Format.Argb32, 16, 12);
		surface.BeginRecording ();
		using (var context = new Context (surface)) draw (context, surface);
		byte[] written = null;
		using SKSurface target = SKSurface.Create (new SKImageInfo (16, 12, SKColorType.Rgba8888, SKAlphaType.Premul));
		Assert.True (surface.TryDrawRecordedCommands (target, (pixels, width, height) => {
			written = new byte[width * height * 4];
			System.Runtime.InteropServices.Marshal.Copy (pixels, written, 0, written.Length);
		}));
		Assert.Equal (expected, written);

		// Model of the failure: a premultiplied write-back that clamps colour to alpha.
		byte[] clamped = (byte[])expected.Clone ();
		for (int i = 0; i < clamped.Length; i += 4)
			for (int c = 0; c < 3; c++) clamped[i + c] = Math.Min (clamped[i + c], clamped[i + 3]);
		Assert.NotEqual (expected, clamped);
	}

	static void RoundRectangle (Context context, double x, double y, double width, double height, double radius)
	{
		double degrees = Math.PI / 180.0;
		context.Antialias = Antialias.Best;
		context.NewPath ();
		context.Arc (x + width - radius, y + radius, radius, -90 * degrees, 0 * degrees);
		context.Arc (x + width - radius, y + height - radius, radius, 0 * degrees, 90 * degrees);
		context.Arc (x + radius, y + height - radius, radius, 90 * degrees, 180 * degrees);
		context.Arc (x + radius, y + radius, radius, 180 * degrees, 270 * degrees);
		context.ClosePath ();
	}
}
