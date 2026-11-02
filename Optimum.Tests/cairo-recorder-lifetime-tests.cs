using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cairo;
using Vintagestory.API.MathTools;
using Xunit;

/// <summary>Disposal order, caller attribution and the DrawSvg matrix loop.</summary>
[Collection ("Cairo materialization metrics")]
public sealed class CairoRecorderLifetimeTests
{
	static CairoRecorderLifetimeTests () { _ = CairoAPI.Version; }

	static List<string> Observe (out Action detach)
	{
		var events = new List<string> ();
		Action<string, bool> observer = (trigger, ok) => events.Add (trigger);
		SurfaceRecorder.MaterializationCompleted += observer;
		detach = () => SurfaceRecorder.MaterializationCompleted -= observer;
		return events;
	}

	[Theory]
	[InlineData (false)]
	[InlineData (true)]
	public void DisposingSurfaceBeforeItsContextDoesNotMaterialize (bool uploadedFirst)
	{
		// TextTextureUtil, GuiElementDialogTitleBar, GuiElementStatbar, IconUtil …:
		// generate texture, surface.Dispose(), then ctx.Dispose().
		List<string> events = Observe (out Action detach);
		try {
			var surface = new ImageSurface (Format.Argb32, 16, 12);
			SurfaceRecorder recorder = surface.BeginRecording ();
			var context = new Context (surface);
			context.SetSourceRGBA (.3, .6, .9, .8);
			context.Rectangle (2, 2, 9, 7);
			context.Fill ();
			if (uploadedFirst) {
				CairoRecorderParityTests.RenderSkia (surface, 16, 12);
				Assert.True (surface.CommitGpuRenderedCommands ());
			}
			surface.Dispose ();
			context.Dispose ();
			Assert.Empty (events);
			Assert.NotEqual (SurfaceRecordingState.NativeMaterialized, recorder.State);
		} finally { detach (); }
	}

	[Fact]
	public void NativeReplayKeepsPixelsFromExposedManagedSourceSnapshot ()
	{
		bool previousRecording = Surface.AutomaticRecordingEnabled;
		try {
			Surface.ConfigureAutomaticRecording (false);
			Surface.ConfigureAutomaticRecording (true);
			using var source = new ImageSurface (Format.Argb32, 4, 4);
			byte[] redPixel = { 0, 0, 255, 255 };
			Marshal.Copy (redPixel, 0, source.DataPtr, redPixel.Length);
			Assert.Equal ((byte)255, source.Data[2]);
			using SurfaceRecorderSnapshot snapshot = source.CaptureRecordedSource ();
			Surface.ConfigureAutomaticRecording (false);
			using var native = new ImageSurface (snapshot.PixelFormat, snapshot.Width, snapshot.Height);
			snapshot.ReplayNative (native.NativeHandleForMetadata);
			byte[] pixels = native.Data;
			int pixelOffset = 0;
			Assert.Equal (new byte[] { 0, 0, 255, 255 }, new byte[] { pixels[pixelOffset], pixels[pixelOffset + 1], pixels[pixelOffset + 2], pixels[pixelOffset + 3] });
		} finally {
			Surface.ConfigureAutomaticRecording (previousRecording);
		}
	}

	[Fact]
	public void ContextOutlivingItsSurfaceKeepsRecordingAndGetTargetSeesAllPixels ()
	{
		Action<Context> draw = context => {
			context.SetSourceRGBA (.3, .6, .9, .8);
			context.Rectangle (2, 2, 9, 7);
			context.Fill ();
		};
		byte[] expected = CairoRecorderParityTests.RenderNative (16, 12, context => {
			draw (context);
			context.SetSourceRGBA (.9, .2, .1, .5);
			context.Rectangle (6, 5, 8, 6);
			context.Fill ();
		});
		var surface = new ImageSurface (Format.Argb32, 16, 12);
		surface.BeginRecording ();
		using var context = new Context (surface);
		draw (context);
		surface.Dispose ();
		context.SetSourceRGBA (.9, .2, .1, .5);
		context.Rectangle (6, 5, 8, 6);
		context.Fill (); // still recorded into the orphaned list
		using var target = (ImageSurface)context.GetTarget (); // a native consumer: materializes the orphan
		Assert.Equal (expected, target.Data);
	}


}
