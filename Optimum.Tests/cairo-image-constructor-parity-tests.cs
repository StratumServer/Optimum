using System;
using System.IO;
using Path = System.IO.Path;
using Cairo;
using Xunit;

// These tests change the process-wide recording mode used by native oracle tests.
[CollectionDefinition ("Cairo materialization metrics", DisableParallelization = true)]
public sealed class CairoRecorderParityCollection { }

[Collection ("Cairo materialization metrics")]
public sealed class CairoImageConstructorParityTests
{
	[Theory]
	[InlineData (-1, 1, 1, Status.InvalidFormat)]
	[InlineData (99, 1, 1, Status.InvalidFormat)]
	[InlineData (-1, -1, -1, Status.InvalidFormat)]
	[InlineData (0, -1, 1, Status.InvalidSize)]
	[InlineData (0, 1, -1, Status.InvalidSize)]
	[InlineData (0, 32768, 0, Status.InvalidSize)]
	[InlineData (0, 0, 32768, Status.InvalidSize)]
	public void InvalidImageDimensionsReturnCairoErrorSurfaces (int format, int width, int height, Status status)
	{
		bool previous = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = new ImageSurface ((Format)format, width, height);
			Assert.Equal (status, surface.Status);
			Assert.Equal (0, surface.Width); Assert.Equal (0, surface.Height);
			Assert.Equal ((Format)(-1), surface.Format); Assert.Equal (0, surface.Stride);
			Assert.Equal (Content.Color, surface.Content); Assert.Equal (SurfaceType.Image, surface.SurfaceType);
			Assert.Equal (0u, surface.ReferenceCount); Assert.Null (surface.Data); Assert.Equal (IntPtr.Zero, surface.DataPtr);
			using (var context = new Context (surface)) {
				Assert.Equal (status, context.Status);
				context.SetSourceRGB (1, 0, 0); context.Rectangle (0, 0, 2, 2); context.Fill ();
				Assert.Equal (status, context.Status);
			}
			if (CairoNativeGuard.NativeAllowed) {
				IntPtr native = NativeMethods.cairo_image_surface_create ((Format)format, width, height);
				try {
					AssertNativeMetadata (native, surface);
					Surface.ConfigureAutomaticRecording (false);
					using var nativeWrapper = new ImageSurface ((Format)format, width, height);
					AssertNativeMetadata (native, nativeWrapper);
					Surface.ConfigureAutomaticRecording (true);
				}
				finally { NativeMethods.cairo_surface_destroy (native); }
			}
			Assert.Equal (status, surface.Finish ());
			AssertNoNativeLoad (deniedBefore);
		} finally { Surface.ConfigureAutomaticRecording (previous); }
	}

	[Theory]
	[InlineData (0, 0)]
	[InlineData (0, 5)]
	[InlineData (5, 0)]
	public void EmptyImagesKeepNativeDimensionsAndNullPixelStorage (int width, int height)
	{
		bool previous = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = new ImageSurface (Format.Argb32, width, height);
			Assert.Equal (Status.Success, surface.Status);
			Assert.Equal (width, surface.Width); Assert.Equal (height, surface.Height);
			Assert.Equal (Format.Argb32, surface.Format); Assert.Equal (1u, surface.ReferenceCount);
			Assert.Null (surface.Data); Assert.Equal (IntPtr.Zero, surface.DataPtr);
			using (var context = new Context (surface)) {
				context.SetSourceRGB (1, 0, 0); context.Paint (); Assert.Equal (Status.Success, context.Status);
				surface.Flush (); surface.MarkDirty ();
				Assert.Null (surface.Data); Assert.Equal (IntPtr.Zero, surface.DataPtr);
			}
			if (CairoNativeGuard.NativeAllowed) {
				IntPtr native = NativeMethods.cairo_image_surface_create (Format.Argb32, width, height);
				try {
					AssertNativeMetadata (native, surface);
					Surface.ConfigureAutomaticRecording (false);
					using var nativeWrapper = new ImageSurface (Format.Argb32, width, height);
					AssertNativeMetadata (native, nativeWrapper);
					Surface.ConfigureAutomaticRecording (true);
				}
				finally { NativeMethods.cairo_surface_destroy (native); }
			}
			string png = Path.Combine (Path.GetTempPath (), "optimum-empty-image-" + Guid.NewGuid ().ToString ("N") + ".png");
			try { surface.WriteToPng (png); Assert.True (File.Exists (png)); Assert.Equal (0, new FileInfo (png).Length); }
			finally { if (File.Exists (png)) File.Delete (png); }
			Assert.Equal (Status.Success, surface.Finish ());
			AssertNoNativeLoad (deniedBefore);
		} finally { Surface.ConfigureAutomaticRecording (previous); }
	}

	[Theory]
	[InlineData (0)]
	[InlineData (1)]
	[InlineData (2)]
	[InlineData (3)]
	public void PngWriteFailuresKeepTheNativeVoidContract (int scenario)
	{
		bool previous = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		DirectoryInfo directory = Directory.CreateTempSubdirectory ("optimum-png-error-");
		string filename = scenario == 0 ? Path.Combine (directory.FullName, "missing", "image.png") :
			scenario == 1 ? directory.FullName : scenario == 2 ? null : "";
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = new ImageSurface (Format.Argb32, 2, 2);
			using (var context = new Context (surface)) { context.SetSourceRGB (1, 0, 0); context.Paint (); }
			surface.WriteToPng (filename);
			Assert.Equal (Status.Success, surface.Status);
			if (CairoNativeGuard.NativeAllowed) {
				IntPtr native = NativeMethods.cairo_image_surface_create (Format.Argb32, 2, 2);
				try { NativeMethods.cairo_surface_write_to_png (native, filename); Assert.Equal (Status.Success, NativeMethods.cairo_surface_status (native)); }
				finally { NativeMethods.cairo_surface_destroy (native); }
			}
			AssertNoNativeLoad (deniedBefore);
		} finally { Surface.ConfigureAutomaticRecording (previous); directory.Delete (true); }
	}

	[Theory]
	[InlineData (false)]
	[InlineData (true)]
	public void WritingPngKeepsTheSurfaceAvailableForAnotherContext (bool drawBeforePng)
	{
		bool previous = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		string png = Path.Combine (Path.GetTempPath (), "optimum-png-snapshot-" + Guid.NewGuid ().ToString ("N") + ".png");
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = new ImageSurface (Format.Argb32, 2, 2);
			if (drawBeforePng) using (var context = new Context (surface)) { context.SetSourceRGB (1, 0, 0); context.Paint (); }
			surface.WriteToPng (png);
			using (var context = new Context (surface)) { context.SetSourceRGB (0, 0, 1); context.Paint (); }
			Assert.Equal (unchecked ((int)0xff0000ff), BitConverter.ToInt32 (surface.Data, 0));
			using var original = SkiaSharp.SKBitmap.Decode (png);
			Assert.Equal (drawBeforePng ? SkiaSharp.SKColors.Red : new SkiaSharp.SKColor (0, 0, 0, 0), original.GetPixel (0, 0));
			AssertNoNativeLoad (deniedBefore);
		} finally { Surface.ConfigureAutomaticRecording (previous); if (File.Exists (png)) File.Delete (png); }
	}

	[Theory]
	[InlineData (false, 0, 2, Status.InvalidContent)]
	[InlineData (false, (int)Content.ColorAlpha, -1, Status.InvalidSize)]
	[InlineData (true, 0, 2, Status.InvalidSize)]
	[InlineData (true, (int)Content.ColorAlpha, 2, Status.InvalidSize)]
	public void SimilarImagesPreserveContentSizeAndParentErrors (bool parentError, int content, int width, Status expected)
	{
		bool previous = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var parent = new ImageSurface (Format.Argb32, parentError ? -1 : 2, 2);
			using Surface result = parent.CreateSimilar ((Content)content, width, 2);
			Assert.Equal (expected, result.Status); Assert.Equal (0u, result.ReferenceCount);
			if (CairoNativeGuard.NativeAllowed) {
				IntPtr nativeParent = NativeMethods.cairo_image_surface_create (Format.Argb32, parentError ? -1 : 2, 2);
				IntPtr native = NativeMethods.cairo_surface_create_similar (nativeParent, (Content)content, width, 2);
				try { AssertNativeMetadata (native, Assert.IsType<ImageSurface> (result)); }
				finally { NativeMethods.cairo_surface_destroy (native); NativeMethods.cairo_surface_destroy (nativeParent); }
			}
			AssertNoNativeLoad (deniedBefore);
		} finally { Surface.ConfigureAutomaticRecording (previous); }
	}

	static void AssertNativeMetadata (IntPtr native, ImageSurface actual)
	{
		Assert.Equal (NativeMethods.cairo_surface_status (native), actual.Status);
		Assert.Equal (NativeMethods.cairo_image_surface_get_width (native), actual.Width);
		Assert.Equal (NativeMethods.cairo_image_surface_get_height (native), actual.Height);
		Assert.Equal (NativeMethods.cairo_image_surface_get_stride (native), actual.Stride);
		Assert.Equal (NativeMethods.cairo_image_surface_get_format (native), actual.Format);
		Assert.Equal (NativeMethods.cairo_surface_get_content (native), actual.Content);
		Assert.Equal (NativeMethods.cairo_surface_get_type (native), actual.SurfaceType);
		Assert.Equal (NativeMethods.cairo_surface_get_reference_count (native), actual.ReferenceCount);
		Assert.Equal (NativeMethods.cairo_image_surface_get_data (native), actual.DataPtr);
	}

	static void AssertNoNativeLoad (int deniedBefore)
	{
		Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
		if (!CairoNativeGuard.NativeAllowed && CairoNativeGuard.LoadedLibcairoMappings () is string[] mappings) Assert.Empty (mappings);
	}
}
