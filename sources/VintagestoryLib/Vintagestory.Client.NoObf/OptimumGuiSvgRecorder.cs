using System;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Vintagestory.Client.NoObf
{
	public static class OptimumGuiSvgRecorder
	{
		[ThreadStatic] static bool replayingNativeSvg;

		public static bool TryRecord (SvgLoader loader, IAsset asset, ImageSurface target, int x, int y, int width, int height, int? color)
		{
			if (replayingNativeSvg || !OptimumGuiGpuProbe.RenderingRequested || asset == null || target == null || width <= 0 || height <= 0 ||
				x < 0 || y < 0 || (long)x + width > target.Width || (long)y + height > target.Height || asset.Data == null) return false;

			SKPicture picture;
			try { picture = RecordedSvgGeometry.Parse (asset.ToText (), width, height, width, height, color); }
			catch (NotSupportedException) { return false; }
			bool transferred = false;
			try {
				transferred = target.RecordPicture (picture, x, y, context => {
					bool wasReplaying = replayingNativeSvg;
					replayingNativeSvg = true;
					try { loader.DrawSvg (asset, target, x, y, width, height, color); }
					finally { replayingNativeSvg = wasReplaying; }
				});
				return transferred;
			} finally { if (!transferred) picture.Dispose (); }
		}

		public static bool TryLoad (SvgLoader loader, ICoreClientAPI api, IAsset asset, int textureWidth, int textureHeight,
			int width, int height, int? color, out LoadedTexture texture)
		{
			texture = null;
			if (!OptimumGuiGpuProbe.RenderingRequested || Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId ||
				textureWidth <= 0 || textureHeight <= 0) return false;
			OptimumGuiGpuProbeBackend.TryRunOnce (RuntimeEnv.MainThreadId);
			if (!OptimumGuiGpuProbe.GuiRenderingEnabled) return false;

			SKPicture picture;
			try { picture = RecordedSvgGeometry.Parse (asset.ToText (), textureWidth, textureHeight, width, height, color, opaqueOnly: true); }
			catch (NotSupportedException) { return false; }

			bool transferred = false;
			try {
				using (var surface = new ImageSurface (Format.Argb32, textureWidth, textureHeight)) {
					surface.BeginRecording ();
					transferred = surface.RecordPicture (picture, 0, 0, context => {
						byte[] rgba = loader.rasterizeSvg (asset, textureWidth, textureHeight, width, height, color);
						for (int i = 0; i < rgba.Length; i += 4) { byte red = rgba[i]; rgba[i] = rgba[i + 2]; rgba[i + 2] = red; }
						using (var target = (ImageSurface)context.GetTarget ()) {
							Marshal.Copy (rgba, 0, target.DataPtr, rgba.Length);
							target.MarkDirty ();
						}
					});
					if (!transferred) return false;
					bool rendered = OptimumGuiGpuProbeBackend.TryCreateCandidateTexture (surface, true, RuntimeEnv.MainThreadId, out int candidate, out string reason, linearMin: true);
					if (OptimumGuiMetrics.Enabled) OptimumGuiMetrics.RecordGpuCandidateTexture (rendered, reason);
					if (!rendered) return false;
					texture = new LoadedTexture (api, candidate, width, height);
					return true;
				}
			} finally { if (!transferred) picture.Dispose (); }
		}
	}
}
