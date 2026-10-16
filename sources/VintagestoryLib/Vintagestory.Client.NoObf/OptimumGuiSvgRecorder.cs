using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Vintagestory.Client.NoObf
{
	public static class OptimumGuiSvgRecorder
	{
		[ThreadStatic] static bool replayingNativeSvg;

		/// <summary>
		/// DrawSvg into a recording surface: Skia rasterizes the SVG into the same straight-alpha
		/// buffer rasterizeSvg returns, and the display list replays the original ColorOver
		/// loop over it. Native materialization replays the original NanoSVG route.
		/// </summary>
		public static bool TryRecord (SvgLoader loader, IAsset asset, ImageSurface target, int x, int y, int width, int height, int? color)
		{
			if (replayingNativeSvg || !OptimumGuiGpuProbe.RenderingRequested || asset == null || target == null || width <= 0 || height <= 0 ||
				asset.Data == null || !target.IsRecordingDisplayList) return false;

			byte[] rgba;
			try { rgba = RecordedSvgGeometry.Rasterize (asset.ToText (), width, height, width, height, color); }
			catch (NotSupportedException) { return false; }
			return target.RecordCommand ("DrawSvg", () => new RecordedLegacyStep (context => {
				bool wasReplaying = replayingNativeSvg;
				replayingNativeSvg = true;
				try {
					using (var surface = (ImageSurface)context.GetTarget ()) loader.DrawSvg (asset, surface, x, y, width, height, color);
				} finally { replayingNativeSvg = wasReplaying; }
			}, RecordedSvgGeometry.DrawSvgOperation (rgba, x, y, width, height, ColorUtil.ColorOver)));
		}

		/// <summary>
		/// LoadSvg on a certified GPU: the rasterizeSvg-equivalent bytes from Skia are written
		/// into a candidate texture through the recorded GPU path. Unsupported SVG content and
		/// any candidate failure return false so the caller uses the vanilla route.
		/// </summary>
		public static bool TryLoad (SvgLoader loader, ICoreClientAPI api, IAsset asset, int textureWidth, int textureHeight,
			int width, int height, int? color, out LoadedTexture texture)
		{
			texture = null;
			if (!OptimumGuiGpuProbe.RenderingRequested || Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId ||
				textureWidth <= 0 || textureHeight <= 0 || asset?.Data == null) return false;
			OptimumGuiGpuProbeBackend.TryRunOnce (RuntimeEnv.MainThreadId);
			if (!OptimumGuiGpuProbe.GuiRenderingEnabled) return false;

			byte[] rgba;
			try { rgba = RecordedSvgGeometry.Rasterize (asset.ToText (), textureWidth, textureHeight, width, height, color); }
			catch (NotSupportedException) { return false; }

			using (var surface = new ImageSurface (Format.Argb32, textureWidth, textureHeight)) {
				surface.BeginRecording ();
				if (!surface.RecordCommand ("LoadSvg", () => new RecordedPixelStep ("LoadSvg",
					RecordedSvgGeometry.TextureOperation (rgba), readBack: false))) return false;
				bool rendered = OptimumGuiGpuProbeBackend.TryCreateCandidateTexture (surface, true, RuntimeEnv.MainThreadId, out int candidate, out string reason, linearMin: true);
				if (OptimumGuiMetrics.Enabled) OptimumGuiMetrics.RecordGpuCandidateTexture (rendered, reason);
				if (!rendered) return false;
				texture = new LoadedTexture (api, candidate, width, height);
				return true;
			}
		}
	}
}
