using System;
using System.Threading;

namespace Vintagestory.API.Client
{
	/// <summary>Opt-in contract and pure readback validation for the experimental GUI GPU renderer.</summary>
	public static class OptimumGuiGpuProbe
	{
		public const int ReadbackWidth = 8;
		public const int ReadbackHeight = 8;
		private static int enabled = string.Equals(Environment.GetEnvironmentVariable("OPTIMUM_GUI_GPU_PROBE"), "1", StringComparison.Ordinal) ? 1 : 0;

		/// <summary>True only when configured and the current GL context passed the backend probe.</summary>
		public static bool GuiRenderingEnabled => Vintagestory.API.Config.OptimumConfig.EffectiveGuiGpuRender;

		/// <summary>True when configured through OptimumConfig or the temporary environment override.</summary>
		public static bool RenderingRequested => Vintagestory.API.Config.OptimumConfig.GuiGpuRenderRequested;

		internal static bool IsSoftwareRenderer(string renderer)
		{
			if (string.IsNullOrEmpty(renderer)) return false;
			return renderer.IndexOf("llvmpipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
			       renderer.IndexOf("softpipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
			       renderer.IndexOf("software rasterizer", StringComparison.OrdinalIgnoreCase) >= 0 ||
			       renderer.IndexOf("swiftshader", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		/// <summary>Enables the one-shot diagnostic probe. Disabled by default.</summary>
		public static bool Enabled
		{
			get => Volatile.Read(ref enabled) != 0;
			set => Volatile.Write(ref enabled, value ? 1 : 0);
		}

		/// <summary>Validates the fixed RGBA8 Skia readback pattern without using OpenGL or Skia.</summary>
		public static bool ValidateReadback(byte[] pixels, out string failure)
		{
			failure = null;
			if (pixels == null || pixels.Length != ReadbackWidth * ReadbackHeight * 4)
			{
				failure = "Readback size mismatch.";
				return false;
			}
			if (!Matches(pixels, 1, 6, 240, 20, 30, 255) ||
			    !Matches(pixels, 6, 6, 20, 230, 40, 255) ||
			    !Matches(pixels, 1, 1, 30, 40, 240, 255) ||
			    !Matches(pixels, 6, 1, 120, 80, 40, 128))
			{
				failure = "Readback did not match expected orientation, RGBA channels, or premultiplied alpha.";
				return false;
			}
			return true;
		}

		private static bool Matches(byte[] pixels, int x, int y, int red, int green, int blue, int alpha)
		{
			int index = (y * ReadbackWidth + x) * 4;
			return Near(pixels[index], red) && Near(pixels[index + 1], green) &&
			       Near(pixels[index + 2], blue) && Near(pixels[index + 3], alpha);
		}

		private static bool Near(byte actual, int expected) => Math.Abs(actual - expected) <= 2;
	}
}
