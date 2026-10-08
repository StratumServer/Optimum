using System;
using System.Runtime.InteropServices;
using System.Threading;
using Cairo;
using SkiaSharp;
using Xunit;

public sealed class CairoSurfaceRecorderTests
{
	static CairoSurfaceRecorderTests () { _ = CairoAPI.Version; }

	[Fact]
	public void ReplayMaterializesRecordedPrefixAndAllowsNativeDrawing()
	{
		using var recorded = new ImageSurface (Format.Argb32, 32, 24);
		using var expected = new ImageSurface (Format.Argb32, 32, 24);
		var recorder = recorded.BeginRecording ();
		Assert.Equal (SurfaceRecordingState.Recording, recorded.RecordingState);
		recorder.FillRectangle (2.25, 3.5, 11.5, 8.25, .2, .7, .4, .65);
		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);

		using (var context = new Context (expected)) {
			DrawNativeRectangle (context, 2.25, 3.5, 11.5, 8.25, .2, .7, .4, .65);
			DrawNativeRectangle (context, 6, 5, 13, 10, .8, .1, .2, .45);
		}
		_ = recorded.DataPtr; // CPU access is the materialization boundary before subsequent native drawing.
		using (var context = new Context (recorded)) {
			Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorded.RecordingState);
			Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
			DrawNativeRectangle (context, 6, 5, 13, 10, .8, .1, .2, .45);
			DrawNativeRectangle (context, 16, 4, 9, 13, .9, .1, .3, .8);
		}
		using (var context = new Context (recorded)) DrawNativeRectangle (context, 1, 18, 7, 4, .3, .4, .9, .7);
		using (var context = new Context (expected)) {
			DrawNativeRectangle (context, 16, 4, 9, 13, .9, .1, .3, .8);
			DrawNativeRectangle (context, 1, 18, 7, 4, .3, .4, .9, .7);
		}

		Assert.Equal (expected.Data, recorded.Data);
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorded.RecordingState);
	}

	[Fact]
	public void RectangleAndColorValuesPreserveCairoClampingAndNegativeDimensions()
	{
		using var surface = new ImageSurface (Format.Argb32, 8, 8);
		var recorder = surface.BeginRecording ();
		recorder.FillRectangle (6, 6, -4, -3, 1.25, -.5, .75, 1.5);
		using (var context = new Context (surface)) { }

		using var expected = new ImageSurface (Format.Argb32, 8, 8);
		using (var context = new Context (expected)) DrawNativeRectangle (context, 6, 6, -4, -3, 1.25, -.5, .75, 1.5);
		Assert.Equal (expected.Data, surface.Data);
	}

	[Fact]
	public void CpuEscapeAfterGpuCommitReplaysRecordedPrefixIntoCairoSurface()
	{
		using var actual = new ImageSurface (Format.Argb32, 16, 12);
		var recorder = actual.BeginRecording ();
		recorder.FillRectangle (1, 1, 14, 10, .12, .48, .82, 1);
		recorder.FillRectangle (3, 2, 8, 7, .91, .2, .36, .43);
		using var bitmap = new SKBitmap (16, 12, SKColorType.Rgba8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas (bitmap)) {
			Assert.True (actual.TryDrawRecordedCommands (canvas));
			canvas.Flush ();
		}
		Assert.Equal (SurfaceRecordingState.Sealed, actual.RecordingState);
		Assert.True (actual.CommitGpuRenderedCommands ());
		Assert.Equal (SurfaceRecordingState.GpuRendered, actual.RecordingState);

		using var expected = new ImageSurface (Format.Argb32, 16, 12);
		using (var context = new Context (expected)) {
			DrawNativeRectangle (context, 1, 1, 14, 10, .12, .48, .82, 1);
			DrawNativeRectangle (context, 3, 2, 8, 7, .91, .2, .36, .43);
		}
		Assert.Equal (expected.Data, actual.Data); // Data materializes the retained display list after GPU publication.
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
	}

	[Fact]
	public void HandleDataDataPointerAndPngBoundariesMaterializePrefix()
	{
		foreach (string boundary in new[] { "handle", "data", "pointer", "png" }) {
			using var surface = new ImageSurface (Format.Argb32, 12, 12);
			var recorder = surface.BeginRecording ();
			recorder.FillRectangle (2, 2, 6, 6, .4, .2, .8, .6);
			using var expected = new ImageSurface (Format.Argb32, 12, 12);
			using (var context = new Context (expected)) DrawNativeRectangle (context, 2, 2, 6, 6, .4, .2, .8, .6);
			if (boundary == "handle") _ = surface.Handle;
			else if (boundary == "data") _ = surface.Data;
			else if (boundary == "pointer") {
				var ptr = surface.DataPtr;
				var actual = new byte[surface.Height * surface.Stride];
				Marshal.Copy (ptr, actual, 0, actual.Length);
				Assert.Equal (expected.Data, actual);
			} else {
				var file = System.IO.Path.GetTempFileName ();
				try {
					surface.WriteToPng (file);
					using var png = new ImageSurface (file);
					Assert.Equal (expected.Data, png.Data);
				} finally { System.IO.File.Delete (file); }
			}
			Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
			Assert.Equal (expected.Data, surface.Data);
		}
	}

	[Fact]
	public void ExistingHandleOrContextMakesSurfaceIneligible()
	{
		using var raw = new ImageSurface (Format.Argb32, 4, 4);
		_ = raw.Handle;
		Assert.Throws<InvalidOperationException> (() => raw.BeginRecording ());

		using var shared = new ImageSurface (Format.Argb32, 4, 4);
		using var context = new Context (shared);
		Assert.Throws<InvalidOperationException> (() => shared.BeginRecording ());
	}

	[Fact]
	public void ExternalImageStorageRemainsPinnedAndIneligibleForRecording()
	{
		var pixels = new byte[8 * 8 * 4];
		var pin = GCHandle.Alloc (pixels, GCHandleType.Pinned);
		try {
			using var surface = new ImageSurface (pin.AddrOfPinnedObject (), Format.Argb32, 8, 8, 8 * 4);
			Assert.Throws<InvalidOperationException> (() => surface.BeginRecording ());
			GC.Collect ();
			GC.WaitForPendingFinalizers ();
			using (var context = new Context (surface)) {
				context.SetSourceRGBA (.2, .5, .8, 1);
				context.Paint ();
			}
			Assert.Contains (pixels, value => value != 0);
		} finally {
			pin.Free ();
		}
	}

	[Fact]
	public void RecordingAndDisposalAreBoundToTheCreatingThread()
	{
		using var surface = new ImageSurface (Format.Argb32, 8, 8);
		var recorder = surface.BeginRecording ();
		Assert.IsType<InvalidOperationException> (RunOnSeparateThread (() => recorder.FillRectangle (0, 0, 1, 1, 0, 0, 0, 1)));
		Assert.IsType<InvalidOperationException> (RunOnSeparateThread (() => _ = surface.Handle));
		Assert.IsType<InvalidOperationException> (RunOnSeparateThread (() => surface.Dispose ()));
		Assert.Equal (SurfaceRecordingState.Recording, surface.RecordingState);
		recorder.FillRectangle (0, 0, 1, 1, 0, 0, 0, 1);
		_ = surface.Handle;
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, surface.RecordingState);
	}

	[Fact]
	public void UnsupportedSurfaceKindsStayNativeAndDisposalIsIdempotent()
	{
		var native = new ImageSurface (Format.Argb32, 4, 4);
		_ = native.Handle;
		native.Dispose ();
		Assert.Equal (IntPtr.Zero, native.Handle);

		using var rgb = new ImageSurface (Format.Rgb24, 4, 4);
		Assert.Throws<InvalidOperationException> (() => rgb.BeginRecording ());
		Assert.Equal (SurfaceRecordingState.Native, rgb.RecordingState);
		using var finished = new ImageSurface (Format.Argb32, 4, 4);
		finished.Finish ();
		Assert.Throws<InvalidOperationException> (() => finished.BeginRecording ());
		using var invalid = new ImageSurface (Format.Argb32, -1, 4);
		Assert.NotEqual (Status.Success, invalid.Status);
		Assert.Throws<InvalidOperationException> (() => invalid.BeginRecording ());

		var surface = new ImageSurface (Format.Argb32, 4, 4);
		var recorder = surface.BeginRecording ();
		recorder.FillRectangle (0, 0, 2, 2, 1, 0, 0, 1);
		surface.Dispose ();
		surface.Dispose ();
		Assert.Equal (SurfaceRecordingState.Disposed, surface.RecordingState);
		Assert.Equal (IntPtr.Zero, surface.Handle);
		Assert.Equal (SurfaceRecordingState.Disposed, recorder.State);
		Assert.Throws<ObjectDisposedException> (() => recorder.FillRectangle (0, 0, 1, 1, 0, 0, 0, 1));
	}

	[Fact]
	public void FailedMaterializationIsStableAndDisposalClearsIt()
	{
		var surface = new ImageSurface (Format.Argb32, 4, 4);
		var recorder = surface.BeginRecording ();
		recorder.FillRectangle (0, 0, 2, 2, 1, 0, 0, 1);
		NativeMethods.cairo_surface_finish (surface.NativeHandleForRecorder);
		var first = Record.Exception (() => _ = surface.Handle);
		Assert.NotNull (first);
		Assert.Equal (SurfaceRecordingState.Failed, recorder.State);
		var second = Record.Exception (() => _ = surface.DataPtr);
		Assert.Same (first, second);
		surface.Dispose ();
		Assert.Equal (SurfaceRecordingState.Disposed, recorder.State);
	}

	static void DrawNativeRectangle (Context context, double x, double y, double width, double height, double r, double g, double b, double a)
	{
		context.SetSourceRGBA (r, g, b, a);
		context.Rectangle (x, y, width, height);
		context.Fill ();
	}

	static Exception RunOnSeparateThread (Action action)
	{
		Exception exception = null;
		var thread = new Thread (() => {
			try { action (); } catch (Exception error) { exception = error; }
		});
		thread.Start ();
		thread.Join ();
		return exception;
	}
}
