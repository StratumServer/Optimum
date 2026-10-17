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

	[Fact]
	[MethodImpl (MethodImplOptions.NoInlining)]
	public void MaterializationCallersAreAttributedToTheFirstNonCairoFrame ()
	{
		SurfaceRecorder.ResetMaterializationCallers ();
		using var surface = new ImageSurface (Format.Argb32, 4, 4);
		surface.BeginRecording ().FillRectangle (0, 0, 2, 2, 1, 0, 0, 1);
		_ = surface.DataPtr;
		Dictionary<string, long> callers = SurfaceRecorder.GetMaterializationCallers ();
		Assert.Equal (1, callers["SurfaceCpuReadback.DataPtr@CairoRecorderLifetimeTests.MaterializationCallersAreAttributedToTheFirstNonCairoFrame"]);
		SurfaceRecorder.ResetMaterializationCallers ();
		Assert.Empty (SurfaceRecorder.GetMaterializationCallers ());
	}

	[Theory]
	[InlineData (1, 0, 0, 1, 3, 2)]
	[InlineData (1.5, 0, 0, 1.5, 2.25, 1.75)]
	[InlineData (0.75, 0.1, -0.2, 0.9, 4.5, 3)]
	public void DrawSvgMatrixLoopMatchesTheOriginal (double xx, double yx, double xy, double yy, double x0, double y0)
	{
		// SvgLoader.DrawSvg(…, Matrix, …) used by the waypoint and ore map layers.
		var matrix = new Matrix (xx, yx, xy, yy, x0, y0);
		const int w = 10, h = 7, surfaceWidth = 24, surfaceHeight = 18;
		var rgba = new byte[w * h * 4];
		for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 37 + 11);
		var expected = new int[surfaceWidth * surfaceHeight];
		var actual = new int[surfaceWidth * surfaceHeight];
		for (int i = 0; i < expected.Length; i++) expected[i] = actual[i] = unchecked((int)0x80402010) + i;
		var source = new int[w * h];
		Buffer.BlockCopy (rgba, 0, source, 0, rgba.Length);
		for (int i = 0; i < w; i++)
			for (int j = 0; j < h; j++) {
				double px = 1 + i, py = 2 + j;
				matrix.TransformPoint (ref px, ref py);
				int index = (int)py * surfaceWidth + (int)px;
				if (index >= 0 && index < expected.Length) expected[index] = ColorUtil.ColorOver (source[j * w + i], expected[index]);
			}
		RecordedPixelOperation operation = RecordedSvgGeometry.DrawSvgMatrixOperation (rgba, matrix, 1, 2, w, h, ColorUtil.ColorOver);
		GCHandle pin = GCHandle.Alloc (actual, GCHandleType.Pinned);
		try { operation (pin.AddrOfPinnedObject (), surfaceWidth, surfaceHeight, surfaceWidth * 4); }
		finally { pin.Free (); }
		Assert.Equal (expected, actual);
	}
}
