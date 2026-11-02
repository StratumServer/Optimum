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
			if (asset == null || target == null || width <= 0 || height <= 0 ||
				x < 0 || y < 0 || (long)x + width > target.Width || (long)y + height > target.Height || asset.Data == null)
				throw new InvalidOperationException("Skia SVG recording received an invalid GUI asset or target.");

			SKPicture picture;
			try { picture = RecordedSvgGeometry.Parse (asset.ToText (), width, height, width, height, color); }
			catch (NotSupportedException exception) { throw new NotSupportedException("The requested SVG uses geometry that the Skia GUI recorder does not support.", exception); }
			bool transferred = false;
			try {
				transferred = target.RecordPicture (picture, x, y, width, height, color,
					_ => throw new InvalidOperationException("Skia SVG display lists cannot be materialized through Cairo."),
					textureColorOrder: false);
				if (!transferred) throw new InvalidOperationException("Skia rejected the GUI SVG display list.");
				return true;
			} finally { if (!transferred) picture.Dispose (); }
		}

		public static bool TryLoad (SvgLoader loader, ICoreClientAPI api, IAsset asset, int textureWidth, int textureHeight,
			int width, int height, int? color, out LoadedTexture texture)
		{
			texture = null;
			if (!OptimumGuiGpuProbe.RenderingRequested) return false;
			if (Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId || textureWidth <= 0 || textureHeight <= 0)
				throw new InvalidOperationException("Skia SVG textures require positive dimensions on the OpenGL context thread.");
			OptimumGuiGpuProbeBackend.TryRunOnce (RuntimeEnv.MainThreadId);

			SKPicture picture;
			try { picture = RecordedSvgGeometry.Parse (asset.ToText (), textureWidth, textureHeight, width, height, color, textureColorOrder: true); }
			catch (NotSupportedException exception) { throw new NotSupportedException("The requested SVG texture uses geometry that the Skia recorder does not support.", exception); }

			bool transferred = false;
			try {
				using (var surface = new ImageSurface (Format.Argb32, textureWidth, textureHeight)) {
					surface.BeginRecording ();
				transferred = surface.RecordPicture (picture, 0, 0, textureWidth, textureHeight, color,
					_ => throw new InvalidOperationException("Skia SVG textures cannot be materialized through Cairo."),
					textureColorOrder: true);
				if (!transferred) throw new InvalidOperationException("Skia rejected the GUI SVG texture display list.");
					bool rendered = OptimumGuiGpuProbeBackend.TryCreateCandidateTexture (surface, true, RuntimeEnv.MainThreadId, out int candidate, out string reason, linearMin: true);
					if (OptimumGuiMetrics.Enabled) OptimumGuiMetrics.RecordGpuCandidateTexture (rendered, reason);
					if (!rendered) throw new InvalidOperationException("Skia could not render the requested SVG texture: " + reason);
					texture = new LoadedTexture (api, candidate, width, height);
					return true;
				}
			} finally { if (!transferred) picture.Dispose (); }
		}
	}
}
