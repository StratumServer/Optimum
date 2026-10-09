using System;
using System.Runtime.InteropServices;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Vintagestory.Client.NoObf;

public static class OptimumGuiSvgRecorder
{
	private static string ReadAsset(IAsset asset)
	{
		if (asset.Data == null) asset.Origin?.LoadAsset(asset);
		if (asset.Data == null) throw new ArgumentNullException(nameof(asset), "SVG asset data is not loaded.");
		return asset.ToText();
	}

	public static bool TryRecord(SvgLoader loader, IAsset asset, ImageSurface target, int x, int y, int width, int height, int? color)
	{
		if (!OptimumGuiGpuProbe.RenderingRequested || target == null || width <= 0 || height <= 0 || x < 0 || y < 0 ||
			(long)x + width > target.Width || (long)y + height > target.Height) return false;
		SkiaSharp.SKPicture picture;
		try { picture = RecordedSvgGeometry.Parse(ReadAsset(asset), width, height, width, height); }
		catch (NotSupportedException) { return false; }
		try {
			if (target.RecordPicture(picture, x, y, width, height, color, context => {
				using (var surface = (ImageSurface)context.GetTarget()) loader.DrawSvg(asset, surface, x, y, width, height, color);
			})) return true;
		} catch { picture.Dispose(); throw; }
		picture.Dispose(); return false;
	}

	public static bool TryLoad(SvgLoader loader, ICoreClientAPI api, IAsset asset, int textureWidth, int textureHeight,
		int width, int height, int? color, out LoadedTexture texture)
	{
		texture = null;
		if (!OptimumGuiGpuProbe.RenderingRequested || Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId || textureWidth <= 0 || textureHeight <= 0) return false;
		OptimumGuiGpuProbeBackend.TryRunOnce(RuntimeEnv.MainThreadId);
		if (!OptimumGuiGpuProbe.GuiRenderingEnabled) return false;
		using (var surface = new ImageSurface(Format.Argb32, textureWidth, textureHeight)) {
			SkiaSharp.SKPicture picture;
			try { picture = RecordedSvgGeometry.Parse(ReadAsset(asset), textureWidth, textureHeight, width, height); }
			catch (NotSupportedException) { return false; }
			bool recorded;
			try {
				recorded = surface.RecordPicture(picture, 0, 0, textureWidth, textureHeight, color, context => {
					byte[] rgba = loader.rasterizeSvg(asset, textureWidth, textureHeight, width, height, color);
					for (int i = 0; i < rgba.Length; i += 4) { byte red = rgba[i]; rgba[i] = rgba[i + 2]; rgba[i + 2] = red; }
					using (var target = (ImageSurface)context.GetTarget()) { Marshal.Copy(rgba, 0, target.DataPtr, rgba.Length); target.MarkDirty(); }
				}, textureColorOrder: true);
			} catch { picture.Dispose(); throw; }
			if (!recorded) { picture.Dispose(); return false; }
			bool rendered = OptimumGuiGpuProbeBackend.TryCreateCandidateTexture(surface, true, RuntimeEnv.MainThreadId, out int candidate, out string reason);
			if (OptimumGuiMetrics.Enabled) OptimumGuiMetrics.RecordGpuCandidateTexture(rendered, reason);
			if (!rendered) return false;
			texture = new LoadedTexture(api, candidate, width, height);
			return true;
		}
	}
}
