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
		public static bool TryRecord (SvgLoader loader, IAsset asset, ImageSurface target, int x, int y, int width, int height, int? color)
		{
			if (!OptimumGuiGpuProbe.RenderingRequested) return false;
			if (asset == null || target == null || target.RecordingState != SurfaceRecordingState.Recording ||
				width <= 0 || height <= 0 ||
				x < 0 || y < 0 || (long)x + width > target.Width || (long)y + height > target.Height || asset.Data == null)
				return false;

			string text = asset.ToText ();
			if (string.IsNullOrWhiteSpace (text)) return false;

			RecordedPreparationCache.Picture picture;
			try { picture = RecordedSvgGeometry.Acquire (text, width, height, width, height); }
			catch (NotSupportedException) { return false; }
			catch (InvalidOperationException) { return false; }

			if (!target.RecordPicture (picture.Value, x, y, width, height, color,
				_ => loader?.DrawSvg (asset, target, x, y, width, height, color),
				textureColorOrder: false,
				pictureOwner: picture)) {
				picture.Dispose ();
				return false;
			}
			return true;
		}

		public static bool TryLoad (SvgLoader loader, ICoreClientAPI api, IAsset asset, int textureWidth, int textureHeight,
			int width, int height, int? color, out LoadedTexture texture)
		{
			texture = null;
			if (!OptimumGuiGpuProbe.RenderingRequested) return false;
			if (api == null || asset == null || asset.Data == null ||
				Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId ||
				textureWidth <= 0 || textureHeight <= 0 || width < 0 || height < 0)
				return false;
			OptimumGuiGpuProbeBackend.TryRunOnce (RuntimeEnv.MainThreadId);
			if (!OptimumGuiGpuProbe.GuiRenderingEnabled) return false;

			string text = asset.ToText ();
			if (string.IsNullOrWhiteSpace (text)) return false;

			SKPicture picture;
			try { picture = RecordedSvgGeometry.Parse (text, textureWidth, textureHeight, width, height, color, textureColorOrder: true); }
			catch (NotSupportedException) { return false; }
			catch (InvalidOperationException) { return false; }

			bool transferred = false;
			try {
				using (var surface = new ImageSurface (Format.Argb32, textureWidth, textureHeight)) {
					surface.BeginRecording ();
					transferred = surface.RecordPicture (picture, 0, 0, textureWidth, textureHeight, color,
						_ => { },
						textureColorOrder: true);
					if (!transferred) return false;
					bool previousLinearMinOverride = OptimumGuiGpuProbeBackend.LinearMinOverride;
					OptimumGuiGpuProbeBackend.LinearMinOverride = true;
					bool rendered;
					int candidate = 0;
					string reason = null;
					try {
						rendered = OptimumGuiGpuProbeBackend.TryCreateCandidateTexture (surface, true, RuntimeEnv.MainThreadId, out candidate, out reason);
					} finally {
						OptimumGuiGpuProbeBackend.LinearMinOverride = previousLinearMinOverride;
					}
					if (OptimumGuiMetrics.Enabled) OptimumGuiMetrics.RecordGpuCandidateTexture (rendered, reason);
					if (!rendered || candidate <= 0) return false;
					texture = new LoadedTexture (api, candidate, width, height);
					return true;
				}
			} finally { if (!transferred) picture.Dispose (); }
		}
	}
}
