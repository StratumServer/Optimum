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
	public void PixelStepAfterGpuCommitMaterializesBeforeRunningNatively ()
	{
		using var surface = new ImageSurface (Format.Argb32, 28, 16);
		var recorder = surface.BeginRecording ();
		using (var context = new Context (surface)) Shapes (context);
		CairoRecorderParityTests.RenderSkia (surface, 28, 16);
		Assert.True (surface.CommitGpuRenderedCommands ());
		surface.DemulAlpha ();
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
		Assert.Equal ("Recorder.DemulAlpha.RecordingStopped", recorder.FirstMaterializationTrigger);
		using var reference = new ImageSurface (Format.Argb32, 28, 16);
		using (var context = new Context (reference)) Shapes (context);
		reference.DemulAlpha ();
		Assert.Equal (reference.Data, surface.Data);
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
