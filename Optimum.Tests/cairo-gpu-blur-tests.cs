using System;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

public sealed class CairoGpuBlurTests
{
	// Run the same shaders in Skia raster for deterministic CI coverage. The GL harness
	// separately verifies GPU execution, GL state, context lifetime, and command ordering.
	[Theory]
	[InlineData(0, -1)]
	[InlineData(.5, 0)]
	[InlineData(1, 1)]
	[InlineData(2, 4)]
	[InlineData(3, -1)]
	[InlineData(3, 0)]
	[InlineData(3, 9)]
	[InlineData(5, 9)]
	[InlineData(8, 0)]
	[InlineData(8, 14)]
	public void BlurShaderPreservesNativeRgbAlphaAndPartialRegion(double range, int edge)
	{
		const int width = 65, height = 47;
		using var expected = new ImageSurface(Format.Argb32, width, height);
		var random = new Random(130);
		using (var context = new Context(expected)) {
			context.Operator = Operator.Source;
			context.Antialias = Antialias.None;
			for (int y = 0; y < height; y++)
			for (int x = 0; x < width; x++) {
				context.SetSourceRGBA(random.Next(256) / 255.0, random.Next(256) / 255.0, random.Next(256) / 255.0, random.Next(256) / 255.0);
				context.Rectangle(x, y, 1, 1); context.Fill();
			}
		}
		byte[] input = expected.Data;
		using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
		Marshal.Copy(input, 0, bitmap.GetPixels(), input.Length);
		using var image = SKImage.FromBitmap(bitmap);
		var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
		using var target = SKSurface.Create(info);
		using var horizontal = SKSurface.Create(info);
		using var vertical = SKSurface.Create(info);
		using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
		target.Canvas.DrawImage(image, 0, 0, paint);
		bool full = edge < 0;
		int left = full ? 0 : 2, top = full ? 0 : 3;
		int right = full ? width : width - 2, bottom = full ? height : height - 3;
		var blur = new RecordedBlur(range, Math.Max(0, edge), left, top, right, bottom, full);
		for (int pass = 0; pass < 3; pass++) {
			using var effect = SKRuntimeEffect.CreateShader(RecordedGpuBlur.ShaderSource(blur.Radius(pass)), out var errors);
			Assert.True(effect != null, errors);
			using (var original = target.Snapshot()) {
				RecordedGpuBlur.DrawPass(effect, original, original, horizontal, paint, left, top, right, bottom, Math.Max(0, edge), true, width, height);
				using var intermediate = horizontal.Snapshot();
				RecordedGpuBlur.DrawPass(effect, intermediate, original, vertical, paint, left, top, right, bottom, Math.Max(0, edge), false, width, height);
			}
			using var result = vertical.Snapshot();
			target.Canvas.DrawImage(result, 0, 0, paint);
		}
		if (full) expected.BlurFull(range); else expected.BlurPartial(range, edge, left, top, right, bottom);
		byte[] native = expected.Data;
		using var pixels = new SKBitmap(info);
		Assert.True(target.ReadPixels(info, pixels.GetPixels(), pixels.RowBytes, 0, 0));
		var actual = new byte[input.Length]; Marshal.Copy(pixels.GetPixels(), actual, 0, actual.Length);
		for (int i = 0; i < actual.Length; i++) {
			int difference = Math.Abs(actual[i] - native[i]);
			Assert.True(difference <= 1, $"Pixel {i / 4 % width},{i / 4 / width}, channel {i % 4}: shader={actual[i]} native={native[i]}");
			if (i % 4 == 3) Assert.Equal(native[i], actual[i]);
		}
	}
}
