using System;
using Cairo;
using SkiaSharp;
using Xunit;

[Collection("GUI metrics")]
public sealed class CairoSnapshotCompatibilityTests
{
	static CairoSnapshotCompatibilityTests () { _ = CairoAPI.Version; }

	[Fact]
	public void ActiveCairoSaveStackDoesNotBlockSkiaSnapshot ()
	{
		using var actual = ImageSurface.CreateForRecordedGui (Format.Argb32, 16, 12);
		var recorder = actual.BeginRecording ();
		using (var context = new Context (actual)) {
			context.Save ();
			context.SetSourceRGBA (.12, .48, .82, 1);
			context.Rectangle (1.25, 1.5, 10.5, 8.25);
			context.Fill ();
			using var bitmap = new SKBitmap (16, 12, SKColorType.Rgba8888, SKAlphaType.Premul);
			using (var canvas = new SKCanvas (bitmap)) Assert.True (actual.TryDrawRecordedCommands (canvas), recorder.FirstMaterializationTrigger);
		}
		Assert.Equal (SurfaceRecordingState.Sealed, recorder.State);
		Assert.Null (recorder.FirstMaterializationTrigger);
	}

	// The mode is process wide, so this runs only where the process already allows libcairo. Switching the
	// variable inside the test would map libcairo under every native-free test sharing the process.
	[NativeCairoFact]
	public void ManagedSurfacePatternCanBeReplayedThroughExplicitNativeCompatibility ()
	{
		using var source = ImageSurface.CreateForRecordedGui (Format.Argb32, 8, 8);
		source.BeginRecording ().FillRectangle (1, 1, 6, 6, .2, .7, .4, 1);
		using var managedPattern = new SurfacePattern (source);
		using var nativePattern = managedPattern.MaterializeNativeForReplay ();
		Assert.False (nativePattern.IsManagedSource);
		Assert.NotEqual (IntPtr.Zero, nativePattern.Handle);
	}
}
