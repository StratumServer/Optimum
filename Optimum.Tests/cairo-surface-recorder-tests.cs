using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Cairo;
using SkiaSharp;
using Vintagestory.API.Config;
using Xunit;

[CollectionDefinition("GUI metrics", DisableParallelization = true)]
public sealed class CairoMaterializationMetricsCollection { }

[Collection("GUI metrics")]
public sealed class CairoSurfaceRecorderTests
{
	static CairoSurfaceRecorderTests () { _ = CairoAPI.Version; _ = typeof (Vintagestory.API.Client.ICoreClientAPI).Assembly; }

	[Fact]
	public void RecordedGuiFactoryCreatesRecordingEligibleSurface()
	{
		using var surface = ImageSurface.CreateForRecordedGui (Format.Argb32, 12, 8);
		Assert.Equal (12, surface.Width);
		Assert.Equal (8, surface.Height);
		Assert.Equal (SurfaceRecordingState.Recording, surface.BeginRecording ().State);
	}

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
		Assert.True (recorder.RecordAntialias (Antialias.None));
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
		Assert.Equal ("SurfaceCpuReadback.Data", recorder.FirstMaterializationTrigger);
	}

	[Fact]
	public void DefaultAntialiasSurfaceHelperCanBeSealedForSkia()
	{
		using var actual = new ImageSurface (Format.Argb32, 16, 12);
		var recorder = actual.BeginRecording ();
		recorder.FillRectangle (1.25, 1.5, 10.5, 8.25, .12, .48, .82, 1);
		using var bitmap = new SKBitmap (16, 12, SKColorType.Rgba8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas (bitmap)) Assert.True (actual.TryDrawRecordedCommands (canvas));
		Assert.Equal (SurfaceRecordingState.Sealed, recorder.State);
	}

	[Fact]
	public void AutomaticSkiaRenderingRejectsCairoMaterialization()
	{
		bool original = Surface.AutomaticRecordingEnabled;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			var recorder = surface.BeginRecording ();
			recorder.FillRectangle (1, 1, 6, 6, 1, 0, 0, 1);

			var exception = Assert.Throws<InvalidOperationException> (() => _ = surface.Data);
			Assert.Contains ("Cairo materialization is forbidden", exception.Message);
			Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
			Assert.Equal ("SurfaceCpuReadback.Data", recorder.FirstMaterializationTrigger);
		} finally {
			Surface.ConfigureAutomaticRecording (original);
		}
	}

	[Fact]
	public void BestAntialiasRecordingMatchesCairoWithinEdgeTolerance()
	{
		using var actual = new ImageSurface (Format.Argb32, 18, 14);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.Antialias = Antialias.Best;
			context.SetSourceRGBA (.2, .7, .4, .65);
			context.Rectangle (2.25, 3.5, 11.5, 8.25);
			context.Fill ();
			context.SetSourceRGBA (.9, .1, .3, .43);
			context.Rectangle (4.5, 1.75, 10.25, 9.5);
			context.Fill ();
		}
		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);

		using var bitmap = new SKBitmap (new SKImageInfo (18, 14, SKColorType.Bgra8888, SKAlphaType.Premul));
		using (var canvas = new SKCanvas (bitmap)) Assert.True (actual.TryDrawRecordedCommands (canvas));

		using var expected = new ImageSurface (Format.Argb32, 18, 14);
		using (var context = new Context (expected)) {
			context.Antialias = Antialias.Best;
			context.SetSourceRGBA (.2, .7, .4, .65);
			context.Rectangle (2.25, 3.5, 11.5, 8.25);
			context.Fill ();
			context.SetSourceRGBA (.9, .1, .3, .43);
			context.Rectangle (4.5, 1.75, 10.25, 9.5);
			context.Fill ();
		}

		byte[] actualPixels = new byte[bitmap.ByteCount];
		Marshal.Copy (bitmap.GetPixels (), actualPixels, 0, actualPixels.Length);
		byte[] expectedPixels = expected.Data;
		int maxChannelDelta = 0;
		for (int i = 0; i < actualPixels.Length; i++) maxChannelDelta = Math.Max (maxChannelDelta, Math.Abs (actualPixels[i] - expectedPixels[i]));
		Assert.InRange (maxChannelDelta, 0, 3);
	}

	[Fact]
	public void UnbalancedSaveCannotBeSealedForSkia()
	{
		using var actual = new ImageSurface (Format.Argb32, 16, 12);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.Save ();
			context.Antialias = Antialias.None;
			context.SetSourceRGBA (.12, .48, .82, 1);
			context.Rectangle (1, 1, 10, 8);
			context.Fill ();
		}

		using var bitmap = new SKBitmap (16, 12, SKColorType.Rgba8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas (bitmap)) Assert.False (actual.TryDrawRecordedCommands (canvas));
		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);

		using var expected = new ImageSurface (Format.Argb32, 16, 12);
		using (var context = new Context (expected)) {
			context.Save ();
			context.Antialias = Antialias.None;
			context.SetSourceRGBA (.12, .48, .82, 1);
			context.Rectangle (1, 1, 10, 8);
			context.Fill ();
		}
		Assert.Equal (expected.Data, actual.Data);
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
			string expectedTrigger = boundary switch {
				"handle" => "Surface.Handle",
				"data" => "SurfaceCpuReadback.Data",
				"pointer" => "SurfaceCpuReadback.DataPtr",
				_ => "Surface.WriteToPng"
			};
			Assert.Equal (expectedTrigger, recorder.FirstMaterializationTrigger);
			Assert.Equal (expected.Data, surface.Data);
		}
	}

	[Fact]
	public void CpuSkiaReplayCompositesRecordedImageWithoutCairoMaterialization()
	{
		using var source = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul);
		source.Erase(SKColors.Lime);
		using var surface = new ImageSurface(Format.Argb32, 8, 8);
		var recorder = surface.BeginRecording();
		Assert.True(recorder.RecordImage(source, 2, 2, 4, 4));
		using var target = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
		using (var canvas = new SKCanvas(target)) Assert.True(surface.TryDrawRecordedCommands(canvas));

		Assert.Equal(SKColors.Lime, target.GetPixel(3, 3));
		Assert.Equal((byte)0, target.GetPixel(0, 0).Alpha);
		Assert.Equal(SurfaceRecordingState.Sealed, recorder.State);
		Assert.Null(recorder.FirstMaterializationTrigger);
	}

	[Fact]
	public void CpuSkiaReplayCompositesRecordedPictureWithoutNativeReplay()
	{
		using var pictureRecorder = new SKPictureRecorder();
		SKCanvas pictureCanvas = pictureRecorder.BeginRecording(SKRect.Create(4, 4));
		pictureCanvas.Clear(SKColors.Transparent);
		pictureCanvas.DrawColor(SKColors.Magenta);
		using SKPicture picture = pictureRecorder.EndRecording();
		using var surface = new ImageSurface(Format.Argb32, 8, 8);
		var recorder = surface.BeginRecording();
		Assert.True(surface.RecordPicture(picture, 2, 2, 4, 4, null,
			_ => throw new Xunit.Sdk.XunitException("CPU Skia replay invoked the native Cairo callback.")));
		using var target = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
		using (var canvas = new SKCanvas(target)) Assert.True(surface.TryDrawRecordedCommands(canvas));

		Assert.Equal(SKColors.Magenta, target.GetPixel(3, 3));
		Assert.Equal((byte)0, target.GetPixel(0, 0).Alpha);
		Assert.Equal(SurfaceRecordingState.Sealed, recorder.State);
		Assert.Null(recorder.FirstMaterializationTrigger);
	}

	[Fact]
	public void MaterializationDiagnosticsCountActualReplayOnceWithoutUpload()
	{
		using var observer = SubscribeMaterializationMetricsForTest ();
		bool wasEnabled = OptimumGuiMetrics.Enabled;
		try {
			OptimumGuiMetrics.Enabled = true;
			OptimumGuiMetrics.Reset ();
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			surface.BeginRecording ().FillRectangle (1, 1, 4, 4, .2, .5, .8, 1);
			_ = surface.DataPtr;
			_ = surface.Data;

			var snapshot = OptimumGuiMetrics.Snapshot ();
			Assert.Equal (1, snapshot.MaterializationAttemptCount);
			Assert.Equal (1, snapshot.MaterializationSuccessCount);
			Assert.Equal (0, snapshot.MaterializationFailureCount);
			Assert.Equal (1, OptimumGuiMetrics.GetMaterializationDiagnostics ()["SurfaceCpuReadback.DataPtr"]);
		} finally {
			OptimumGuiMetrics.Reset ();
			OptimumGuiMetrics.Enabled = wasEnabled;
		}
	}

	[Fact]
	public void MaterializationDiagnosticsCountGpuReadbackAndFailedReplayExactlyOnce()
	{
		using var observer = SubscribeMaterializationMetricsForTest ();
		bool wasEnabled = OptimumGuiMetrics.Enabled;
		try {
			OptimumGuiMetrics.Enabled = true;
			OptimumGuiMetrics.Reset ();
			using (var surface = new ImageSurface (Format.Argb32, 8, 8)) {
				var recorder = surface.BeginRecording ();
				Assert.True (recorder.RecordAntialias (Antialias.None));
				recorder.FillRectangle (1, 1, 4, 4, .2, .5, .8, 1);
				using var bitmap = new SKBitmap (8, 8, SKColorType.Rgba8888, SKAlphaType.Premul);
				using (var canvas = new SKCanvas (bitmap)) Assert.True (surface.TryDrawRecordedCommands (canvas));
				Assert.True (surface.CommitGpuRenderedCommands ());
				_ = surface.Data;
				_ = surface.DataPtr;
			}

			using (var failed = new ImageSurface (Format.Argb32, 4, 4)) {
				failed.BeginRecording ().FillRectangle (0, 0, 2, 2, 1, 0, 0, 1);
				NativeMethods.cairo_surface_finish (failed.NativeHandleForRecorder);
				Assert.NotNull (Record.Exception (() => _ = failed.Handle));
				Assert.NotNull (Record.Exception (() => _ = failed.DataPtr));
			}

			var snapshot = OptimumGuiMetrics.Snapshot ();
			Assert.Equal (2, snapshot.MaterializationAttemptCount);
			Assert.Equal (1, snapshot.MaterializationSuccessCount);
			Assert.Equal (1, snapshot.MaterializationFailureCount);
			var causes = OptimumGuiMetrics.GetMaterializationDiagnostics ();
			Assert.Equal (1, causes["SurfaceCpuReadback.Data"]);
			Assert.Equal (1, causes["Surface.Handle"]);
		} finally {
			OptimumGuiMetrics.Reset ();
			OptimumGuiMetrics.Enabled = wasEnabled;
		}
	}

	[Fact]
	public void MaterializationDiagnosticsBoundTriggerKindsAndReset()
	{
		using var observer = SubscribeMaterializationMetricsForTest ();
		bool wasEnabled = OptimumGuiMetrics.Enabled;
		try {
			OptimumGuiMetrics.Enabled = true;
			OptimumGuiMetrics.Reset ();
			for (int i = 0; i < 35; i++) {
				using var surface = new ImageSurface (Format.Argb32, 2, 2);
				surface.BeginRecording ().FillRectangle (0, 0, 1, 1, 1, 0, 0, 1);
				surface.MaterializeRecorder (IntPtr.Zero, "cause-" + i, "test");
			}
			var causes = OptimumGuiMetrics.GetMaterializationDiagnostics ();
			Assert.Equal (33, causes.Count); // 32 explicit keys plus the bounded overflow bucket.
			Assert.Equal (3, causes["other-trigger-kinds"]);
			Assert.Equal (35, OptimumGuiMetrics.MaterializationAttemptCount);
			OptimumGuiMetrics.ResetMaterializationDiagnostics ();
			Assert.Empty (OptimumGuiMetrics.GetMaterializationDiagnostics ());
			Assert.Equal (0, OptimumGuiMetrics.MaterializationAttemptCount);
		} finally {
			OptimumGuiMetrics.Reset ();
			OptimumGuiMetrics.Enabled = wasEnabled;
		}
	}

	private static IDisposable SubscribeMaterializationMetricsForTest ()
	{
		Type recorderType = typeof (ImageSurface).Assembly.GetType ("Cairo.SurfaceRecorder", throwOnError: true)!;
		EventInfo materialized = recorderType.GetEvent ("MaterializationCompleted", BindingFlags.Static | BindingFlags.NonPublic)!;
		var callback = (Action<string, bool>)typeof (OptimumGuiMetrics)
			.GetMethod ("RecordMaterialization", BindingFlags.Static | BindingFlags.NonPublic)!
			.CreateDelegate (typeof (Action<string, bool>));
		materialized.GetAddMethod (nonPublic: true)!.Invoke (null, [callback]);
		return new MaterializationMetricsObserver(materialized.GetRemoveMethod (nonPublic: true)!, callback);
	}

	private sealed class MaterializationMetricsObserver (MethodInfo remove, Action<string, bool> callback) : IDisposable
	{
		public void Dispose () => remove.Invoke (null, [callback]);
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
