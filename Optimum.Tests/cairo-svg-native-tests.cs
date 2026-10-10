using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Cairo;
using NanoSvg;
using SkiaSharp;
using Xunit;

public sealed class CairoSvgNativeTests
{
	[Fact]
	public void RectangleQualificationHasABoundedOverlapBudget()
	{
		var svg = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='257' height='4'>");
		for (int x = 0; x < 257; x++) svg.Append($"<rect x='{x}' width='1' height='4'/>");
		svg.Append("</svg>");
		var exception = Assert.Throws<NotSupportedException>(() => RecordedSvgGeometry.Parse(svg.ToString(), 257, 4, 257, 4));
		Assert.Contains("rectangle budget", exception.Message);
	}

	[Theory]
	[InlineData("<rect x='1' y='1' width='6' height='2'/>")]
	[InlineData("<circle cx='4' cy='2' r='1.5'/>")]
	[InlineData("<rect x='.3' y='.7' width='6' height='2'/>")]
	[InlineData("<rect width='8' height='4' stroke='red' stroke-width='1'/>")]
	[InlineData("<rect width='8' height='4' fill-opacity='.35'/><rect x='2' width='6' height='4' fill-opacity='.7'/>")]
	public void UnqualifiedCoverageAndShapeCompositionUseNativeRasterization(string shapes)
	{
		string svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='8' height='4'>{shapes}</svg>";
		Assert.Throws<NotSupportedException>(() => RecordedSvgGeometry.Parse(svg, 8, 4, 8, 4));
	}

	[Fact]
	public void NativeViewportScaleDeterminesPixelAlignment()
	{
		const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='4'><rect width='3' height='4'/><rect x='3' width='5' height='4'/></svg>";
		using var aligned = RecordedSvgGeometry.Parse(svg, 16, 8, 16, 8);
		Assert.Throws<NotSupportedException>(() => RecordedSvgGeometry.Parse(svg, 12, 6, 12, 6));
		Assert.Throws<NotSupportedException>(() => RecordedSvgGeometry.Parse(svg, 16, 12, 16, 12));
	}

	[Theory]
	[InlineData("linear", "pad")]
	[InlineData("linear", "repeat")]
	[InlineData("linear", "reflect")]
	[InlineData("radial", "pad")]
	[InlineData("radial", "repeat")]
	[InlineData("radial", "reflect")]
	public void NativeGradientTableMustNotBecomeAContinuousSkiaGradient(string kind, string spread)
	{
		string svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='8' height='4'><defs><{kind}Gradient id='g' spreadMethod='{spread}'><stop offset='0' stop-color='#3585cd'/><stop offset='1' stop-color='#d7396b'/></{kind}Gradient></defs><rect width='8' height='4' fill='url(#g)'/></svg>";
		var exception = Assert.Throws<NotSupportedException>(() => RecordedSvgGeometry.Parse(svg, 8, 4, 8, 4));
		Assert.Contains("gradient requires native rasterization", exception.Message);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(.8)]
	[InlineData(.35)]
	[InlineData(.0039)]
	public void RecordedSolidPaintUsesNativeOpacityAndPremultipliedBytes(double opacity)
	{
		foreach (double fillAlpha in new[] { 1.0, .5, .35 }) {
			string svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='8' height='4'><rect width='8' height='4' fill='#3585cd' fill-opacity='{fillAlpha.ToString(CultureInfo.InvariantCulture)}' opacity='{opacity.ToString(CultureInfo.InvariantCulture)}'/></svg>";
			byte[] expected = Native(svg);
			using var picture = RecordedSvgGeometry.Parse(svg, 8, 4, 8, 4);
			using var bitmap = new SKBitmap(8, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
			using (var canvas = new SKCanvas(bitmap)) { canvas.Clear(SKColors.Transparent); canvas.DrawPicture(picture); }
			var actual = new byte[8 * 4 * 4]; Marshal.Copy(bitmap.GetPixels(), actual, 0, actual.Length);
			for (int i = 0; i < actual.Length; i += 4) {
				Assert.Equal(expected[i + 3], actual[i + 3]);
				for (int c = 0; c < 3; c++) {
					int straight = actual[i + 3] == 0 ? 0 : actual[i + c] * 255 / actual[i + 3];
					Assert.Equal(expected[i + c], straight);
				}
			}
		}
	}

	[Theory]
	[InlineData(0)]
	[InlineData(.35)]
	public void SvgOverlayReconstructsIntegerColorBytesBeforeLegacyComposition(double backdropAlpha)
	{
		const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='4'><rect width='8' height='4' fill='#3585cd' fill-opacity='.35' opacity='.8'/></svg>";
		byte[] native = Native(svg);
		using var picture = RecordedSvgGeometry.Parse(svg, 8, 4, 8, 4);
		using var source = SKSurface.Create(new SKImageInfo(8, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
		source.Canvas.DrawPicture(picture);
		using var image = source.Snapshot();
		using var cairo = new ImageSurface(Format.Argb32, 8, 4);
		using (var context = new Context(cairo)) { context.SetSourceRGBA(.2, .4, .6, backdropAlpha); context.Paint(); }
		byte[] backdrop = cairo.Data;
		using var bitmap = new SKBitmap(8, 4, SKColorType.Bgra8888, SKAlphaType.Premul);
		Marshal.Copy(backdrop, 0, bitmap.GetPixels(), backdrop.Length);
		using var original = SKImage.FromBitmap(bitmap);
		using var target = SKSurface.Create(new SKImageInfo(8, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
		using var effect = SKRuntimeEffect.CreateShader(RecordedGpuBlur.ImageOverlayShaderSource, out string errors);
		Assert.True(effect != null, errors);
		RecordedGpuBlur.DrawOverlay(effect, image, original, target, 0, 0, 8, 4, 8, 4, new SKSamplingOptions(SKFilterMode.Nearest), null, false, true);
		using var actual = new SKBitmap(8, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
		Assert.True(target.ReadPixels(actual.Info, actual.GetPixels(), actual.RowBytes, 0, 0));
		var pixels = new byte[native.Length]; Marshal.Copy(actual.GetPixels(), pixels, 0, pixels.Length);
		for (int i = 0; i < pixels.Length; i += 4) {
			int over = native[i] | native[i + 1] << 8 | native[i + 2] << 16 | native[i + 3] << 24;
			int under = backdrop[i + 2] | backdrop[i + 1] << 8 | backdrop[i] << 16 | backdrop[i + 3] << 24;
			int expected = Vintagestory.API.MathTools.ColorUtil.ColorOver(over, under);
			for (int c = 0; c < 4; c++) Assert.InRange(Math.Abs(pixels[i + c] - ((expected >> (8 * c)) & 255)), 0, 1);
		}
	}

	static byte[] Native(string svg)
	{
		IntPtr image = SvgNativeMethods.nsvgParse(svg, "px", 96);
		IntPtr rasterizer = SvgNativeMethods.nsvgCreateRasterizer();
		IntPtr buffer = Marshal.AllocHGlobal(8 * 4 * 4);
		try {
			Assert.NotEqual(IntPtr.Zero, image); Assert.NotEqual(IntPtr.Zero, rasterizer);
			SvgNativeMethods.nsvgRasterize(rasterizer, image, 0, 0, 1, buffer, 8, 4, 8 * 4);
			var result = new byte[8 * 4 * 4]; Marshal.Copy(buffer, result, 0, result.Length); return result;
		} finally { Marshal.FreeHGlobal(buffer); SvgNativeMethods.nsvgDeleteRasterizer(rasterizer); SvgNativeMethods.nsvgDelete(image); }
	}
}
