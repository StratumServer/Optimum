using System;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Cairo
{
	/// <summary>Immutable font bytes retained independently of the caller's file and FontFace lifetime.</summary>
	internal sealed class ManagedFileFontSource
	{
		readonly byte[] bytes;
		internal readonly SKTypeface Typeface;
		internal readonly int LoadOptions;
		internal string Family => string.IsNullOrWhiteSpace(Typeface.FamilyName) ? "Unknown" : Typeface.FamilyName;
		internal FontSlant Slant => Typeface.IsItalic ? FontSlant.Italic : FontSlant.Normal;
		internal FontWeight Weight => Typeface.IsBold ? FontWeight.Bold : FontWeight.Normal;

		internal ManagedFileFontSource(string filename, int loadOptions)
		{
			LoadOptions = loadOptions;
			try {
				bytes = File.ReadAllBytes(filename);
				using var data = SKData.CreateCopy(bytes);
				Typeface = SKTypeface.FromData(data) ?? throw new InvalidDataException("The file has no readable font face.");
			} catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is ArgumentException || exception is UnauthorizedAccessException) {
				throw new Exception(filename, exception);
			}
		}

		internal void ApplyLoadOptions(SKFont font, FontOptions options)
		{
			if ((LoadOptions & 2 /* FT_LOAD_NO_HINTING */) != 0) font.Hinting = SKFontHinting.None;
			else if (options.HintStyle == HintStyle.Default && ((LoadOptions >> 16) & 15) == 1) font.Hinting = SKFontHinting.Slight;
			if ((LoadOptions & 32 /* FT_LOAD_FORCE_AUTOHINT */) != 0) font.ForceAutoHinting = true;
			if ((LoadOptions & 8 /* FT_LOAD_NO_BITMAP */) != 0) font.EmbeddedBitmaps = false;
			if ((LoadOptions & 4096 /* FT_LOAD_MONOCHROME */) != 0 || ((LoadOptions >> 16) & 15) == 2)
				font.Edging = SKFontEdging.Alias;
		}

		[DllImport("freetype6", CallingConvention = CallingConvention.Cdecl)] static extern int FT_New_Memory_Face(IntPtr library, IntPtr bytes, nint length, nint faceIndex, out IntPtr face);
		[DllImport("freetype6", CallingConvention = CallingConvention.Cdecl)] static extern int FT_Done_FreeType(IntPtr library);
	}
}
