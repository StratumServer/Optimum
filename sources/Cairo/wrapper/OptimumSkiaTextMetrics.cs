using System;
using System.Collections.Concurrent;
using SkiaSharp;

namespace Cairo
{
	/// <summary>
	/// Skia text measurement for GUI layout, with the signature the patched <c>CairoFont</c> calls. It measures with the
	/// options <c>CairoFont.SetupContext</c> sets (subpixel antialiasing, default hinting), so layout no longer needs a
	/// Cairo context to size text.
	/// </summary>
	public static class OptimumSkiaTextMetrics
	{
		private const int MaxCacheEntries = 4096;
		private static readonly ConcurrentDictionary<FontExtentsKey, FontExtents> fontExtentsCache = new();
		private static readonly ConcurrentDictionary<TextExtentsKey, TextExtents> textExtentsCache = new();

		private readonly struct FontExtentsKey : IEquatable<FontExtentsKey>
		{
			public readonly string Family;
			public readonly FontWeight Weight;
			public readonly FontSlant Slant;
			public readonly double Size;

			public FontExtentsKey(string family, FontWeight weight, FontSlant slant, double size)
			{
				Family = family ?? string.Empty;
				Weight = weight;
				Slant = slant;
				Size = size;
			}

			public bool Equals(FontExtentsKey other) =>
				Weight == other.Weight &&
				Slant == other.Slant &&
				Size.Equals(other.Size) &&
				string.Equals(Family, other.Family, StringComparison.OrdinalIgnoreCase);

			public override bool Equals(object obj) => obj is FontExtentsKey other && Equals(other);

			public override int GetHashCode() =>
				HashCode.Combine(
					StringComparer.OrdinalIgnoreCase.GetHashCode(Family),
					(int)Weight,
					(int)Slant,
					Size);
		}

		private readonly struct TextExtentsKey : IEquatable<TextExtentsKey>
		{
			public readonly string Family;
			public readonly FontWeight Weight;
			public readonly FontSlant Slant;
			public readonly double Size;
			public readonly string Text;

			public TextExtentsKey(string family, FontWeight weight, FontSlant slant, double size, string text)
			{
				Family = family ?? string.Empty;
				Weight = weight;
				Slant = slant;
				Size = size;
				Text = text ?? string.Empty;
			}

			public bool Equals(TextExtentsKey other) =>
				Weight == other.Weight &&
				Slant == other.Slant &&
				Size.Equals(other.Size) &&
				string.Equals(Family, other.Family, StringComparison.OrdinalIgnoreCase) &&
				string.Equals(Text, other.Text, StringComparison.Ordinal);

			public override bool Equals(object obj) => obj is TextExtentsKey other && Equals(other);

			public override int GetHashCode() =>
				HashCode.Combine(
					StringComparer.OrdinalIgnoreCase.GetHashCode(Family),
					(int)Weight,
					(int)Slant,
					Size,
					StringComparer.Ordinal.GetHashCode(Text));
		}

		public static FontExtents GetFontExtents(string family, FontWeight weight, FontSlant slant, double size)
		{
			if (!(size > 0) || double.IsInfinity(size)) return default;
			var key = new FontExtentsKey(family, weight, slant, size);
			if (fontExtentsCache.TryGetValue(key, out FontExtents cached))
				return cached;

			using FontOptions options = LayoutOptions();
			using SKFont font = SkiaGuiFont.Create(family ?? string.Empty, slant, weight, size, options);
			FontExtents extents = SkiaTextMetrics.FontExtents(font, options);

			if (fontExtentsCache.Count > MaxCacheEntries)
				fontExtentsCache.Clear();
			fontExtentsCache.TryAdd(key, extents);
			return extents;
		}

		public static TextExtents GetTextExtents(string family, FontWeight weight, FontSlant slant, double size, string text)
		{
			if (string.IsNullOrEmpty(text) || !(size > 0) || double.IsInfinity(size)) return default;
			var key = new TextExtentsKey(family, weight, slant, size, text);
			if (textExtentsCache.TryGetValue(key, out TextExtents cached))
				return cached;

			using FontOptions options = LayoutOptions();
			using SKFont font = SkiaGuiFont.Create(family ?? string.Empty, slant, weight, size, options);
			TextExtents extents = SkiaTextMetrics.TextExtents(font, options, text);

			if (textExtentsCache.Count > MaxCacheEntries)
				textExtentsCache.Clear();
			textExtentsCache.TryAdd(key, extents);
			return extents;
		}

		static FontOptions LayoutOptions() => FontOptions.FromValues(Antialias.Subpixel, HintStyle.Default, HintMetrics.Default, SubpixelOrder.Default);
	}
}
