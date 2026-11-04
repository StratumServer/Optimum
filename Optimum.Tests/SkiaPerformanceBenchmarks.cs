using System;
using System.Diagnostics;
using Cairo;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

public sealed class SkiaPerformanceBenchmarks
{
	private readonly ITestOutputHelper output;

	public SkiaPerformanceBenchmarks(ITestOutputHelper output)
	{
		this.output = output;
	}

	[Fact]
	public void BenchmarkShaderCompilationAndLookupOverhead()
	{
		const int iterations = 100;
		// Measure compiling blur shaders and demultiply tables repeatedly
		long startAlloc = GC.GetAllocatedBytesForCurrentThread();
		var sw = Stopwatch.StartNew();

		for (int i = 0; i < iterations; i++)
		{
			for (int radius = 1; radius <= 4; radius++)
			{
				string source = RecordedGpuBlur.ShaderSource(radius);
				using var effect = SKRuntimeEffect.CreateShader(source, out string errors);
				Assert.NotNull(effect);
			}
		}

		sw.Stop();
		long endAlloc = GC.GetAllocatedBytesForCurrentThread();
		output.WriteLine($"[Benchmark] Blur Shader Compilation ({iterations} x 4 radii = {iterations * 4} shaders): {sw.ElapsedMilliseconds} ms, Allocations: {(endAlloc - startAlloc) / 1024} KB");

		// Measure Demultiply table creation
		startAlloc = GC.GetAllocatedBytesForCurrentThread();
		sw.Restart();
		for (int i = 0; i < 10; i++)
		{
			using var table = RecordedGpuBlur.CreateDemultiplyTable();
			Assert.NotNull(table);
		}
		sw.Stop();
		endAlloc = GC.GetAllocatedBytesForCurrentThread();
		output.WriteLine($"[Benchmark] Demultiply Table Creation (10 tables): {sw.ElapsedMilliseconds} ms, Allocations: {(endAlloc - startAlloc) / 1024} KB");

		// Measure multiple RecordedGpuBlur instances reusing static shaders
		startAlloc = GC.GetAllocatedBytesForCurrentThread();
		sw.Restart();
		// In Skia raster, simulate calling Apply on multiple RecordedGpuBlur instances
		for (int i = 0; i < 100; i++)
		{
			// Calling ShaderSource or retrieving cached shader
			for (int radius = 1; radius <= 4; radius++)
			{
				string src = RecordedGpuBlur.ShaderSource(radius);
				Assert.NotNull(src);
			}
		}
		sw.Stop();
		endAlloc = GC.GetAllocatedBytesForCurrentThread();
		output.WriteLine($"[Benchmark] Static Blur Shader Source Reuse (100 passes): {sw.ElapsedMilliseconds} ms, Allocations: {(endAlloc - startAlloc) / 1024} KB");
	}

	[Fact]
	public void BenchmarkTextCoverageCaptureOverhead()
	{
		if (!OperatingSystem.IsWindows()) return;

		string[] uiStrings = new[] { "Singleplayer", "Multiplayer", "Settings", "Back", "Quit Game", "Mods", "Credits" };
		using var options = FontOptions.FromValues(Antialias.Subpixel, HintStyle.Default, HintMetrics.Default, SubpixelOrder.Default);
		var state = new ManagedGraphicsState();
		state.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Normal);
		state.SetFontSize(16.0);
		using var font = SkiaGuiFont.Create(state, options);

		const int iterations = 50;
		long startAlloc = GC.GetAllocatedBytesForCurrentThread();
		var sw = Stopwatch.StartNew();
		int captured = 0;

		for (int i = 0; i < iterations; i++)
		{
			foreach (string text in uiStrings)
			{
				if (WindowsGdiTextCoverage.TryCapture(state, font, options, text, null, 800, 600, null, out SKImage coverage, out _))
				{
					captured++;
					coverage.Dispose();
				}
			}
		}

		sw.Stop();
		long endAlloc = GC.GetAllocatedBytesForCurrentThread();
		output.WriteLine($"[Benchmark] Text Coverage Capture ({captured} texts): {sw.ElapsedMilliseconds} ms, Mean: {(double)sw.ElapsedMilliseconds / captured:F3} ms/text, Allocations: {(endAlloc - startAlloc) / 1024} KB");

		// Measure re-creating SKImage from cached bytes
		byte[] sampleBytes = new byte[64 * 32 * 4];
		var info = new SKImageInfo(64, 32, SKColorType.Rgba8888, SKAlphaType.Opaque);
		startAlloc = GC.GetAllocatedBytesForCurrentThread();
		sw.Restart();
		for (int i = 0; i < captured; i++)
		{
			using var data = SKData.CreateCopy(sampleBytes);
			using var img = SKImage.FromPixels(info, data);
			Assert.NotNull(img);
			var span = img.PeekPixels().GetPixelSpan();
			Assert.Equal(64 * 32 * 4, span.Length);
		}
		sw.Stop();
		endAlloc = GC.GetAllocatedBytesForCurrentThread();
		output.WriteLine($"[Benchmark] Cached Image Reconstitution ({captured} images): {sw.ElapsedMilliseconds} ms, Mean: {(double)sw.ElapsedMilliseconds / captured:F3} ms/image, Allocations: {(endAlloc - startAlloc) / 1024} KB");
	}

	[Fact]
	public void TextCoverageRelativePositionInvarianceTest()
	{
		if (!OperatingSystem.IsWindows()) return;

		using var options = FontOptions.FromValues(Antialias.Subpixel, HintStyle.Default, HintMetrics.Default, SubpixelOrder.Default);
		var state1 = new ManagedGraphicsState();
		state1.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Normal);
		state1.SetFontSize(16.0);
		state1.MoveTo(100.0, 100.0);
		using var font1 = SkiaGuiFont.Create(state1, options);

		var state2 = new ManagedGraphicsState();
		state2.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Normal);
		state2.SetFontSize(16.0);
		state2.MoveTo(250.0, 320.0);
		using var font2 = SkiaGuiFont.Create(state2, options);

		Assert.True(WindowsGdiTextCoverage.TryCapture(state1, font1, options, "Singleplayer", null, 800, 600, null, out SKImage cov1, out PointD origin1));
		Assert.True(WindowsGdiTextCoverage.TryCapture(state2, font2, options, "Singleplayer", null, 800, 600, null, out SKImage cov2, out PointD origin2));

		using (cov1)
		using (cov2)
		{
			Assert.Equal(cov1.Width, cov2.Width);
			Assert.Equal(cov1.Height, cov2.Height);
			// Relative offset from current point
			double relX1 = origin1.X - 100.0;
			double relY1 = origin1.Y - 100.0;
			double relX2 = origin2.X - 250.0;
			double relY2 = origin2.Y - 320.0;
			Assert.Equal(relX1, relX2, 1);
			Assert.Equal(relY1, relY2, 1);

			using var p1 = cov1.ToRasterImage();
			using var p2 = cov2.ToRasterImage();
			var span1 = p1.PeekPixels().GetPixelSpan();
			var span2 = p2.PeekPixels().GetPixelSpan();
			Assert.True(span1.SequenceEqual(span2), "Cached raster pixels match 100%!");
		}
	}
}
