using System;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Captures GDI's glyph coverage for Windows Cairo text rendering.</summary>
	internal static class WindowsGdiTextCoverage
	{
		internal static bool TryCapture(ManagedGraphicsState state, SKFont font, FontOptions options, string text,
			RecordedGuiClip[] clips, int width, int height, SKRect? textInkClip, out SKImage coverage, out PointD origin)
		{
			coverage = null;
			origin = default;
			if (!OperatingSystem.IsWindows() || state == null || font?.Typeface == null || options == null ||
				string.IsNullOrEmpty(text) || text.Length > 4096 || width <= 0 || height <= 0 ||
				ContainsSurrogate(text))
				return false;

			try {
				coverage = RecordedFreeTypeText.Capture(state, font, options, text, clips ?? Array.Empty<RecordedGuiClip>(), width, height, out origin);
				return coverage != null;
			} catch {
				coverage?.Dispose();
				coverage = null;
				return false;
			}
		}

		static bool ContainsSurrogate(string text)
		{
			foreach (char value in text) if (char.IsSurrogate(value)) return true;
			return false;
		}
	}
}
