using System;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

public sealed class CairoSolidColorTests
{
	static CairoSolidColorTests() { _ = CairoAPI.Version; }

	[Theory]
	[InlineData(Operator.Clear)]
	[InlineData(Operator.Source)]
	[InlineData(Operator.In)]
	[InlineData(Operator.Out)]
	[InlineData(Operator.DestIn)]
	[InlineData(Operator.DestAtop)]
	public void OperatorsNotBoundedBySourceFallBackAndPreserveNativeCoverage(Operator operation)
	{
		foreach (double alpha in new[] { 0.0, .5, .8 })
		foreach (bool background in new[] { false, true }) {
			using var native = new ImageSurface(Format.Argb32, 8, 8);
			_ = native.Handle;
			using var recorded = new ImageSurface(Format.Argb32, 8, 8);
			recorded.BeginRecording();
			foreach (var surface in new[] { native, recorded }) {
				using var context = new Context(surface);
				if (background) { context.SetSourceRGBA(.2, .4, .6, 1); context.Paint(); }
				context.Save();
				context.Rectangle(1, 2, 5, 4); context.Clip();
				context.Operator = operation;
				context.SetSourceRGBA(.8, .2, .4, .5);
				context.PaintWithAlpha(alpha);
				context.Restore();
				context.SetSourceRGBA(.3, .7, .1, 1);
				context.Rectangle(0, 0, 1, 1); context.Fill();
			}
			Assert.Equal(SurfaceRecordingState.NativeMaterialized, recorded.RecordingState);
			Assert.Equal(native.Data, recorded.Data);
		}
	}

	[Theory]
	[InlineData(Operator.Source)]
	[InlineData(Operator.Over)]
	public void RecordedSolidColorsPreserveNativePremultipliedBytes(Operator operation)
	{
		const int width = 256, height = 16;
		using var native = new ImageSurface(Format.Argb32, width, height);
		_ = native.Handle;
		using var recorded = new ImageSurface(Format.Argb32, width, height);
		recorded.BeginRecording();
		foreach (var surface in new[] { native, recorded }) {
			var random = new Random(130);
			using var context = new Context(surface);
			context.Antialias = Antialias.None;
			context.Operator = operation;
			for (int y = 0; y < height; y++)
			for (int x = 0; x < width; x++) {
				double alpha = y == 1 ? Math.Max(0, (x * 256 - .5001) / 65535)
					: y == 2 ? (x * 256 + .5001) / 65535 : x / 255.0;
				double r = random.NextDouble(), g = random.NextDouble(), b = random.NextDouble();
				if (y == 0) { r = x % 2; g = 1; b = 0; }
				if (y == 3) { r = -1; g = 2; b = .5; }
				context.SetSourceRGBA(r, g, b, alpha);
				context.Rectangle(x, y, 1, 1); context.Fill();
			}
		}
		using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas(bitmap)) Assert.True(recorded.TryDrawRecordedCommands(canvas));
		var actual = new byte[width * height * 4];
		Marshal.Copy(bitmap.GetPixels(), actual, 0, actual.Length);
		byte[] expected = native.Data;
		if (operation == Operator.Source) Assert.Equal(expected, actual);
		else {
			// Skia's CPU SrcOver compositor can lose one low-alpha channel level
			// even with exact source bytes. The live GPU oracle is checked separately.
			for (int i = 0; i < actual.Length; i++)
				Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
		}
	}
}
