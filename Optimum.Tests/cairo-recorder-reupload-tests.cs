using System;
using System.Collections.Generic;
using Cairo;
using Xunit;

/// <summary>GuiElementTextButton-style reuse: upload, ctx.Clear(), draw, upload again.</summary>
[Collection ("Cairo materialization metrics")]
public sealed class CairoRecorderReuploadTests
{
	static CairoRecorderReuploadTests () { _ = CairoAPI.Version; }

	static void Button (Context context, double shade)
	{
		context.SetSourceRGBA (.4, .3, .2, 1);
		context.Rectangle (0, 0, 30, 12);
		context.Fill ();
		context.SetSourceRGBA (0, 0, 0, shade);
		context.Rectangle (0, 0, 30, 12);
		context.Fill ();
	}

	[Fact]
	public void DrawingAfterGpuCommitAppendsAndClearShortensTheList ()
	{
		var events = new List<string> ();
		Action<string, bool> observer = (trigger, ok) => events.Add (trigger);
		SurfaceRecorder.MaterializationCompleted += observer;
		try {
			using var surface = new ImageSurface (Format.Argb32, 30, 12);
			SurfaceRecorder recorder = surface.BeginRecording ();
			using var context = new Context (surface);
			Button (context, 0);
			CairoRecorderParityTests.RenderSkia (surface, 30, 12);
			Assert.True (surface.CommitGpuRenderedCommands ());

			context.Clear ();               // Paint with Operator.Source after the upload
			Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
			Button (context, .4);
			byte[] second = CairoRecorderParityTests.RenderSkia (surface, 30, 12);
			Assert.True (surface.CommitGpuRenderedCommands ());
			byte[] third = CairoRecorderParityTests.RenderSkia (surface, 30, 12); // unchanged re-upload
			Assert.Equal (second, third);
			Assert.True (surface.CommitGpuRenderedCommands ());

			byte[] expected = CairoRecorderParityTests.RenderNative (30, 12, c => { Button (c, 0); c.Clear (); Button (c, .4); });
			CairoRecorderParityTests.AssertWithinTolerance (expected, second, 30, 12, edgeAllowance: false);
			Assert.Empty (events);
			Assert.Equal (expected, surface.Data); // native replay of the shortened list is exact
		} finally { SurfaceRecorder.MaterializationCompleted -= observer; }
	}

	[Fact]
	public void FullClearInvalidatesTheSourceSnapshotCache ()
	{
		using var source = new ImageSurface (Format.Argb32, 4, 4);
		SurfaceRecorder recorder = source.BeginRecording ();
		using var writer = new Context (source);
		writer.SetSourceRGBA (1, 0, 0, 1); writer.Paint ();
		SurfaceRecorderSnapshot red = recorder.CaptureSnapshot ();
		writer.Clear (); // the list becomes [clear]: same length as [red paint]
		SurfaceRecorderSnapshot cleared = recorder.CaptureSnapshot ();
		Assert.Equal (red.Count, cleared.Count);
		Assert.NotSame (red, cleared);
	}

	[Fact]
	public void ContextCreatedAfterGpuCommitKeepsRecording ()
	{
		using var surface = new ImageSurface (Format.Argb32, 30, 12);
		SurfaceRecorder recorder = surface.BeginRecording ();
		using (var context = new Context (surface)) Button (context, 0);
		CairoRecorderParityTests.RenderSkia (surface, 30, 12);
		Assert.True (surface.CommitGpuRenderedCommands ());
		using (var context = new Context (surface)) Button (context, .2);
		Assert.Equal (SurfaceRecordingState.Recording, recorder.State);
		byte[] expected = CairoRecorderParityTests.RenderNative (30, 12, c => { Button (c, 0); Button (c, .2); });
		Assert.Equal (expected, surface.Data);
	}

	[Fact]
	public void NeverDrawnNativeSurfacesAreBitmapOnlyUploads ()
	{
		CairoGpuPolicy.AutoRecordOverride = false;
		try {
			using var bitmap = new ImageSurface (Format.Argb32, 4, 4);
			System.Runtime.InteropServices.Marshal.WriteInt32 (bitmap.DataPtr, 0, -1);
			Assert.True (bitmap.IsCpuPixelBuffer);
			Assert.Equal ("not-eligible-at-creation", bitmap.RecordingOrigin);
			using (var context = new Context (bitmap)) context.Paint ();
			Assert.False (bitmap.IsCpuPixelBuffer);

			using var rgb = new ImageSurface (Format.Rgb24, 4, 4);
			Assert.Equal ("format-Rgb24", rgb.RecordingOrigin);
			using var external = new ImageSurface (new byte[64], Format.Argb32, 4, 4, 16);
			Assert.Equal ("native-constructor", external.RecordingOrigin);
			Assert.True (external.IsCpuPixelBuffer);
			_ = external.Handle;
			Assert.False (external.IsCpuPixelBuffer);
		} finally { CairoGpuPolicy.AutoRecordOverride = null; }
	}
}
