using System;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

public sealed class CairoRetainedSurfaceTests
{
	static CairoRetainedSurfaceTests() { _ = CairoAPI.Version; }

	[Fact]
	public void ClosedSourceCanBeCapturedByAnotherProducerThread()
	{
		using var source = new ImageSurface(Format.Argb32, 8, 8);
		source.BeginRecording();
		using (var writer = new Context(source)) { writer.SetSourceRGBA(1, 0, 0, 1); writer.Paint(); }
		SKColor pixel = default;
		Exception failure = null;
		var producer = new System.Threading.Thread(() => {
			try {
			using var target = new ImageSurface(Format.Argb32, 8, 8);
			target.BeginRecording();
			using (var writer = new Context(target)) { writer.SetSource(source); writer.Paint(); }
			using var bitmap = new SKBitmap(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
			using (var canvas = new SKCanvas(bitmap)) Assert.True(target.TryDrawRecordedCommands(canvas));
			pixel = bitmap.GetPixel(4, 4);
			} catch (Exception exception) { failure = exception; }
		});
		producer.Start(); producer.Join();
		if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
		Assert.Equal(SKColors.Red, pixel);
		Assert.Equal(SurfaceRecordingState.Recording, source.RecordingState);
	}

	[Theory]
	[InlineData(1.0, LineCap.Butt)]
	[InlineData(1.5, LineCap.Butt)]
	[InlineData(2.0, LineCap.Butt)]
	[InlineData(1.0, LineCap.Round)]
	[InlineData(1.5, LineCap.Round)]
	[InlineData(2.0, LineCap.Round)]
	public void ClosedRetracedStrokePreservesCairoCoverage(double scale, LineCap cap)
	{
		using var recorded = new ImageSurface(Format.Argb32, 16, 24); recorded.BeginRecording();
		using var native = new ImageSurface(Format.Argb32, 16, 24);
		foreach (var surface in new[] { recorded, native }) {
			using var context = new Context(surface);
			context.Scale(scale, scale); context.LineCap = cap; context.LineWidth = 1;
			context.SetSourceRGBA(1, 1, 1, 1);
			context.MoveTo(2, 0); context.LineTo(2, 12); context.ClosePath(); context.Stroke();
		}
		using var pixels = new SKBitmap(16, 24, SKColorType.Bgra8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas(pixels)) Assert.True(recorded.TryDrawRecordedCommands(canvas));
		var actual = new byte[16 * 24 * 4]; Marshal.Copy(pixels.GetPixels(), actual, 0, actual.Length);
		byte[] expected = native.Data;
		for (int i = 0; i < actual.Length; i++) Assert.True(Math.Abs(actual[i] - expected[i]) <= 1,
			$"Closed stroke scale {scale}, cap {cap}, byte {i}: GPU geometry={actual[i]}, Cairo={expected[i]}");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void DisposedSourceReflectsSubsequentWritesThroughItsLiveContext(bool exposePatternHandle)
	{
		using var source = new ImageSurface(Format.Argb32, 8, 8); source.BeginRecording();
		using var writer = new Context(source);
		writer.SetSourceRGBA(1, 0, 0, 1); writer.Paint();
		using var pattern = new SurfacePattern(source);
		using var target = new ImageSurface(Format.Argb32, 8, 8); target.BeginRecording();
		using var reader = new Context(target); reader.SetSource(pattern);
		source.Dispose();
		writer.SetSourceRGBA(0, 1, 0, 1); writer.Paint();
		if (exposePatternHandle) _ = pattern.Handle;
		reader.Paint();
		using var bitmap = new SKBitmap(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas(bitmap)) Assert.True(target.TryDrawRecordedCommands(canvas));
		Assert.Equal(SKColors.Lime, bitmap.GetPixel(4, 4));
		Assert.Equal(255, target.Data[(4 * 8 + 4) * 4 + 1]);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void SurfaceSourcesRemainRecordedAcrossBindingMutationAndDisposal(bool patternSource)
	{
		using var target = new ImageSurface(Format.Argb32, 32, 16);
		target.BeginRecording();
		var source = new ImageSurface(Format.Argb32, 8, 8);
		source.BeginRecording();
		using (var sourceContext = new Context(source)) {
			sourceContext.Antialias = Antialias.None;
			sourceContext.SetSourceRGBA(1, 0, 0, 1); sourceContext.Paint();
			using var context = new Context(target);
			context.Antialias = Antialias.None;
			using var pattern = new SurfacePattern(source) { Filter = Filter.Nearest, Extend = Extend.Repeat };
			if (patternSource) context.SetSource(pattern); else context.SetSource(source, 0, 0);
			context.Rectangle(0, 0, 8, 8); context.Fill();
			sourceContext.SetSourceRGBA(0, 1, 0, 1); sourceContext.Paint();
			context.Rectangle(8, 0, 8, 8);
			if (!patternSource) context.SetSource(source, 8, 0);
			context.Fill();
			Assert.Equal(SurfaceRecordingState.Recording, source.RecordingState);
		}
		source.Dispose();
		Assert.Equal(SurfaceRecordingState.Recording, target.RecordingState);
		using var bitmap = new SKBitmap(32, 16, SKColorType.Bgra8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas(bitmap)) Assert.True(target.TryDrawRecordedCommands(canvas));
		Assert.Equal(SKColors.Red, bitmap.GetPixel(2, 2));
		Assert.Equal(SKColors.Lime, bitmap.GetPixel(10, 2));
		byte[] skia = new byte[32 * 16 * 4]; Marshal.Copy(bitmap.GetPixels(), skia, 0, skia.Length);
		Assert.Equal(skia, target.Data);
	}

	[Fact]
	public void DisposingSourceBeforePaintKeepsItsFinalRecording()
	{
		using var target = new ImageSurface(Format.Argb32, 8, 8);
		target.BeginRecording();
		var source = new ImageSurface(Format.Argb32, 8, 8); source.BeginRecording();
		using var context = new Context(target);
		context.SetSource(source);
		using (var drawing = new Context(source)) { drawing.SetSourceRGBA(0, 0, 1, 1); drawing.Paint(); }
		source.Dispose();
		context.Paint();
		using var bitmap = new SKBitmap(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas(bitmap)) Assert.True(target.TryDrawRecordedCommands(canvas));
		Assert.Equal(SKColors.Blue, bitmap.GetPixel(4, 4));
		Assert.Equal(SurfaceRecordingState.Sealed, target.RecordingState);
		byte[] pixels = new byte[8 * 8 * 4]; Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
		Assert.Equal(pixels, target.Data);
	}

	[Fact]
	public void SvgGeometryRecordsNativeViewportPathsColorsAndGradients()
	{
		const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='12'><defs><linearGradient id='g'><stop offset='0' stop-color='#ff0000'/><stop offset='1' stop-color='#0000ff'/></linearGradient></defs><rect x='2' y='2' width='12' height='8' fill='url(#g)'/></svg>";
		using var picture = RecordedSvgGeometry.Parse(svg, 32, 24, 32, 24);
		using var surface = SKSurface.Create(new SKImageInfo(32, 24)); surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.DrawPicture(picture);
		using var pixels = new SKBitmap(32, 24); Assert.True(surface.ReadPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes, 0, 0));
		Assert.Equal(0, pixels.GetPixel(1, 1).Alpha);
		Assert.Equal(255, pixels.GetPixel(8, 12).Alpha); Assert.True(pixels.GetPixel(8, 12).Red > pixels.GetPixel(8, 12).Blue);
		Assert.True(pixels.GetPixel(24, 12).Blue > pixels.GetPixel(24, 12).Red);
	}

	[Fact]
	public void DrawingAfterGpuSubmissionRetainsEarlierCommandsWithoutCpuMaterialization()
	{
		using var surface = new ImageSurface(Format.Argb32, 8, 8); surface.BeginRecording();
		using var context = new Context(surface); context.Antialias = Antialias.None;
		context.SetSourceRGBA(1, 0, 0, 1); context.Paint();
		using var bitmap = new SKBitmap(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas(bitmap)) Assert.True(surface.TryDrawRecordedCommands(canvas));
		Assert.True(surface.CommitGpuRenderedCommands());
		context.SetSourceRGBA(0, 1, 0, 1); context.Rectangle(4, 0, 4, 8); context.Fill();
		Assert.Equal(SurfaceRecordingState.Recording, surface.RecordingState);
		using (var canvas = new SKCanvas(bitmap)) Assert.True(surface.TryDrawRecordedCommands(canvas));
		Assert.Equal(SKColors.Red, bitmap.GetPixel(2, 2)); Assert.Equal(SKColors.Lime, bitmap.GetPixel(6, 2));
		Assert.True(surface.CommitGpuRenderedCommands());
		var pixels = new byte[8 * 8 * 4]; Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
		Assert.Equal(pixels, surface.Data);
	}

	[Fact]
	public void ClosingGpuRenderedSurfaceDoesNotReplayPixelsUnlessTheContextIsUsedAgain()
	{
		var surface = new ImageSurface(Format.Argb32, 8, 8); var recorder = surface.BeginRecording();
		using var context = new Context(surface);
		context.SetSourceRGBA(1, 0, 0, 1); context.Paint();
		using (var bitmap = new SKBitmap(8, 8)) using (var canvas = new SKCanvas(bitmap)) Assert.True(surface.TryDrawRecordedCommands(canvas));
		Assert.True(surface.CommitGpuRenderedCommands());
		surface.Dispose();
		IntPtr target = NativeMethods.cairo_get_target(context.NativeHandleForRecorder);
		var bytes = new byte[8 * 8 * 4]; Marshal.Copy(NativeMethods.cairo_image_surface_get_data(target), bytes, 0, bytes.Length);
		Assert.All(bytes, value => Assert.Equal(0, value));
		Assert.Equal(SurfaceRecordingState.Disposed, recorder.State);
		_ = context.Handle;
		Marshal.Copy(NativeMethods.cairo_image_surface_get_data(target), bytes, 0, bytes.Length);
		Assert.Equal(255, bytes[2]); Assert.Equal(255, bytes[3]);
	}

	[Fact]
	public void ImageOverlayShaderPreservesResizeAndLegacyComposition()
	{
		using var bitmap = new SKBitmap(11, 7, SKColorType.Bgra8888, SKAlphaType.Unpremul);
		var random = new Random(130);
		for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
			bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
		using var native = new ImageSurface(Format.Argb32, 32, 24);
		using (var context = new Context(native)) { context.SetSourceRGBA(.2, .4, .6, .8); context.Paint(); }
		using var sourceBitmap = new SKBitmap(32, 24, SKColorType.Bgra8888, SKAlphaType.Premul);
		Marshal.Copy(native.Data, 0, sourceBitmap.GetPixels(), 32 * 24 * 4);
		using var backdrop = SKImage.FromBitmap(sourceBitmap);
		using var image = new RecordedGuiImage(bitmap, 3, 4, 17, 13);
		using var effect = SKRuntimeEffect.CreateShader(RecordedGpuBlur.ImageOverlayShaderSource, out string errors);
		Assert.True(effect != null, errors);
		using var target = SKSurface.Create(sourceBitmap.Info);
		RecordedGpuBlur.DrawImageOverlay(effect, image, backdrop, target, 32, 24);
		native.Image(bitmap, 3, 4, 17, 13);
		using var pixels = new SKBitmap(sourceBitmap.Info);
		Assert.True(target.ReadPixels(sourceBitmap.Info, pixels.GetPixels(), pixels.RowBytes, 0, 0));
		var actual = new byte[32 * 24 * 4]; Marshal.Copy(pixels.GetPixels(), actual, 0, actual.Length);
		byte[] expected = native.Data;
		for (int i = 0; i < actual.Length; i++) Assert.True(Math.Abs(actual[i] - expected[i]) <= 1, $"Image byte {i}: native={expected[i]}, shader={actual[i]}");
	}

	[Fact]
	public void ImageOverlayCapturesAssetPixelsAndKeepsTheSurfaceRecorded()
	{
		using var bitmap = new SKBitmap(4, 4, SKColorType.Bgra8888, SKAlphaType.Unpremul); bitmap.Erase(SKColors.Red);
		using var recorded = new ImageSurface(Format.Argb32, 8, 8); recorded.BeginRecording();
		recorded.Image(bitmap, 2, 2, 4, 4); bitmap.Erase(SKColors.Blue);
		Assert.Equal(SurfaceRecordingState.Recording, recorded.RecordingState);
		using var pixels = new SKBitmap(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas(pixels)) Assert.True(recorded.TryDrawRecordedCommands(canvas));
		Assert.Equal(SKColors.Red, pixels.GetPixel(3, 3));
		var expected = new byte[8 * 8 * 4]; Marshal.Copy(pixels.GetPixels(), expected, 0, expected.Length);
		Assert.Equal(expected, recorded.Data);
	}

	[Fact]
	public void DemultiplyShaderPreservesTheLegacyByteOperation()
	{
		const int width = 256, height = 256;
		using var input = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
		var bytes = new byte[width * height * 4];
		var random = new Random(130);
		for (int i = 0; i < bytes.Length; i += 4) {
			int alpha = random.Next(256);
			bytes[i] = (byte)random.Next(alpha + 1); bytes[i + 1] = (byte)random.Next(alpha + 1);
			bytes[i + 2] = (byte)random.Next(alpha + 1); bytes[i + 3] = (byte)alpha;
		}
		Marshal.Copy(bytes, 0, input.GetPixels(), bytes.Length);
		using var source = SKImage.FromBitmap(input);
		using var result = SKSurface.Create(input.Info);
		using var effect = SKRuntimeEffect.CreateShader(RecordedGpuBlur.DemultiplyShaderSource, out string errors);
		Assert.True(effect != null, errors);
		using var table = RecordedGpuBlur.CreateDemultiplyTable();
		RecordedGpuBlur.DrawDemultiply(effect, source, table, result, width, height);
		using var pixels = new SKBitmap(input.Info);
		Assert.True(result.ReadPixels(input.Info, pixels.GetPixels(), pixels.RowBytes, 0, 0));
		SurfaceTransformDemulAlpha.ApplyToPixels(input.GetPixels(), width, height);
		var expected = new byte[bytes.Length]; var actual = new byte[bytes.Length];
		Marshal.Copy(input.GetPixels(), expected, 0, expected.Length); Marshal.Copy(pixels.GetPixels(), actual, 0, actual.Length);
		for (int i = 0; i < expected.Length; i++) Assert.True(expected[i] == actual[i], $"Byte {i}: native={expected[i]}, shader={actual[i]}");
	}

	[Fact]
	public void DemultiplyRemainsRecordedAndNativeFallbackPreservesItsResult()
	{
		using var recorded = new ImageSurface(Format.Argb32, 8, 8); recorded.BeginRecording();
		using var native = new ImageSurface(Format.Argb32, 8, 8);
		foreach (var surface in new[] { recorded, native }) {
			using (var context = new Context(surface)) { context.SetSourceRGBA(.2, .4, .6, .8); context.Paint(); }
			surface.DemulAlpha();
		}
		Assert.Equal(SurfaceRecordingState.Recording, recorded.RecordingState);
		Assert.Equal(native.Data, recorded.Data);
	}

	[Fact]
	public void ExposingPatternHandleReplaysRecordedPixelsForNativeConsumers()
	{
		using var surface = new ImageSurface(Format.Argb32, 8, 8); surface.BeginRecording();
		using (var context = new Context(surface)) { context.SetSourceRGBA(1, 0, 0, 1); context.Paint(); }
		using var pattern = new SurfacePattern(surface);
		_ = pattern.Handle;
		using var native = new ImageSurface(Format.Argb32, 8, 8);
		using (var context = new Context(native)) { context.SetSource(pattern); context.Paint(); }
		Assert.Equal(surface.Data, native.Data);
	}
}
