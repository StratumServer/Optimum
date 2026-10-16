using System;
using System.Runtime.InteropServices;
using Cairo;
using NanoSvg;
using SkiaSharp;
using Vintagestory.API.MathTools;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoSvgRecorderTests
{
	const string Svg = "<svg xmlns='http://www.w3.org/2000/svg' width='{0}' height='{1}'>{2}</svg>";
	static string Document (int width, int height, string content) => string.Format (Svg, width, height, content);

	[Theory]
	[InlineData (null)]
	[InlineData (unchecked((int)0xff112233))]
	[InlineData (0x7fffffff)]
	[InlineData (unchecked((int)0xB3418FD7))]
	public void SolidCoverageMatchesRasterizeSvgBytes (int? tint)
	{
		// Pharos LoadSvg fixture plus tiled translucent rectangles.
		foreach (string svg in new[] {
			Document (16, 12, "<rect width='16' height='12' fill='#c26017'/>"),
			Document (8, 4, "<rect width='3' height='4' fill='#3585cd' fill-opacity='.8'/><rect x='3' width='5' height='4' fill='#d7396b' fill-opacity='.45'/>"),
			Document (16, 12, "<rect x='2' y='2' width='12' height='8' fill='#ff0000'/>")
		}) {
			int width = svg.Contains ("width='8'") ? 8 : 16, height = width == 8 ? 4 : 12;
			byte[] expected = NativeRasterize (svg, width, height, width, height, tint);
			byte[] actual = RecordedSvgGeometry.Rasterize (svg, width, height, width, height, tint);
			Assert.Equal (expected.Length, actual.Length);
			for (int i = 0; i < actual.Length; i++)
				Assert.True (Math.Abs (expected[i] - actual[i]) <= 1, $"byte {i}: skia={actual[i]} nanosvg={expected[i]} tint={tint}");
		}
	}

	[Theory]
	[InlineData ("<circle cx='12' cy='12' r='8.5' fill='#3a7bd5'/>")]
	[InlineData ("<path d='M2 20 C6 2 14 2 22 20 Z' fill='#d5603a' fill-opacity='.7' stroke='#203040' stroke-width='1.5'/>")]
	[InlineData ("<rect x='3' y='3' width='14' height='10' rx='3' fill='none' stroke='#ffffff' stroke-width='2' stroke-linejoin='round'/><rect x='6' y='8' width='12' height='12' fill='#22aa55' opacity='.5'/>")]
	[InlineData ("<path fill-rule='evenodd' d='M2 2 H22 V22 H2 Z M8 8 H16 V16 H8 Z' fill='#884422'/>")]
	public void CurvedAndStrokedIconsStayWithinAntialiasParity (string content)
	{
		string svg = Document (24, 24, content);
		foreach (int? tint in new int?[] { null, unchecked((int)0xffeeddcc) }) {
			foreach (int size in new[] { 24, 36, 48 }) {
				byte[] expected = Premultiplied (NativeRasterize (svg, size, size, size, size, tint));
				byte[] actual = Premultiplied (RecordedSvgGeometry.Rasterize (svg, size, size, size, size, tint));
				CairoRecorderParityTests.AssertLocalAa (expected, actual, size, size);
			}
		}
	}

	[Theory]
	[InlineData ("tiled", "<rect width='24' height='32' fill='#3585cd'/><rect x='24' width='24' height='32' fill='#d7396b'/>", null)]
	[InlineData ("alpha", "<rect width='48' height='32' fill='#3585cd' fill-opacity='.5'/>", null)]
	[InlineData ("tint", "<rect width='48' height='32' fill='#3585cd'/>", unchecked((int)0xb73585cd))]
	[InlineData ("stroke", "<rect width='48' height='32' fill='#3585cd' stroke='#d7396b' stroke-width='2'/>", null)]
	public void PharosLoadSvgDiagnosticCasesMatchNanoSvg (string name, string content, int? tint)
	{
		// The cases of Optimum.Pharos.SvgDiagnostic at its three texture sizes.
		string svg = Document (48, 32, content);
		foreach ((int width, int height) in new[] { (48, 32), (72, 48), (96, 64) }) {
			byte[] expected = NativeRasterize (svg, width, height, width, height, tint);
			byte[] actual = RecordedSvgGeometry.Rasterize (svg, width, height, width, height, tint);
			// Flat pixels within one level; only the stroke has antialiased edge pixels.
			CairoRecorderParityTests.AssertWithinTolerance (expected, actual, width, height, edgeAllowance: name == "stroke");
		}
	}

	[Theory]
	[InlineData ("<defs><linearGradient id='g'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue'/></linearGradient></defs><rect width='8' height='4' fill='url(#g)'/>")]
	[InlineData ("<rect width='8' height='4' fill='none' stroke='red' stroke-width='1' stroke-dasharray='1 1'/>")]
	public void GradientAndDashedSvgStayOnNativeRasterizer (string content)
	{
		Assert.Throws<NotSupportedException> (() => RecordedSvgGeometry.Rasterize (Document (8, 4, content), 8, 4, 8, 4, null));
	}

	[Theory]
	[InlineData (null)]
	[InlineData (unchecked((int)0xff112233))]
	public void DrawSvgIntoRecordingSurfaceMatchesVanillaColorOver (int? tint)
	{
		// Pharos fixtures 3 and 5: DrawSvg over recorded content, without native rasterization.
		_ = CairoAPI.Version;
		string svg = Document (16, 12, "<rect x='2' y='2' width='12' height='8' fill='#ff0000'/>");
		Action<Context> prefix = context => {
			context.SetSourceRGBA (.16, .58, .92, .75);
			context.Antialias = Antialias.None;
			context.Rectangle (2.25, 1.5, 11, 9);
			context.Fill ();
		};
		CairoRecorderParityTests.AssertParity (16, 12, (context, surface) => {
			prefix (context);
			if (surface.IsRecordingDisplayList) {
				byte[] rgba = RecordedSvgGeometry.Rasterize (svg, 16, 12, 16, 12, tint);
				Assert.True (surface.RecordCommand ("DrawSvg", () => new RecordedLegacyStep (
					replay => VanillaDrawSvg (svg, (ImageSurface)replay.GetTarget (), 0, 0, 16, 12, tint),
					RecordedSvgGeometry.DrawSvgOperation (rgba, 0, 0, 16, 12, ColorUtil.ColorOver))));
			} else VanillaDrawSvg (svg, surface, 0, 0, 16, 12, tint);
		}, edgeAllowance: false);
	}

	[Fact]
	public void LoadSvgTextureOperationStoresRasterizeSvgBytesAsLogicalRgba ()
	{
		_ = CairoAPI.Version;
		string svg = Document (16, 12, "<rect width='16' height='12' fill='#c26017'/>");
		byte[] rgba = RecordedSvgGeometry.Rasterize (svg, 16, 12, 16, 12, 0x7fffffff);
		using var surface = new ImageSurface (Format.Argb32, 16, 12);
		surface.BeginRecording ();
		Assert.True (surface.RecordCommand ("LoadSvg", () => new RecordedPixelStep ("LoadSvg", RecordedSvgGeometry.TextureOperation (rgba), readBack: false)));
		using SKSurface target = SKSurface.Create (new SKImageInfo (16, 12, SKColorType.Rgba8888, SKAlphaType.Premul));
		Assert.True (surface.TryDrawRecordedCommands (target));
		var texels = new byte[16 * 12 * 4];
		GCHandle pin = GCHandle.Alloc (texels, GCHandleType.Pinned);
		try { Assert.True (target.ReadPixels (new SKImageInfo (16, 12, SKColorType.Rgba8888, SKAlphaType.Premul), pin.AddrOfPinnedObject (), 16 * 4, 0, 0)); }
		finally { pin.Free (); }
		Assert.Equal (rgba, texels); // Straight alpha survives the premultiplied target unchanged.
	}

	static byte[] Premultiplied (byte[] straight)
	{
		var result = new byte[straight.Length];
		for (int i = 0; i < straight.Length; i += 4) {
			int a = straight[i + 3];
			result[i] = (byte)(straight[i] * a / 255); result[i + 1] = (byte)(straight[i + 1] * a / 255);
			result[i + 2] = (byte)(straight[i + 2] * a / 255); result[i + 3] = (byte)a;
		}
		return result;
	}

	// SvgLoader.rasterizeSvg with the native NanoSVG rasterizer (vanilla reference).
	static byte[] NativeRasterize (string svg, int textureWidth, int textureHeight, int width, int height, int? color)
	{
		byte[] tint = color.HasValue ? ColorUtil.ToRGBABytes (color.Value) : null;
		IntPtr image = SvgNativeMethods.nsvgParse (svg, "px", 96f);
		IntPtr rasterizer = SvgNativeMethods.nsvgCreateRasterizer ();
		try {
			NsvgSize size = Marshal.PtrToStructure<NsvgSize> (SvgNativeMethods.nsvgImageGetSize (image));
			float scale; int x = 0, y = 0;
			if (width == 0 && height == 0) scale = 1f;
			else if (width == 0) scale = (float)height / size.height;
			else if (height == 0) scale = (float)width / size.width;
			else {
				float sx = (float)width / size.width, sy = (float)height / size.height;
				scale = sx < sy ? sx : sy;
				x = (int)((float)textureWidth - size.width * scale) / 2;
				y = (int)((float)textureHeight - size.height * scale) / 2;
			}
			byte[] pixels = new byte[textureWidth * textureHeight * 4];
			GCHandle pin = GCHandle.Alloc (pixels, GCHandleType.Pinned);
			try { SvgNativeMethods.nsvgRasterize (rasterizer, image, x, y, scale, pin.AddrOfPinnedObject (), textureWidth, textureHeight, textureWidth * 4); }
			finally { pin.Free (); }
			if (tint != null) {
				for (int i = 0; i < pixels.Length - 1; i += 4) {
					float a = (float)(int)pixels[i + 3] / 255f;
					pixels[i] = (byte)(a * (float)(int)tint[0]);
					pixels[i + 1] = (byte)(a * (float)(int)tint[1]);
					pixels[i + 2] = (byte)(a * (float)(int)tint[2]);
					pixels[i + 3] = (byte)(a * (float)(int)tint[3]);
				}
			} else for (int i = 0; i < pixels.Length - 1; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
			return pixels;
		} finally { SvgNativeMethods.nsvgDeleteRasterizer (rasterizer); SvgNativeMethods.nsvgDelete (image); }
	}

	// SvgLoader.DrawSvg (vanilla reference).
	static void VanillaDrawSvg (string svg, ImageSurface into, int posx, int posy, int width, int height, int? color)
	{
		byte[] array = NativeRasterize (svg, width, height, width, height, color);
		var pixels = new int[array.Length / 4];
		Buffer.BlockCopy (array, 0, pixels, 0, array.Length);
		int num = into.Width * into.Height;
		IntPtr destination = into.DataPtr;
		for (int i = 0; i < width; i++)
			for (int j = 0; j < height; j++) {
				int index = (posy + j) * into.Width + posx + i;
				if (index >= 0 && index < num)
					Marshal.WriteInt32 (destination, index * 4, ColorUtil.ColorOver (pixels[j * width + i], Marshal.ReadInt32 (destination, index * 4)));
			}
		into.MarkDirty ();
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
}
