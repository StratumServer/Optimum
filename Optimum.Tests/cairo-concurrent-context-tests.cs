using System;
using Cairo;
using Xunit;
using SkiaSharp;
using System.IO;
using System.Runtime.CompilerServices;

[Collection ("Cairo materialization metrics")]
public sealed class CairoConcurrentContextTests
{
    [Theory]
    [InlineData (0)]
    [InlineData (1)]
    [InlineData (2)]
    [InlineData (3)]
    [InlineData (4)]
    [InlineData (5)]
    [InlineData (6)]
    public void ConcurrentContextPixelsAndNativeReplayMatchCairo (int program)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        int denied = CairoNativeGuard.DeniedCount;
        try {
            Surface.ConfigureAutomaticRecording (true);
            using var surface = new ImageSurface (Format.Argb32, 16, 12);
            using (var first = new Context (surface)) using (var second = new Context (surface)) Draw (first, second, program);
            using var snapshot = surface.CaptureRecordedSource ();
            byte[] actual;
            if (CairoRecorderParityTests.RenderOverride != null) actual = CairoRecorderParityTests.RenderOverride (surface, 16, 12);
            else {
                using var bitmap = new SKBitmap (16, 12, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas (bitmap)) snapshot.DrawTo (canvas);
                actual = bitmap.Bytes;
            }
            Assert.Contains (actual, x => x != 0);
            if (CairoNativeGuard.NativeAllowed) {
                Surface.ConfigureAutomaticRecording (false);
                using var expected = new ImageSurface (Format.Argb32, 16, 12);
                using (var first = new Context (expected)) using (var second = new Context (expected)) Draw (first, second, program);
                Assert.Equal (expected.Data, actual);
                using var replayed = new ImageSurface (Format.Argb32, 16, 12);
                snapshot.ReplayNative (replayed.NativeHandleForMetadata);
                Assert.Equal (expected.Data, replayed.Data);
            }
            Assert.Equal (denied, CairoNativeGuard.DeniedCount);
            if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    static void Draw (Context first, Context second, int program)
    {
        first.Antialias = Antialias.None; second.Antialias = Antialias.None;
        first.SetSourceRGB (1, 0, 0); second.SetSourceRGB (0, 0, 1);
        first.Rectangle (1, 1, 5, 8); second.Rectangle (8, 2, 5, 6);
        if (program == 0) { first.Fill (); second.Fill (); }
        if (program == 1) {
            first.Save (); first.Translate (2, 1); first.Fill ();
            second.Save (); second.Translate (-1, 2); second.Fill (); first.Restore (); second.Restore ();
        }
        if (program == 2) {
            first.Clip (); second.Fill ();
            first.Paint (); Assert.True (second.InClip (15, 11));
        }
        if (program == 3) {
            second.Restore (); Assert.Equal (Status.InvalidRestore, second.Status);
            first.Fill (); second.Fill (); Assert.Equal (Status.Success, first.Status);
        }
        if (program == 4) {
            first.NewPath (); second.NewPath ();
            first.PushGroup (); first.Rectangle (1, 1, 5, 8); first.Fill ();
            second.Rectangle (8, 2, 5, 6); second.Fill ();
            first.PopGroupToSource (); first.Paint ();
        }
        if (program == 5) {
            first.NewPath (); first.Translate (3, 2); first.Rectangle (0, 0, 4, 4); first.Clip ();
            first.PushGroup ();
            using (var target = first.GetGroupTarget ()) using (var alias = new Context (target)) {
                alias.Antialias = Antialias.None; alias.SetSourceRGB (1, 0, 0);
                alias.Rectangle (3, 2, 2, 2); alias.Fill ();
            }
            first.PopGroupToSource (); first.Paint ();
            second.Fill ();
        }
        if (program == 6) {
            first.Clip (); second.Paint ();
            first.Operator = Operator.Clear; first.Paint ();
            Assert.Equal (Operator.Over, second.Operator);
        }
    }

    [Fact]
    public void PixelCheckpointsKeepSourcesOwnedByBothContexts ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            using var surface = new ImageSurface (Format.Argb32, 16, 12);
            using var first = new Context (surface); using var second = new Context (surface);
            using (var gradient = new LinearGradient (0, 0, 8, 0)) {
                gradient.AddColorStop (0, new Color (1, 0, 0)); gradient.AddColorStop (1, new Color (0, 1, 0));
                first.SetSource (gradient);
            }
            using (var gradient = new LinearGradient (8, 0, 16, 0)) {
                gradient.AddColorStop (0, new Color (0, 0, 1)); gradient.AddColorStop (1, new Color (0, 1, 1));
                second.SetSource (gradient);
            }
            first.Rectangle (0, 0, 8, 6); first.Fill ();
            _ = surface.DataPtr;
            second.Rectangle (8, 0, 8, 12); second.Fill ();
            first.Rectangle (0, 6, 8, 6); first.Fill ();
            byte[] data = surface.Data;
            Assert.Equal (BitConverter.ToUInt32 (data, 0), BitConverter.ToUInt32 (data, 6 * surface.Stride));
            Assert.NotEqual (0u, BitConverter.ToUInt32 (data, 8 * 4));
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Fact]
    public void PdfPageTransitionsKeepIndependentContextState ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        string file = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-contexts-" + Guid.NewGuid ().ToString ("N") + ".pdf");
        try {
            Surface.ConfigureAutomaticRecording (true);
            using var surface = new PdfSurface (file, 24, 16);
            using var first = new Context (surface); using var second = new Context (surface);
            first.Translate (2, 1); second.Translate (12, 3);
            first.SetSourceRGB (1, 0, 0); second.SetSourceRGB (0, 0, 1);
            first.Rectangle (0, 0, 4, 4); first.Fill (); second.Rectangle (0, 0, 4, 4); second.Fill ();
            first.ShowPage ();
            Assert.Equal (12, second.Matrix.X0); Assert.Equal (3, second.Matrix.Y0);
            Assert.Equal (2, first.Matrix.X0); Assert.Equal (1, first.Matrix.Y0);
            second.Rectangle (0, 0, 4, 4); second.Fill (); first.Rectangle (0, 0, 4, 4); first.Fill ();
            surface.Finish ();
            Assert.True (File.ReadAllBytes (file).Length > 100);
        } finally { Surface.ConfigureAutomaticRecording (previous); if (File.Exists (file)) File.Delete (file); }
    }

    [MethodImpl (MethodImplOptions.NoInlining)]
    static WeakReference AbandonContext (ImageSurface surface)
    {
        var context = new Context (surface);
        context.SetSourceRGB (1, 0, 0); context.Paint ();
        return new WeakReference (context);
    }

    [Fact]
    public void InactiveContextCanBeFinalizedWithoutActivatingTheRecordingThread ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            using var surface = new ImageSurface (Format.Argb32, 2, 2);
            WeakReference abandoned = AbandonContext (surface);
            using var active = new Context (surface);
            GC.Collect (); GC.WaitForPendingFinalizers (); GC.Collect ();
            Assert.False (abandoned.IsAlive);
            Assert.Equal (Status.Success, active.Status);
            Assert.Equal (unchecked ((int)0xffff0000), BitConverter.ToInt32 (surface.Data, 0));
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void ContextsCanDrawAfterCpuTextureReplay (bool freshContext)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            using var surface = new ImageSurface (Format.Argb32, 4, 2);
            using var first = new Context (surface);
            first.SetSourceRGB (1, 0, 0); first.Paint ();
            using var bitmap = new SKBitmap (4, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas (bitmap)) Assert.True (surface.TryDrawRecordedCommands (canvas));
            using var next = freshContext ? new Context (surface) : null;
            Context drawing = next ?? first;
            drawing.SetSourceRGB (0, 0, 1); drawing.Rectangle (0, 0, 1, 2); drawing.Fill ();
            byte[] pixels = surface.Data;
            Assert.Equal (unchecked ((int)0xff0000ff), BitConverter.ToInt32 (pixels, 0));
            Assert.Equal (unchecked ((int)0xffff0000), BitConverter.ToInt32 (pixels, 4));
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Fact]
    public void InterleavedContextsKeepTheirPathsTransformsSourcesAndSaveStacks ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        int denied = CairoNativeGuard.DeniedCount;
        try {
            Surface.ConfigureAutomaticRecording (true);
            using var surface = new ImageSurface (Format.Argb32, 8, 4);
            using var left = new Context (surface);
            left.SetSourceRGB (1, 0, 0); left.Rectangle (0, 0, 3, 4); left.Save ();
            using var right = new Context (surface);
            right.Translate (4, 0); right.SetSourceRGB (0, 0, 1);
            right.Rectangle (0, 0, 3, 4);
            left.Fill (); right.Fill ();
            left.Restore (); Assert.Equal (Status.Success, left.Status);
            Assert.Equal (0, left.Matrix.X0); Assert.Equal (4, right.Matrix.X0);
            left.LineWidth = 7; Assert.Equal (2, right.LineWidth); Assert.Equal (7, left.LineWidth);
            byte[] pixels = surface.Data;
            Assert.Equal (unchecked ((int)0xffff0000), BitConverter.ToInt32 (pixels, 0));
            Assert.Equal (unchecked ((int)0xff0000ff), BitConverter.ToInt32 (pixels, 4 * 4));
            Assert.Equal (0, BitConverter.ToInt32 (pixels, 3 * 4));
            Assert.Equal (denied, CairoNativeGuard.DeniedCount);
            if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }
}
