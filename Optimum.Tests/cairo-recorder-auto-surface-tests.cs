using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Cairo;
using Vintagestory.API.Config;
using Xunit;

/// <summary>ImageSurface(Format, int, int) starts as a display list while GUI GPU recording
/// is eligible, so dynamic GUI surfaces reach LoadCairoTexture as GPU candidates.</summary>
[Collection ("Cairo materialization metrics")]
public sealed class CairoRecorderAutoSurfaceTests
{
	static CairoRecorderAutoSurfaceTests () { _ = CairoAPI.Version; }

	static T WithAutoRecording<T> (Func<T> body)
	{
		CairoGpuPolicy.AutoRecordOverride = true;
		try { return body (); } finally { CairoGpuPolicy.AutoRecordOverride = null; }
	}

	[Fact]
	public void NewArgb32SurfaceRecordsAndExplicitBeginRecordingKeepsTheList ()
	{
		WithAutoRecording (() => {
			using var surface = new ImageSurface (Format.Argb32, 24, 16);
			Assert.Equal (SurfaceRecordingState.Recording, surface.RecordingState);
			Assert.True (surface.IsAutoRecorded);
			SurfaceRecorder recorder = surface.BeginRecording (); // GuiComposer hook
			using (var context = new Context (surface)) {
				context.SetSourceRGBA (.3, .6, .9, .8);
				context.Rectangle (2, 2, 12, 9);
				context.Fill ();
			}
			Assert.Same (recorder, surface.BeginRecording ());
			byte[] skia = CairoRecorderParityTests.RenderSkia (surface, 24, 16);
			byte[] expected = CairoRecorderParityTests.RenderNative (24, 16, context => {
				context.SetSourceRGBA (.3, .6, .9, .8);
				context.Rectangle (2, 2, 12, 9);
				context.Fill ();
			});
			CairoRecorderParityTests.AssertWithinTolerance (expected, skia, 24, 16, edgeAllowance: false);
			return 0;
		});
	}

	[Fact]
	public void SurfacesStayNativeWithoutEligibilityAndForOtherFormats ()
	{
		CairoGpuPolicy.AutoRecordOverride = false;
		try {
			using var native = new ImageSurface (Format.Argb32, 8, 8);
			Assert.Equal (SurfaceRecordingState.Native, native.RecordingState);
		} finally { CairoGpuPolicy.AutoRecordOverride = null; }
		WithAutoRecording (() => {
			using var rgb = new ImageSurface (Format.Rgb24, 8, 8);
			using var bytes = new ImageSurface (new byte[8 * 8 * 4], Format.Argb32, 8, 8, 32);
			Assert.Equal (SurfaceRecordingState.Native, rgb.RecordingState);
			Assert.Equal (SurfaceRecordingState.Native, bytes.RecordingState);
			return 0;
		});
	}

	[Fact]
	public void FirstContextCanStartRecordingAfterEligibilityChangesButRawDataExposurePreventsIt ()
	{
		CairoGpuPolicy.AutoRecordOverride = false;
		var surface = new ImageSurface (Format.Argb32, 8, 8);
		Assert.Equal (SurfaceRecordingState.Native, surface.RecordingState);
		CairoGpuPolicy.AutoRecordOverride = true;
		try {
			using (surface) {
				using (var context = new Context (surface)) {
					Assert.Equal (SurfaceRecordingState.Recording, surface.RecordingState);
					context.SetSourceRGBA (1, 0, 0, 1);
					context.Paint ();
				}
				Assert.Equal (SurfaceRecordingState.Recording, surface.RecordingState);
			}
		} finally { CairoGpuPolicy.AutoRecordOverride = null; }

		CairoGpuPolicy.AutoRecordOverride = false;
		var pixelBuffer = new ImageSurface (Format.Argb32, 8, 8);
		Marshal.WriteInt32 (pixelBuffer.DataPtr, 0, unchecked((int)0xff336699));
		CairoGpuPolicy.AutoRecordOverride = true;
		try {
			using (pixelBuffer) {
				using var context = new Context (pixelBuffer);
				Assert.Equal (SurfaceRecordingState.Native, pixelBuffer.RecordingState);
				context.SetSourceRGBA (1, 0, 0, 1);
				context.Paint ();
			}
		} finally { CairoGpuPolicy.AutoRecordOverride = null; }
	}

	[Fact]
	public void CpuPixelBufferUseIsNotAMaterialization ()
	{
		// GuiElement.getImageSurfaceFromAsset: new surface, write DataPtr, MarkDirty, upload.
		var events = new List<string> ();
		Action<string, bool> observer = (trigger, ok) => events.Add (trigger);
		SurfaceRecorder.MaterializationCompleted += observer;
		try {
			WithAutoRecording (() => {
				using var surface = new ImageSurface (Format.Argb32, 4, 4);
				Marshal.WriteInt32 (surface.DataPtr, 0, unchecked((int)0xff336699));
				surface.MarkDirty ();
				Assert.Equal (SurfaceRecordingState.NativeMaterialized, surface.RecordingState);
				Assert.True (surface.IsCpuPixelBuffer);
				Assert.Equal (unchecked((int)0xff336699), Marshal.ReadInt32 (surface.DataPtr));
				using (var context = new Context (surface)) { context.SetSourceRGBA (1, 0, 0, 1); context.Paint (); }
				Assert.False (surface.IsCpuPixelBuffer); // Cairo drew into it natively
				return 0;
			});
			Assert.Empty (events);

			WithAutoRecording (() => {
				using var drawn = new ImageSurface (Format.Argb32, 4, 4);
				using (var context = new Context (drawn)) { context.SetSourceRGBA (1, 0, 0, 1); context.Paint (); }
				_ = drawn.DataPtr;
				Assert.False (drawn.IsCpuPixelBuffer);
				return 0;
			});
			Assert.Equal (new[] { "SurfaceCpuReadback.DataPtr" }, events);
		} finally { SurfaceRecorder.MaterializationCompleted -= observer; }
	}

	[Fact]
	public void AutoRecordedSurfacesFollowSequentialThreadHandOff ()
	{
		WithAutoRecording (() => {
			ImageSurface surface = null;
			var worker = new Thread (() => {
				CairoGpuPolicy.AutoRecordOverride = true;
				surface = new ImageSurface (Format.Argb32, 16, 8);
				using var context = new Context (surface);
				context.SetSourceRGBA (.2, .4, .6, 1);
				context.Rectangle (1, 1, 6, 4);
				context.Fill ();
			});
			worker.Start ();
			worker.Join ();
			using (surface) {
				Assert.Equal (SurfaceRecordingState.Recording, surface.RecordingState);
				CairoRecorderParityTests.RenderSkia (surface, 16, 8); // upload on the "main" thread
				Assert.True (surface.CommitGpuRenderedCommands ());
			}
			return 0;
		});
	}
}
