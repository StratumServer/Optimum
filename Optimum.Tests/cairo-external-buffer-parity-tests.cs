using System;
using System.IO;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;
using Path = System.IO.Path;

[Collection ("Cairo materialization metrics")]
public sealed class CairoExternalBufferParityTests
{
    [Theory]
    [InlineData (-1, -1, -1, -1, Status.InvalidFormat)]
    [InlineData (99, 1, 1, 4, Status.InvalidFormat)]
    [InlineData (0, -1, 1, -4, Status.InvalidSize)]
    [InlineData (0, 32768, 0, 0, Status.InvalidSize)]
    [InlineData (0, 1, -1, 4, Status.InvalidSize)]
    [InlineData (0, 1, 1, 0, Status.InvalidStride)]
    [InlineData (0, 1, 0, 0, Status.InvalidStride)]
    [InlineData (0, 1, 1, 5, Status.InvalidStride)]
    [InlineData (2, 1, 1, 1, Status.InvalidStride)]
    public void InvalidDataSurfacesPreserveNativeStatusPrecedence (int format, int width, int height, int stride, Status expected)
    {
        InManagedMode (() => {
            using var pointer = new ImageSurface (IntPtr.Zero, (Format)format, width, height, stride);
            using var array = new ImageSurface ((byte[])null, (Format)format, width, height, stride);
            foreach (var surface in new[] { pointer, array }) {
                Assert.Equal (expected, surface.Status);
                Assert.Equal (0u, surface.ReferenceCount);
                Assert.Null (surface.Data);
                Assert.Equal (IntPtr.Zero, surface.DataPtr);
            }
            CompareNativeMetadata (IntPtr.Zero, (Format)format, width, height, stride, pointer);
        });
    }

    [Theory]
    [InlineData (Format.Argb32, 3, 2, 16)]
    [InlineData (Format.Argb32, 3, 2, -16)]
    [InlineData (Format.Rgb24, 3, 2, 16)]
    [InlineData (Format.A8, 3, 2, 8)]
    [InlineData (Format.A1, 3, 2, 8)]
    [InlineData (Format.Rgb16565, 3, 2, 8)]
    [InlineData (Format.Argb32, 0, 3, 4)]
    [InlineData (Format.Argb32, 0, 3, -4)]
    [InlineData (Format.Argb32, 3, 0, -12)]
    [InlineData (Format.Argb32, 0, 0, -4)]
    [InlineData (Format.Argb32, 1, 1, 2147483644)]
    [InlineData (Format.Argb32, 1, 1, int.MinValue)]
    public void NullBuffersAllocateLikeCairo (Format format, int width, int height, int stride)
    {
        InManagedMode (() => {
            using var pointer = new ImageSurface (IntPtr.Zero, format, width, height, stride);
            using var array = new ImageSurface ((byte[])null, format, width, height, stride);
            Assert.Equal (Status.Success, pointer.Status);
            Assert.Equal (width > 0 && height > 0 ? Surface.StrideForWidth (format, width) : stride, pointer.Stride);
            CompareNativeMetadata (IntPtr.Zero, format, width, height, stride, pointer);
            Assert.Equal (pointer.Stride, array.Stride);
            Assert.Equal (pointer.Data, array.Data);
            if (width == 0 || height == 0) { Assert.Null (pointer.Data); Assert.Equal (IntPtr.Zero, pointer.DataPtr); }
            else {
                Assert.NotEqual (IntPtr.Zero, pointer.DataPtr);
                using var context = new Context (pointer);
                context.SetSourceRGB (1, 0, 0); context.Paint ();
                Assert.Equal (Status.Success, context.Status);
                Assert.NotNull (pointer.Data);
            }
        });
    }

    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void BottomUpPointersPreserveRowsPaddingAndExternalWrites (bool negative)
    {
        InManagedMode (() => {
            const int width = 3, height = 3, pitch = 16;
            int stride = negative ? -pitch : pitch;
            IntPtr allocation = Marshal.AllocHGlobal (pitch * height);
            string png = Path.Combine (Path.GetTempPath (), "optimum-external-" + Guid.NewGuid ().ToString ("N") + ".png");
            try {
                for (int i = 0; i < pitch * height; i++) Marshal.WriteByte (allocation, i, 0x51);
                IntPtr first = negative ? IntPtr.Add (allocation, pitch * (height - 1)) : allocation;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++) Marshal.WriteInt32 (first, y * stride + x * 4, unchecked ((int)0xff00ff00));
                using var surface = new ImageSurface (first, Format.Argb32, width, height, stride);
                Assert.Equal (stride, surface.Stride); Assert.Equal (first, surface.DataPtr);
                CompareNativeMetadata (first, Format.Argb32, width, height, stride, surface);
                using (var context = new Context (surface)) {
                    context.SetSourceRGB (1, 0, 0); context.Rectangle (0, 0, width, 1); context.Fill ();
                    context.SetSourceRGB (0, 0, 1); context.Rectangle (0, 2, width, 1); context.Fill ();
                }
                Assert.Equal (first, surface.DataPtr);
                Assert.Equal (unchecked ((int)0xffff0000), Marshal.ReadInt32 (first));
                Assert.Equal (unchecked ((int)0xff00ff00), Marshal.ReadInt32 (first, stride));
                Assert.Equal (unchecked ((int)0xff0000ff), Marshal.ReadInt32 (first, stride * 2));
                for (int y = 0; y < height; y++) for (int x = 12; x < pitch; x++) Assert.Equal (0x51, Marshal.ReadByte (first, y * stride + x));
                Marshal.WriteInt32 (first, stride, unchecked ((int)0xffffff00));
                surface.MarkDirty (); surface.WriteToPng (png);
                using var bitmap = SKBitmap.Decode (png);
                Assert.Equal (SKColors.Red, bitmap.GetPixel (0, 0));
                Assert.Equal (SKColors.Yellow, bitmap.GetPixel (0, 1));
                Assert.Equal (SKColors.Blue, bitmap.GetPixel (0, 2));
                if (negative) Assert.Throws<OverflowException> (() => surface.Data);
                else Assert.Equal (unchecked ((int)0xffffff00), BitConverter.ToInt32 (surface.Data, pitch));
                surface.Finish ();
            } finally { Marshal.FreeHGlobal (allocation); if (File.Exists (png)) File.Delete (png); }
        });
    }

    [Theory]
    [InlineData (0, 0, 4)]
    [InlineData (0, 3, 4)]
    [InlineData (3, 0, 12)]
    [InlineData (3, 0, -12)]
    public void EmptyExternalBuffersRetainTheirPointerAndStride (int width, int height, int stride)
    {
        InManagedMode (() => {
            byte[] data = new byte[Math.Abs (stride) * Math.Max (1, height)];
            var pin = GCHandle.Alloc (data, GCHandleType.Pinned);
            try {
                using var pointer = new ImageSurface (pin.AddrOfPinnedObject (), Format.Argb32, width, height, stride);
                using var array = new ImageSurface (data, Format.Argb32, width, height, stride);
                Assert.Equal (stride, pointer.Stride); Assert.Equal (stride, array.Stride);
                Assert.Equal (pin.AddrOfPinnedObject (), pointer.DataPtr);
                CompareNativeMetadata (pin.AddrOfPinnedObject (), Format.Argb32, width, height, stride, pointer);
                Assert.Equal (data.Length == 0 ? Array.Empty<byte> () : new byte[Math.Abs (stride) * height], pointer.Data);
                using (var context = new Context (pointer)) { context.Paint (); Assert.Equal (Status.Success, context.Status); }
                Assert.Equal (Status.Success, pointer.Finish ());
            } finally { pin.Free (); }
        });
    }

    [Fact]
    public void A1WritesPreserveInactiveBitsAndStridePadding ()
    {
        InManagedMode (() => {
            var data = new byte[] { 0xfd, 0xa5, 0x5a, 0x81 };
            using var surface = new ImageSurface (data, Format.A1, 3, 1, 4);
            using (var context = new Context (surface)) { context.Operator = Operator.Clear; context.Paint (); }
            _ = surface.DataPtr;
            Assert.Equal (new byte[] { 0xf8, 0xa5, 0x5a, 0x81 }, data);
            if (CairoNativeGuard.NativeAllowed) {
                var expected = new byte[] { 0xfd, 0xa5, 0x5a, 0x81 };
                var pin = GCHandle.Alloc (expected, GCHandleType.Pinned);
                try {
                    IntPtr native = NativeMethods.cairo_image_surface_create_for_data (pin.AddrOfPinnedObject (), Format.A1, 3, 1, 4);
                    IntPtr context = NativeMethods.cairo_create (native);
                    try { NativeMethods.cairo_set_operator (context, Operator.Clear); NativeMethods.cairo_paint (context); Assert.Equal (expected, data); }
                    finally { NativeMethods.cairo_destroy (context); NativeMethods.cairo_surface_destroy (native); }
                } finally { pin.Free (); }
            }
        });
    }

    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void SuccessiveContextsStartWithDefaultStateAndKeepEarlierPixels (bool snapshotBetweenContexts)
    {
        InManagedMode (() => {
            using var surface = new ImageSurface (Format.Argb32, 4, 2);
            using (var first = new Context (surface)) {
                first.Translate (2, 0);
                first.Rectangle (0, 0, 2, 2); first.Clip ();
                first.SetSourceRGB (1, 0, 0); first.Paint ();
                first.LineWidth = 9;
                first.Save (); first.Scale (2, 3);
            }
            if (snapshotBetweenContexts) {
                using var target = SKSurface.Create (new SKImageInfo (4, 2));
                using var snapshot = surface.CaptureRecordedSource ();
                snapshot.DrawTo (target);
            }
            using (var second = new Context (surface)) {
                Assert.Equal (2, second.LineWidth);
                Assert.Equal (1, second.Matrix.Xx); Assert.Equal (0, second.Matrix.X0);
                Assert.True (second.InClip (.5, .5));
                second.SetSourceRGB (0, 0, 1); second.Rectangle (0, 0, 1, 2); second.Fill ();
            }
            byte[] pixels = surface.Data;
            Assert.Equal (unchecked ((int)0xff0000ff), BitConverter.ToInt32 (pixels, 0));
            Assert.Equal (0, BitConverter.ToInt32 (pixels, 4));
            Assert.Equal (unchecked ((int)0xffff0000), BitConverter.ToInt32 (pixels, 8));
        });
    }

    [Fact]
    public void ShortArraysAndNegativeMultiRowArraysRemainBoundsChecked ()
    {
        InManagedMode (() => {
            Assert.Throws<ArgumentException> (() => new ImageSurface (new byte[7], Format.Argb32, 1, 2, 4));
            Assert.Throws<ArgumentException> (() => new ImageSurface (new byte[8], Format.Argb32, 1, 2, -4));
        });
    }

    static void CompareNativeMetadata (IntPtr data, Format format, int width, int height, int stride, ImageSurface actual)
    {
        if (!CairoNativeGuard.NativeAllowed) return;
        IntPtr native = NativeMethods.cairo_image_surface_create_for_data (data, format, width, height, stride);
        try {
            Assert.Equal (NativeMethods.cairo_surface_status (native), actual.Status);
            Assert.Equal (NativeMethods.cairo_image_surface_get_width (native), actual.Width);
            Assert.Equal (NativeMethods.cairo_image_surface_get_height (native), actual.Height);
            Assert.Equal (NativeMethods.cairo_image_surface_get_stride (native), actual.Stride);
            Assert.Equal (NativeMethods.cairo_image_surface_get_format (native), actual.Format);
            Assert.Equal (NativeMethods.cairo_image_surface_get_data (native) != IntPtr.Zero, actual.DataPtr != IntPtr.Zero);
        } finally { NativeMethods.cairo_surface_destroy (native); }
    }

    static void InManagedMode (Action test)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        int denied = CairoNativeGuard.DeniedCount;
        try {
            Surface.ConfigureAutomaticRecording (true); test ();
            Assert.Equal (denied, CairoNativeGuard.DeniedCount);
            if (!CairoNativeGuard.NativeAllowed && CairoNativeGuard.LoadedLibcairoMappings () is string[] mappings) Assert.Empty (mappings);
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }
}
