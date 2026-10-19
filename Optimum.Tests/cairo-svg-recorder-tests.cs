using System;
using System.Runtime.InteropServices;
using Cairo;
using NanoSvg;
using SkiaSharp;
using Xunit;

public sealed class CairoSvgRecorderTests
{
	[Fact]
	public void TiledSolidRectanglesMatchNanoSvgPixels()
	{
		const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='4'><rect width='3' height='4' fill='#3585cd' fill-opacity='.8'/><rect x='3' width='5' height='4' fill='#d7396b' fill-opacity='.45'/></svg>";
		byte[] expected = Native (svg);
		using var picture = RecordedSvgGeometry.Parse (svg, 8, 4, 8, 4, null);
		using var bitmap = new SKBitmap (8, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas (bitmap)) { canvas.Clear (SKColors.Transparent); canvas.DrawPicture (picture); }
		var actual = new byte[8 * 4 * 4]; Marshal.Copy (bitmap.GetPixels (), actual, 0, actual.Length);
		for (int i = 0; i < actual.Length; i += 4) {
			Assert.Equal (expected[i + 3], actual[i + 3]);
			for (int channel = 0; channel < 3; channel++) {
				int straight = actual[i + 3] == 0 ? 0 : actual[i + channel] * 255 / actual[i + 3];
				Assert.InRange (Math.Abs (expected[i + channel] - straight), 0, 1);
			}
		}
	}

	[Fact]
	public void TintedSvgUsesNativeRasterizerUntilPremultipliedAlphaParityIsEstablished ()
	{
		const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='4'><rect width='8' height='4' fill='#3585cd'/></svg>";
		Assert.Throws<NotSupportedException> (() => RecordedSvgGeometry.Parse (svg, 8, 4, 8, 4, unchecked((int)0xB3418FD7)));
	}

	[Theory]
	[InlineData("<rect width='8' height='4' fill='#3585cd' fill-opacity='.8'/>")]
	[InlineData("<rect width='8' height='4' fill='#3585cd' opacity='.5'/>")]
	public void PartiallyTransparentSvgStaysOnNativeTextureUploadPath (string content)
	{
		string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='4'>" + content + "</svg>";
		Assert.Throws<NotSupportedException> (() => RecordedSvgGeometry.Parse (svg, 8, 4, 8, 4, null, opaqueOnly: true));
	}

	[Fact]
	public void OpaqueTiledSvgCanBeRecordedForTextureUpload ()
	{
		const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='4'><rect width='3' height='4' fill='#3585cd'/><rect x='3' width='5' height='4' fill='#d7396b'/></svg>";
		byte[] expected = Native (svg);
		for (int i = 0; i < expected.Length; i += 4) (expected[i], expected[i + 2]) = (expected[i + 2], expected[i]);
		using var picture = RecordedSvgGeometry.Parse (svg, 8, 4, 8, 4, null, opaqueOnly: true);
		using var bitmap = new SKBitmap (8, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas (bitmap)) { canvas.Clear (SKColors.Transparent); canvas.DrawPicture (picture); }
		var actual = new byte[8 * 4 * 4]; Marshal.Copy (bitmap.GetPixels (), actual, 0, actual.Length);
		Assert.Equal (expected, actual);
	}

	[Theory]
	[InlineData("<circle cx='4' cy='2' r='1.5'/>")]
	[InlineData("<rect x='.25' width='8' height='4'/>")]
	[InlineData("<rect width='8' height='4' stroke='red' stroke-width='1'/>")]
	[InlineData("<rect width='8' height='4' fill='none' stroke='red' stroke-width='1'/>")]
	[InlineData("<rect width='8' height='4'/><rect x='2' width='6' height='4'/>")]
	[InlineData("<defs><linearGradient id='g'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue'/></linearGradient></defs><rect width='8' height='4' fill='url(#g)'/>")]
	public void UnsupportedSvgCoverageStaysOnNativeRasterizer (string content)
	{
		string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='4'>" + content + "</svg>";
		Assert.Throws<NotSupportedException> (() => RecordedSvgGeometry.Parse (svg, 8, 4, 8, 4, null));
	}

	[Fact]
	public void NativeMaterializationReplaysSvgOnceWhenReplayReadsSurfacePixels ()
	{
		_ = CairoAPI.Version; // Install the Cairo Linux resolver before first native call.
		using var surface = new ImageSurface (Format.Argb32, 8, 4);
		var recorder = surface.BeginRecording ();
		using var picture = new SKPictureRecorder ();
		var canvas = picture.BeginRecording (SKRect.Create (8, 4));
		canvas.DrawColor (SKColors.Red);
		using var recordedPicture = picture.EndRecording ();
		int replayCount = 0;
		Assert.True (surface.RecordPicture (recordedPicture, 0, 0, context => {
			replayCount++;
			IntPtr pixels = surface.DataPtr;
			Marshal.WriteInt32 (pixels, unchecked((int)0xff224466));
		}));

		byte[] materialized = surface.Data;
		Assert.Equal (1, replayCount);
		Assert.Equal (SurfaceRecordingState.NativeMaterialized, recorder.State);
		Assert.Equal (0x66, materialized[0]);
		Assert.Equal (0x44, materialized[1]);
		Assert.Equal (0x22, materialized[2]);
		Assert.Equal (0xff, materialized[3]);
	}

	static byte[] Native (string svg)
	{
		IntPtr image = SvgNativeMethods.nsvgParse (svg, "px", 96);
		IntPtr rasterizer = SvgNativeMethods.nsvgCreateRasterizer ();
		IntPtr buffer = Marshal.AllocHGlobal (8 * 4 * 4);
		try {
			Assert.NotEqual (IntPtr.Zero, image); Assert.NotEqual (IntPtr.Zero, rasterizer);
			SvgNativeMethods.nsvgRasterize (rasterizer, image, 0, 0, 1, buffer, 8, 4, 8 * 4);
			var result = new byte[8 * 4 * 4]; Marshal.Copy (buffer, result, 0, result.Length);
			return result;
		} finally { Marshal.FreeHGlobal (buffer); SvgNativeMethods.nsvgDeleteRasterizer (rasterizer); SvgNativeMethods.nsvgDelete (image); }
	}
}
