using System;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

public sealed class CairoDirectSkiaBackingTests
{
	[Fact]
	public void DirectSkiaBackingCreatesRasterSurfaceAndCanvas()
	{
		using var surface = new ImageSurface(Format.Argb32, 64, 48);
		surface.EnableDirectSkiaBacking();

		Assert.True(surface.HasDirectBacking);
		Assert.NotNull(surface.DirectSurface);
		Assert.NotNull(surface.DirectCanvas);
		Assert.Equal(SurfaceRecordingState.Recording, surface.RecordingState);
	}

	[Fact]
	public void DirectContextDrawsDirectlyToRasterMemory()
	{
		using var surface = new ImageSurface(Format.Argb32, 32, 32);
		surface.EnableDirectSkiaBacking();

		using (var ctx = new Context(surface))
		{
			ctx.SetSourceRGBA(1.0, 0.0, 0.0, 1.0);
			ctx.Rectangle(0, 0, 32, 32);
			ctx.Fill();
		}

		IntPtr dataPtr = surface.DataPtr;
		Assert.NotEqual(IntPtr.Zero, dataPtr);

		// Read back pixel at (16, 16) - should be fully opaque red in Bgra8888 (0xFFFF0000)
		int pixel = Marshal.ReadInt32(dataPtr, 16 * surface.Stride + 16 * 4);
		uint color = unchecked((uint)pixel);
		byte alpha = (byte)((color >> 24) & 0xFF);
		byte red = (byte)((color >> 16) & 0xFF);
		byte green = (byte)((color >> 8) & 0xFF);
		byte blue = (byte)(color & 0xFF);

		Assert.Equal(255, alpha);
		Assert.Equal(255, red);
		Assert.Equal(0, green);
		Assert.Equal(0, blue);
	}

	[Fact]
	public void TryDrawRecordedCommandsBlitsDirectSurfaceToTarget()
	{
		using var surface = new ImageSurface(Format.Argb32, 32, 32);
		surface.EnableDirectSkiaBacking();

		using (var ctx = new Context(surface))
		{
			ctx.SetSourceRGBA(0.0, 1.0, 0.0, 1.0);
			ctx.Rectangle(4, 4, 24, 24);
			ctx.Fill();
		}

		var info = new SKImageInfo(32, 32, SKColorType.Bgra8888, SKAlphaType.Premul);
		using var target = SKSurface.Create(info);
		Assert.True(surface.TryDrawRecordedCommands(target));

		using var snapshot = target.Snapshot();
		using var bmp = SKBitmap.FromImage(snapshot);
		SKColor centerPixel = bmp.GetPixel(16, 16);

		Assert.Equal(255, centerPixel.Alpha);
		Assert.Equal(0, centerPixel.Red);
		Assert.Equal(255, centerPixel.Green);
		Assert.Equal(0, centerPixel.Blue);
	}

	[Fact]
	public void SaveRestoreStackPreservesGraphicsState()
	{
		using var surface = new ImageSurface(Format.Argb32, 32, 32);
		surface.EnableDirectSkiaBacking();

		using var ctx = new Context(surface);
		ctx.LineWidth = 2.0;
		ctx.Tolerance = 0.25;
		ctx.FillRule = FillRule.EvenOdd;

		ctx.Save();
		ctx.LineWidth = 8.0;
		ctx.Tolerance = 0.5;
		ctx.FillRule = FillRule.Winding;

		Assert.Equal(8.0, ctx.LineWidth);
		Assert.Equal(0.5, ctx.Tolerance);
		Assert.Equal(FillRule.Winding, ctx.FillRule);

		ctx.Restore();

		Assert.Equal(2.0, ctx.LineWidth);
		Assert.Equal(0.25, ctx.Tolerance);
		Assert.Equal(FillRule.EvenOdd, ctx.FillRule);
	}

	[Fact]
	public void TextDrawingAdvancesCurrentPoint()
	{
		using var surface = new ImageSurface(Format.Argb32, 128, 64);
		surface.EnableDirectSkiaBacking();

		using var ctx = new Context(surface);
		ctx.SelectFontFace("Sans", FontSlant.Normal, FontWeight.Normal);
		ctx.SetFontSize(16.0);
		ctx.MoveTo(10, 30);

		Assert.Equal(10.0, ctx.CurrentPoint.X);
		Assert.Equal(30.0, ctx.CurrentPoint.Y);

		ctx.ShowText("Optimum");

		Assert.True(ctx.CurrentPoint.X > 10.0);
		Assert.Equal(30.0, ctx.CurrentPoint.Y);
	}

	[Fact]
	public void DirectBackingSupportsDemulAlpha()
	{
		using var surface = new ImageSurface(Format.Argb32, 16, 16);
		surface.EnableDirectSkiaBacking();

		using (var ctx = new Context(surface))
		{
			// Semi-transparent white: 50% alpha
			ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.5);
			ctx.Rectangle(0, 0, 16, 16);
			ctx.Fill();
		}

		surface.DemulAlpha();
		IntPtr dataPtr = surface.DataPtr;
		Assert.NotEqual(IntPtr.Zero, dataPtr);

		int pixel = Marshal.ReadInt32(dataPtr, 0);
		uint color = unchecked((uint)pixel);
		byte b0 = (byte)(color & 0xFF);
		byte b1 = (byte)((color >> 8) & 0xFF);
		byte b2 = (byte)((color >> 16) & 0xFF);
		byte b3 = (byte)((color >> 24) & 0xFF);

		// DemulAlpha preserves alpha channel (~128) and scales premultiplied RGB channels back to ~255
		Assert.True(b0 > 120 && b0 < 135, $"b0 was {b0}");
		Assert.True(b1 > 240, $"b1 was {b1}");
		Assert.True(b2 > 240, $"b2 was {b2}");
		Assert.True(b3 > 240, $"b3 was {b3}");
	}

	[Fact]
	public void GlobalDirectSkiaBackingConfigEnablesDirectBackingAutomatically()
	{
		bool previous = ImageSurface.DirectSkiaBackingEnabled;
		try
		{
			ImageSurface.DirectSkiaBackingEnabled = true;
			using var surface = new ImageSurface(Format.Argb32, 32, 32);

			Assert.True(surface.HasDirectBacking);
			Assert.NotNull(surface.DirectSurface);
			Assert.NotNull(surface.DirectCanvas);
		}
		finally
		{
			ImageSurface.DirectSkiaBackingEnabled = previous;
		}
	}
}
