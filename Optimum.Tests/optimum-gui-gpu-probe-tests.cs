using System;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
	public sealed class OptimumGuiGpuProbeTests
	{
		[Theory]
		[InlineData("llvmpipe (LLVM 20.1.2, 256 bits)")]
		[InlineData("softpipe")]
		[InlineData("Google SwiftShader")]
		public void SoftwareRenderersAreRejectedBeforeSkiaGlInitialization(string renderer)
		{
			Assert.True(OptimumGuiGpuProbe.IsSoftwareRenderer(renderer));
		}

		[Theory]
		[InlineData("AMD Radeon RX 7900 XT")]
		[InlineData("NVIDIA GeForce RTX 4090")]
		public void HardwareRendererNamesAreNotRejected(string renderer)
		{
			Assert.False(OptimumGuiGpuProbe.IsSoftwareRenderer(renderer));
		}

	[Fact]
	public void ReadbackValidationAcceptsExpectedOrientationRgbaAndPremultipliedAlpha()
	{
		byte[] pixels = CreateExpectedReadback();

		Assert.True(OptimumGuiGpuProbe.ValidateReadback(pixels, out string failure), failure);
	}

	[Theory]
	[InlineData(1, 6, 0)] // top-left moved to wrong GL row
	[InlineData(6, 6, 1)] // red/green channels swapped
	[InlineData(6, 1, 3)] // alpha changed
	public void ReadbackValidationRejectsOrientationChannelAndAlphaErrors(int x, int y, int channel)
	{
		byte[] pixels = CreateExpectedReadback();
		pixels[(y * 8 + x) * 4 + channel] ^= 0x7F;

		Assert.False(OptimumGuiGpuProbe.ValidateReadback(pixels, out _));
	}

	[Fact]
	public void TopLeftReadbackRequiresCairoRowOrder()
	{
		// GUI candidates render with a top-left origin so that GL row 0 is canvas row 0,
		// like a Cairo surface uploaded with TexImage2D. A bottom-left render is flipped.
		byte[] bottomLeft = CreateExpectedReadback();
		byte[] topLeft = new byte[bottomLeft.Length];
		for (int y = 0; y < 8; y++) Array.Copy(bottomLeft, (7 - y) * 32, topLeft, y * 32, 32);
		Assert.True(OptimumGuiGpuProbe.ValidateTopLeftReadback(topLeft, out string failure), failure);
		Assert.False(OptimumGuiGpuProbe.ValidateTopLeftReadback(bottomLeft, out _));
	}

	[Fact]
	public void ProbeCanBeDisabledWithoutAContext()
	{
		bool original = OptimumGuiGpuProbe.Enabled;
		try
		{
			OptimumGuiGpuProbe.Enabled = false;
			Assert.False(OptimumGuiGpuProbe.Enabled);
		}
		finally
		{
			OptimumGuiGpuProbe.Enabled = original;
		}
	}

	[Fact]
	public void GuiGpuPreferenceRoundTripsButRequiresAProbedBackend()
	{
		string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-gui-gpu-cfg-" + Guid.NewGuid().ToString("N"));
		System.IO.Directory.CreateDirectory(dir);
		bool original = Vintagestory.API.Config.OptimumConfig.GuiGpuRenderEnabled;
		try
		{
			Vintagestory.API.Config.OptimumConfig.SetDataPath(dir);
			Vintagestory.API.Config.OptimumConfig.GuiGpuRenderEnabled = true;
			Vintagestory.API.Config.OptimumConfig.Save();
			Vintagestory.API.Config.OptimumConfig.GuiGpuRenderEnabled = false;
			Vintagestory.API.Config.OptimumConfig.Load();

			Assert.True(Vintagestory.API.Config.OptimumConfig.GuiGpuRenderEnabled);
			Assert.False(Vintagestory.API.Config.OptimumConfig.EffectiveGuiGpuRender);
		}
		finally
		{
			Vintagestory.API.Config.OptimumConfig.GuiGpuRenderEnabled = original;
			try { System.IO.Directory.Delete(dir, true); } catch { }
		}
	}

	[Fact]
	public void RequestAndBackendProbeCannotBypassCertification()
	{
		var original = Vintagestory.API.Config.OptimumConfig.GuiGpuRenderEnabled;
		try
		{
			Vintagestory.API.Config.OptimumConfig.GuiGpuRenderEnabled = true;
			Vintagestory.API.Config.OptimumConfig.SetGuiGpuCertificationPassed(false, null, null, null, 0, 0, 0, 0);
			Vintagestory.API.Config.OptimumConfig.SetGuiGpuBackendProbePassed(true);
			Assert.False(OptimumGuiGpuProbe.RenderingRequested);
			Assert.False(OptimumGuiGpuProbe.GuiRenderingEnabled);
			Vintagestory.API.Config.OptimumConfig.SetGuiGpuCertificationPassed(true, "vendor", "renderer", "4.6 driver", 4, 6, 2, 0);
			Assert.True(Vintagestory.API.Config.OptimumConfig.GuiGpuDeviceMatches("vendor", "renderer", "4.6 driver", 4, 6, 2, 0));
			Assert.False(Vintagestory.API.Config.OptimumConfig.GuiGpuDeviceMatches("vendor", "other", "4.6 driver", 4, 6, 2, 0));
			Assert.False(Vintagestory.API.Config.OptimumConfig.GuiGpuDeviceMatches("vendor", "renderer", "4.6 driver", 4, 6, 1, 0));
			Assert.False(Vintagestory.API.Config.OptimumConfig.GuiGpuDeviceMatches("vendor", "renderer", "4.6 driver", 4, 6, 2, 1));
			Assert.True(OptimumGuiGpuProbe.RenderingRequested);
			Assert.True(OptimumGuiGpuProbe.GuiRenderingEnabled);
			Vintagestory.API.Config.OptimumConfig.SetGuiGpuBackendProbePassed(false);
			Assert.False(OptimumGuiGpuProbe.GuiRenderingEnabled);
			Vintagestory.API.Config.OptimumConfig.RevokeGuiGpuCertification();
			Assert.False(OptimumGuiGpuProbe.RenderingRequested);
			Assert.False(Vintagestory.API.Config.OptimumConfig.GuiGpuDeviceMatches("vendor", "renderer", "4.6 driver", 4, 6, 2, 0));
		}
		finally
		{
			Vintagestory.API.Config.OptimumConfig.SetGuiGpuCertificationPassed(false, null, null, null, 0, 0, 0, 0);
			Vintagestory.API.Config.OptimumConfig.SetGuiGpuBackendProbePassed(false);
			Vintagestory.API.Config.OptimumConfig.GuiGpuRenderEnabled = original;
		}
	}

	private static byte[] CreateExpectedReadback()
	{
		byte[] pixels = new byte[8 * 8 * 4];
		Set(pixels, 1, 6, 240, 20, 30, 255);
		Set(pixels, 6, 6, 20, 230, 40, 255);
		Set(pixels, 1, 1, 30, 40, 240, 255);
		Set(pixels, 6, 1, 120, 80, 40, 128);
		return pixels;
	}

	private static void Set(byte[] pixels, int x, int y, byte red, byte green, byte blue, byte alpha)
	{
		int index = (y * 8 + x) * 4;
		pixels[index] = red;
		pixels[index + 1] = green;
		pixels[index + 2] = blue;
		pixels[index + 3] = alpha;
	}
}
