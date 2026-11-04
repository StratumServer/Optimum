using System;
using System.Collections.Concurrent;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Captures GDI's glyph coverage for Windows Cairo text rendering with bounded LRU caching.</summary>
	internal static class WindowsGdiTextCoverage
	{
		private const int MaxCacheEntries = 1024;
		private static readonly ConcurrentDictionary<TextCoverageKey, CachedCoverage> Cache = new();

		private sealed class CachedCoverage
		{
			public readonly byte[] Pixels;
			public readonly int Width;
			public readonly int Height;
			public readonly double RelLeft;
			public readonly double RelTop;

			public CachedCoverage(byte[] pixels, int width, int height, double relLeft, double relTop)
			{
				Pixels = pixels;
				Width = width;
				Height = height;
				RelLeft = relLeft;
				RelTop = relTop;
			}
		}

		private readonly struct TextCoverageKey : IEquatable<TextCoverageKey>
		{
			public readonly string Text;
			public readonly string Family;
			public readonly float Size;
			public readonly FontWeight Weight;
			public readonly FontSlant Slant;
			public readonly Antialias Antialias;
			public readonly SubpixelOrder SubpixelOrder;
			public readonly byte PhaseX;
			public readonly byte PhaseY;
			public readonly float ScaleX;
			public readonly float ScaleY;

			public TextCoverageKey(string text, string family, float size, FontWeight weight, FontSlant slant,
				Antialias antialias, SubpixelOrder order, byte phaseX, byte phaseY, float scaleX, float scaleY)
			{
				Text = text;
				Family = family ?? string.Empty;
				Size = size;
				Weight = weight;
				Slant = slant;
				Antialias = antialias;
				SubpixelOrder = order;
				PhaseX = phaseX;
				PhaseY = phaseY;
				ScaleX = scaleX;
				ScaleY = scaleY;
			}

			public bool Equals(TextCoverageKey other) =>
				Size.Equals(other.Size) &&
				Weight == other.Weight &&
				Slant == other.Slant &&
				Antialias == other.Antialias &&
				SubpixelOrder == other.SubpixelOrder &&
				PhaseX == other.PhaseX &&
				PhaseY == other.PhaseY &&
				ScaleX.Equals(other.ScaleX) &&
				ScaleY.Equals(other.ScaleY) &&
				string.Equals(Text, other.Text, StringComparison.Ordinal) &&
				string.Equals(Family, other.Family, StringComparison.OrdinalIgnoreCase);

			public override bool Equals(object obj) => obj is TextCoverageKey other && Equals(other);

			public override int GetHashCode()
			{
				HashCode hc = default;
				hc.Add(Text, StringComparer.Ordinal);
				hc.Add(Family, StringComparer.OrdinalIgnoreCase);
				hc.Add(Size);
				hc.Add((int)Weight);
				hc.Add((int)Slant);
				hc.Add((int)Antialias);
				hc.Add((int)SubpixelOrder);
				hc.Add(PhaseX);
				hc.Add(PhaseY);
				hc.Add(ScaleX);
				hc.Add(ScaleY);
				return hc.ToHashCode();
			}
		}

		internal static bool TryCapture(ManagedGraphicsState state, SKFont font, FontOptions options, string text,
			RecordedGuiClip[] clips, int width, int height, SKRect? textInkClip, out SKImage coverage, out PointD origin)
		{
			coverage = null;
			origin = default;
			if (!OperatingSystem.IsWindows() || state == null || font?.Typeface == null || options == null ||
				string.IsNullOrEmpty(text) || text.Length > 4096 || width <= 0 || height <= 0 ||
				ContainsSurrogate(text))
				return false;

			bool isCacheEligible = (clips == null || clips.Length == 0) &&
				!textInkClip.HasValue && text.Length <= 256;

			state.TryGetCurrentPoint(out double curX, out double curY);
			PointD translation = state.FontTranslation;
			double x = curX + translation.X;
			double y = curY + translation.Y;
			Matrix matrix = state.Matrix;
			double px = x, py = y;
			matrix.TransformPoint(ref px, ref py);
			double roundPx = Math.Floor(px + .5);
			double roundPy = Math.Floor(py + .5);

			TextCoverageKey key = default;
			if (isCacheEligible)
			{
				byte phaseX = (byte)((int)Math.Floor((px - Math.Floor(px)) * 4 + .5) & 3);
				byte phaseY = (byte)((int)Math.Floor((py - Math.Floor(py)) * 4 + .5) & 3);
				key = new TextCoverageKey(text, font.Typeface.FamilyName, font.Size, state.FontWeight, state.FontSlant,
					options.Antialias, options.SubpixelOrder, phaseX, phaseY, (float)matrix.Xx, (float)matrix.Yy);

				if (Cache.TryGetValue(key, out CachedCoverage cached))
				{
					using var data = SKData.CreateCopy(cached.Pixels);
					coverage = SKImage.FromPixels(new SKImageInfo(cached.Width, cached.Height, SKColorType.Rgba8888, SKAlphaType.Opaque), data);
					if (coverage != null)
					{
						origin = new PointD(roundPx + cached.RelLeft, roundPy + cached.RelTop);
						return true;
					}
				}
			}

			try
			{
				coverage = RecordedFreeTypeText.Capture(state, font, options, text, clips ?? Array.Empty<RecordedGuiClip>(), width, height, out origin);
				if (coverage != null && isCacheEligible)
				{
					using var raster = coverage.ToRasterImage();
					var span = raster.PeekPixels().GetPixelSpan();
					if (!span.IsEmpty)
					{
						byte[] pixels = span.ToArray();
						double relLeft = origin.X - roundPx;
						double relTop = origin.Y - roundPy;
						if (Cache.Count >= MaxCacheEntries) Cache.Clear();
						Cache.TryAdd(key, new CachedCoverage(pixels, coverage.Width, coverage.Height, relLeft, relTop));
					}
				}
				return coverage != null;
			}
			catch
			{
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
