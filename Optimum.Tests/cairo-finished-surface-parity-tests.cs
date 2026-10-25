using System;
using System.Collections.Generic;
using Cairo;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoFinishedSurfaceParityTests
{
    [Theory]
    [InlineData (0)]
    [InlineData (1)]
    [InlineData (2)]
    [InlineData (3)]
    [InlineData (4)]
    [InlineData (5)]
    [InlineData (6)]
    [InlineData (7)]
    [InlineData (8)]
    [InlineData (9)]
    public void FinishedSurfaceAndContextErrorsMatchNative (int operation)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        int denied = CairoNativeGuard.DeniedCount;
        try {
            string[] native = null;
            if (CairoNativeGuard.NativeAllowed) {
                Surface.ConfigureAutomaticRecording (false);
                native = Exercise (operation);
            }
            Surface.ConfigureAutomaticRecording (true);
            string[] actual = Exercise (operation);
            if (native != null) Assert.Equal (native, actual);
            Assert.Equal (denied, CairoNativeGuard.DeniedCount);
            if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void FinishAllowsSavedStateAndRetainsSourceResources (bool native)
    {
        if (native && !CairoNativeGuard.NativeAllowed) return;
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (!native);
            using var surface = new ImageSurface (Format.Argb32, 8, 8);
            using var context = new Context (surface);
            using var gradient = new LinearGradient (0, 0, 8, 0);
            gradient.AddColorStop (0, new Color (1, 0, 0));
            gradient.AddColorStop (1, new Color (0, 0, 1));
            context.SetSource (gradient);
            context.LineWidth = 3;
            context.Save ();
            context.LineWidth = 9;
            context.Rectangle (0, 0, 4, 4); context.Clip ();
            context.Paint ();
            Assert.Equal (Status.Success, surface.Finish ());
            Assert.Equal (Status.Success, context.Status);
            Assert.Equal (9, context.LineWidth);
            using (var source = context.GetSource ()) Assert.Equal (Status.Success, source.Status);
            context.Restore ();
            Assert.Equal (3, context.LineWidth);
            context.Paint ();
            Assert.Equal (Status.SurfaceFinished, context.Status);
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Fact]
    public void FinishBeforeTheFirstContextKeepsTheTargetAvailable ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            using var surface = new ImageSurface (Format.Argb32, 4, 4);
            Assert.Equal (Status.Success, surface.Finish ());
            using var context = new Context (surface);
            Assert.Equal (Status.SurfaceFinished, context.Status);
            using var target = context.GetTarget ();
            Assert.Equal (Status.NoMemory, target.Status);
            Assert.Equal (Status.NoMemory, target.Finish ());
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Fact]
    public void FailedDrawingDoesNotChangeFinishedPixels ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            Surface.ConfigureAutomaticRecording (true);
            var pixels = new byte[64];
            using var surface = new ImageSurface (pixels, Format.Argb32, 4, 4, 16);
            using var context = new Context (surface);
            context.SetSourceRGB (1, 0, 0); context.Paint ();
            surface.Finish ();
            byte[] finished = (byte[])pixels.Clone ();
            Assert.Equal (unchecked ((int)0xffff0000), BitConverter.ToInt32 (finished, 0));
            context.SetSourceRGB (0, 0, 1); context.Paint ();
            Assert.Equal (Status.SurfaceFinished, context.Status);
            Assert.Equal (finished, pixels);
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Theory]
    [InlineData ("pdf")]
    [InlineData ("ps")]
    [InlineData ("svg")]
    public void FinishedDocumentsKeepTheirOutputAndContextErrors (string kind)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        string path = System.IO.Path.Combine (System.IO.Path.GetTempPath (), Guid.NewGuid () + "." + kind);
        try {
            Surface.ConfigureAutomaticRecording (true);
            using Surface surface = kind switch {
                "pdf" => new PdfSurface (path, 8, 8),
                "ps" => new PSSurface (path, 8, 8),
                _ => new SvgSurface (path, 8, 8)
            };
            using var context = new Context (surface);
            context.SetSourceRGB (1, 0, 0); context.Paint ();
            context.Save ();
            Assert.Equal (Status.Success, surface.Finish ());
            byte[] output = System.IO.File.ReadAllBytes (path);
            Assert.NotEmpty (output);
            context.Restore ();
            context.Paint ();
            Assert.Equal (Status.SurfaceFinished, context.Status);
            Assert.Equal (Status.SurfaceFinished, surface.Finish ());
            using var created = new Context (surface);
            Assert.Equal (Status.SurfaceFinished, created.Status);
            Assert.Equal (output, System.IO.File.ReadAllBytes (path));
        } finally { Surface.ConfigureAutomaticRecording (previous); if (System.IO.File.Exists (path)) System.IO.File.Delete (path); }
    }

    static string[] Exercise (int operation)
    {
        using var surface = new ImageSurface (Format.Argb32, 8, 8);
        using var first = new Context (surface);
        using var second = new Context (surface);
        first.SetSourceRGB (1, 0, 0); first.Paint ();
        using var alias = first.GetTarget ();
        Assert.Equal (Status.Success, alias.Finish ());
        Assert.Equal (Status.Success, surface.Finish ());
        Assert.Equal (Status.Success, first.Status);
        using (var created = new Context (surface)) {
            Assert.Equal (Status.SurfaceFinished, created.Status);
            using var failedTarget = created.GetTarget ();
            using var failedGroupTarget = created.GetGroupTarget ();
            Assert.Equal (Status.NoMemory, failedTarget.Status);
            Assert.Equal (Status.NoMemory, failedGroupTarget.Status);
        }
        first.LineWidth = 7;
        Assert.Equal (7, first.LineWidth);
        var result = new List<string> ();
        switch (operation) {
            case 0: first.Paint (); break;
            case 1: first.PaintWithAlpha (0); break;
            case 2: first.Rectangle (0, 0, 3, 3); first.Fill (); break;
            case 3: first.Fill (); break;
            case 4: first.Stroke (); break;
            case 5: first.Operator = Operator.Dest; first.Paint (); break;
            case 6: first.Clip (); first.Paint (); break;
            case 7: first.ShowPage (); break;
            case 8: first.CopyPage (); break;
            case 9: surface.MarkDirty (); break;
        }
        bool ignored = operation is 1 or 3 or 5 or 6;
        Assert.Equal (ignored ? Status.Success : Status.SurfaceFinished, surface.Status);
        Assert.Equal (ignored || operation == 9 ? Status.Success : Status.SurfaceFinished, first.Status);
        Assert.Equal (Status.Success, second.Status);
        result.Add ($"{surface.Status}:{first.Status}:{second.Status}:{first.LineWidth}");
        second.LineWidth = 9;
        result.Add ($"{second.Status}:{second.LineWidth}");
        second.Paint ();
        result.Add ($"{surface.Status}:{second.Status}:{surface.Finish ()}");
        surface.Flush ();
        first.LineWidth = 3;
        result.Add ($"{surface.Status}:{first.Status}:{first.LineWidth}");
        return result.ToArray ();
    }
}
