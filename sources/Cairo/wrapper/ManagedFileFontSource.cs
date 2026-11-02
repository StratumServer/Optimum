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

		static readonly IntPtr NativeOwnerKey = Marshal.AllocHGlobal(1);
		static readonly CairoDestroyFunc ReleaseNativeOwner = Release;
		static void Release(IntPtr pointer)
		{
			GCHandle anchor = GCHandle.FromIntPtr(pointer);
			try { ((NativeOwner)anchor.Target).Dispose(); }
			finally { anchor.Free(); }
		}

		// Only explicit native-handle interoperability and the native oracle use this route.
		internal IntPtr CreateNativeHandle()
		{
			if (!CairoNativeGuard.NativeAllowed) throw new NotSupportedException("A native font handle requires the explicit libcairo compatibility backend.");
			var owner = new NativeOwner(bytes);
			IntPtr handle = IntPtr.Zero;
			GCHandle anchor = default;
			try {
				handle = NativeMethods.cairo_ft_font_face_create_for_ft_face(owner.Face, LoadOptions);
				if (handle == IntPtr.Zero || NativeMethods.cairo_font_face_status(handle) != Status.Success)
					throw new InvalidOperationException("Cairo could not create the explicit file-font handle.");
				anchor = GCHandle.Alloc(owner);
				Status status = NativeMethods.cairo_font_face_set_user_data(handle, NativeOwnerKey, GCHandle.ToIntPtr(anchor), ReleaseNativeOwner);
				if (status != Status.Success) throw new InvalidOperationException("Cairo could not retain the file-font buffer: " + status);
				return handle;
			} catch {
				if (handle != IntPtr.Zero) NativeMethods.cairo_font_face_destroy(handle);
				if (anchor.IsAllocated) anchor.Free();
				owner.Dispose();
				throw;
			}
		}

		sealed class NativeOwner : IDisposable
		{
			GCHandle pin;
			IntPtr library;
			internal IntPtr Face;
			internal NativeOwner(byte[] bytes)
			{
				try {
					pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
					if (Util.FreeTypeFontFace.FT_Init_FreeType(out library) != 0 ||
						FT_New_Memory_Face(library, pin.AddrOfPinnedObject(), bytes.Length, 0, out Face) != 0)
						throw new InvalidOperationException("FreeType could not open the retained font data.");
				} catch { Dispose(); throw; }
			}
			public void Dispose()
			{
				if (Face != IntPtr.Zero) { Util.FreeTypeFontFace.FT_Done_Face(Face); Face = IntPtr.Zero; }
				if (library != IntPtr.Zero) { FT_Done_FreeType(library); library = IntPtr.Zero; }
				if (pin.IsAllocated) pin.Free();
			}
		}
		[DllImport("freetype6", CallingConvention = CallingConvention.Cdecl)] static extern int FT_New_Memory_Face(IntPtr library, IntPtr bytes, nint length, nint faceIndex, out IntPtr face);
		[DllImport("freetype6", CallingConvention = CallingConvention.Cdecl)] static extern int FT_Done_FreeType(IntPtr library);
	}
}
