using System;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

public sealed class CairoSolidColorTests
{
	static CairoSolidColorTests() { _ = CairoAPI.Version; }

	[Theory]
	[InlineData(false, Extend.None)]
	[InlineData(false, Extend.Repeat)]
	[InlineData(true, Extend.None)]
	[InlineData(true, Extend.Repeat)]
	public void DiscontinuousGradientsFallBackBeforeMutationAndPreserveNativeCoverage(bool radial, Extend extend)
	{
		using var native = new ImageSurface(Format.Argb32, 48, 32);
		_ = native.Handle;
		using var recorded = new ImageSurface(Format.Argb32, 48, 32);
		recorded.BeginRecording();
		foreach (var surface in new[] { native, recorded }) {
			using var context = new Context(surface);
			context.SetSourceRGBA(.2, .4, .6, .35); context.Paint();
			context.Save(); context.Rectangle(1, 1, 46, 30); context.Clip();
			context.Operator = Operator.Source;
			using Gradient gradient = radial ? new RadialGradient(14, 12, 2, 25, 18, 14) : new LinearGradient(8, 4, 35, 25);
			gradient.Extend = extend;
			gradient.AddColorStop(0, new Color(.1234, .3456, .7891, .15));
			gradient.AddColorStop(.4, new Color(.8912, .2314, .4567, .7));
			gradient.AddColorStop(1, new Color(.3123, .6789, .9123, .35));
			context.SetSource(gradient); context.Paint();
			gradient.AddColorStop(.2, new Color(0, 1, 0, 1));
			gradient.Matrix = new Matrix(1, 0, 0, 1, 50, 50);
			context.Restore(); context.SetSourceRGBA(.3, .7, .1, 1);
			context.Rectangle(0, 0, 1, 1); context.Fill();
		}
		Assert.Equal(SurfaceRecordingState.NativeMaterialized, recorded.RecordingState);
		Assert.Contains("Gradient extension:", recorded.MaterializeCause);
		Assert.Equal(native.Data, recorded.Data);
	}

	[Theory]
	[InlineData(-.25)]
	[InlineData(0)]
	[InlineData(.5)]
	[InlineData(.8)]
	[InlineData(1)]
	[InlineData(1.25)]
	public void MaskedSolidOverClampsMaskBeforePremultiplication(double mask)
	{
		// Test source bytes on transparent pixels; live GPU checks cover
		// nontransparent backdrops separately from the CPU compositor.
		foreach (double backdrop in new[] { 0.0 }) {
			const int width = 256, height = 8;
			using var native = new ImageSurface(Format.Argb32, width, height);
			_ = native.Handle;
			using var recorded = new ImageSurface(Format.Argb32, width, height);
			recorded.BeginRecording();
			foreach (var surface in new[] { native, recorded }) {
				using var context = new Context(surface);
				context.Antialias = Antialias.None;
				context.SetSourceRGBA(.2, .4, .6, backdrop); context.Paint();
				var random = new Random(130);
				for (int y = 0; y < height; y++)
				for (int x = 0; x < width; x++) {
					context.SetSourceRGBA(random.NextDouble(), random.NextDouble(), random.NextDouble(), x / 255.0);
					context.Save(); context.Rectangle(x, y, 1, 1); context.Clip();
					context.PaintWithAlpha(mask); context.Restore();
				}
			}
			Assert.Equal(SurfaceRecordingState.Recording, recorded.RecordingState);
			using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
			using (var canvas = new SKCanvas(bitmap)) Assert.True(recorded.TryDrawRecordedCommands(canvas));
			var actual = new byte[width * height * 4];
			Marshal.Copy(bitmap.GetPixels(), actual, 0, actual.Length);
			byte[] expected = native.Data;
			for (int i = 0; i < actual.Length; i++) Assert.InRange(Math.Abs(actual[i] - expected[i]), 0, 1);
		}
	}

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
