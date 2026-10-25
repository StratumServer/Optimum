using System;
using Cairo;
using Xunit;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text;

[Collection ("Cairo materialization metrics")]
public sealed class CairoSurfaceLifetimeParityTests
{
    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void ContextsKeepPixelsAliveAfterTheirSurfaceWrapperIsDisposed (bool twoContexts)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        int denied = CairoNativeGuard.DeniedCount;
        try {
            Surface.ConfigureAutomaticRecording (true);
            byte[] actual = DrawAfterOwnerDisposal (twoContexts);
            if (CairoNativeGuard.NativeAllowed) {
                Surface.ConfigureAutomaticRecording (false);
                Assert.Equal (DrawAfterOwnerDisposal (twoContexts), actual);
            }
            Assert.Equal (denied, CairoNativeGuard.DeniedCount);
            if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    static byte[] DrawAfterOwnerDisposal (bool twoContexts)
    {
        var owner = new ImageSurface (Format.Argb32, 8, 4);
        IntPtr pixels = owner.DataPtr;
        var first = new Context (owner);
        Context second = twoContexts ? new Context (owner) : null;
        try {
            owner.Dispose ();
            Assert.Throws<ObjectDisposedException> (() => owner.Width);
            first.SetSourceRGB (1, 0, 0); first.Rectangle (0, 0, 3, 4); first.Fill ();
            if (second != null) { second.SetSourceRGB (0, 0, 1); second.Rectangle (4, 0, 3, 4); second.Fill (); }
            using var target = Assert.IsType<ImageSurface> (first.GetTarget ());
            Assert.Equal (8, target.Width); Assert.Equal (4, target.Height);
            byte[] result = Surface.AutomaticRecordingEnabled && CairoRecorderParityTests.RenderOverride != null
                ? CairoRecorderParityTests.RenderOverride (target, 8, 4) : target.Data;
            Assert.Equal (pixels, target.DataPtr);
            Assert.Equal (unchecked ((int)0xffff0000), BitConverter.ToInt32 (result, 0));
            Assert.Equal (twoContexts ? unchecked ((int)0xff0000ff) : 0, BitConverter.ToInt32 (result, 16));
            return result;
        } finally { second?.Dispose (); first.Dispose (); owner.Dispose (); }
    }

    [Fact]
    public void PixelPinIsReleasedAfterTheLastContextAndAlias ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            var owner = new ImageSurface (Format.Argb32, 2, 2); _ = owner.DataPtr;
            var first = new Context (owner); var second = new Context (owner);
            var alias = first.GetTarget ();
            owner.Dispose (); Assert.False (owner.ManagedPixelPinReleased);
            first.Dispose (); Assert.False (owner.ManagedPixelPinReleased);
            second.Dispose (); Assert.False (owner.ManagedPixelPinReleased);
            alias.Dispose (); Assert.True (owner.ManagedPixelPinReleased);
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Fact]
    public void GroupStackRetainsTheDisposedParent ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            var owner = new ImageSurface (Format.Argb32, 4, 4);
            using var context = new Context (owner);
            context.PushGroup (); owner.Dispose ();
            context.SetSourceRGB (1, 0, 0); context.Paint ();
            context.PopGroupToSource (); context.Paint ();
            using var target = Assert.IsType<ImageSurface> (context.GetTarget ());
            Assert.Equal (unchecked ((int)0xffff0000), BitConverter.ToInt32 (target.Data, 0));
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Fact]
    public void SourcePatternsObserveLaterDrawingAfterOwnerDisposal ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            var source = new ImageSurface (Format.Argb32, 2, 2);
            using var drawing = new Context (source);
            drawing.SetSourceRGB (1, 0, 0); drawing.Paint ();
            using var destination = new ImageSurface (Format.Argb32, 2, 2);
            using var context = new Context (destination);
            using (var pattern = new SurfacePattern (source)) context.SetSource (pattern);
            source.Dispose ();
            drawing.SetSourceRGB (0, 0, 1); drawing.Paint (); context.Paint ();
            Assert.Equal (unchecked ((int)0xff0000ff), BitConverter.ToInt32 (destination.Data, 0));
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Fact]
    public void ErrorContextKeepsItsStatusAfterSurfaceDisposal ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            var source = new ImageSurface (Format.Argb32, -1, 2);
            using var context = new Context (source); source.Dispose ();
            Assert.Equal (Status.InvalidSize, context.Status); context.Paint ();
            using var target = context.GetTarget ();
            Assert.Equal (Status.InvalidSize, target.Status);
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Fact]
    public void ExplicitNativeHandlesRetainIndependentAliasReferences ()
    {
        if (!CairoNativeGuard.NativeAllowed) return;
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            var source = new ImageSurface (Format.Argb32, 2, 2);
            _ = source.NativeHandleForMetadata;
            using var context = new Context (source); source.Dispose ();
            using (var target = Assert.IsType<ImageSurface> (context.GetTarget ())) {
                IntPtr handle = target.NativeHandleForMetadata;
                Assert.True (NativeMethods.cairo_surface_get_reference_count (handle) >= 2);
            }
            context.SetSourceRGB (0, 1, 0); context.Paint ();
            using var final = Assert.IsType<ImageSurface> (context.GetTarget ());
            Assert.Equal (unchecked ((int)0xff00ff00), BitConverter.ToInt32 (final.Data, 0));
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [MethodImpl (MethodImplOptions.NoInlining)]
    static WeakReference MakeUnownedContext (ImageSurface owner)
    {
        var context = new Context (owner); context.SetSourceRGB (1, 0, 0); context.Paint ();
        return new WeakReference (context);
    }

    [Fact]
    public void LastContextFinalizerCanReleaseTheRetainedPixels ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            var owner = new ImageSurface (Format.Argb32, 2, 2); _ = owner.DataPtr;
            WeakReference context = MakeUnownedContext (owner); owner.Dispose ();
            GC.Collect (); GC.WaitForPendingFinalizers (); GC.Collect ();
            Assert.False (context.IsAlive); Assert.True (owner.ManagedPixelPinReleased);
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Theory]
    [InlineData (0)]
    [InlineData (1)]
    [InlineData (2)]
    public void DocumentContextsOutliveTheirDisposedWrappers (int format)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        string file = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-disposed-document-" + Guid.NewGuid ().ToString ("N"));
        try {
            Surface.ConfigureAutomaticRecording (true);
            Surface owner = format == 0 ? new PdfSurface (file, 16, 12) : format == 1 ? new PSSurface (file, 16, 12) : new SvgSurface (file, 16, 12);
            var first = new Context (owner); var second = new Context (owner);
            try {
                owner.Dispose ();
                first.SetSourceRGB (1, 0, 0); first.Paint ();
                second.SetSourceRGB (0, 0, 1); second.Rectangle (1, 1, 4, 4); second.Fill ();
                if (format != 2) { first.ShowPage (); second.SetSourceRGB (0, 1, 0); second.Paint (); }
                Assert.Equal (Status.Success, first.Status); Assert.Equal (Status.Success, second.Status);
                Surface alias = first.GetTarget ();
                first.Dispose (); second.Dispose ();
                using (var last = new Context (alias)) { last.SetSourceRGB (1, 0, 1); last.Rectangle (7, 1, 2, 2); last.Fill (); }
                alias.Dispose ();
            } finally { second.Dispose (); first.Dispose (); owner.Dispose (); }
            byte[] bytes = File.ReadAllBytes (file); Assert.True (bytes.Length > 100);
            string text = Encoding.UTF8.GetString (bytes);
            Assert.Contains (format == 0 ? "%PDF" : format == 1 ? "%!PS" : "<svg", text);
        } finally { Surface.ConfigureAutomaticRecording (previous); if (File.Exists (file)) File.Delete (file); }
    }

    [Fact]
    public void DisposedWrappersCannotMutateTheLiveBackend ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            var owner = new ImageSurface (Format.Argb32, 2, 2);
            using var context = new Context (owner); owner.Dispose ();
            Assert.Throws<ObjectDisposedException> (() => owner.Flush ());
            Assert.Throws<ObjectDisposedException> (() => owner.Finish ());
            Assert.Throws<ObjectDisposedException> (() => owner.DeviceOffset);
            Assert.Throws<ObjectDisposedException> (() => owner.DeviceOffset = new PointD (1, 1));
            Assert.Throws<ObjectDisposedException> (() => owner.SetFallbackResolution (72, 72));
            Assert.Throws<ObjectDisposedException> (() => owner.CreateSimilar (Content.ColorAlpha, 2, 2));
            context.SetSourceRGB (1, 0, 0); context.Paint ();
            using var target = Assert.IsType<ImageSurface> (context.GetTarget ());
            Assert.Equal (0, target.DeviceOffset.X);
            Assert.Equal (unchecked ((int)0xffff0000), BitConverter.ToInt32 (target.Data, 0));
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Fact]
    public void DisposingAnAliasDoesNotInvalidateItsContext ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            using var surface = new ImageSurface (Format.Argb32, 3, 2);
            using var first = new Context (surface);
            Surface alias = first.GetTarget ();
            using var second = new Context (alias);
            alias.Dispose ();
            second.SetSourceRGB (0, 1, 0); second.Paint ();
            Assert.Equal (unchecked ((int)0xff00ff00), BitConverter.ToInt32 (surface.Data, 0));
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }
}
