using System;
using Cairo;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoFinishedDrawingParityTests
{
    [Theory]
    [InlineData (0, false)]
    [InlineData (1, true)]
    [InlineData (2, true)]
    [InlineData (3, false)]
    [InlineData (4, true)]
    [InlineData (5, false)]
    [InlineData (6, false)]
    [InlineData (7, true)]
    [InlineData (8, true)]
    [InlineData (9, false)]
    [InlineData (10, true)]
    [InlineData (11, false)]
    [InlineData (12, true)]
    [InlineData (13, false)]
    [InlineData (14, true)]
    [InlineData (15, true)]
    [InlineData (16, false)]
    [InlineData (17, true)]
    [InlineData (18, true)]
    public void FinishedDrawingNoOpsMatchNative (int operation, bool fails)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        int denied = CairoNativeGuard.DeniedCount;
        try {
            if (CairoNativeGuard.NativeAllowed) {
                Surface.ConfigureAutomaticRecording (false);
                Exercise (operation, fails);
            }
            Surface.ConfigureAutomaticRecording (true);
            Exercise (operation, fails);
            Assert.Equal (denied, CairoNativeGuard.DeniedCount);
            if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    static void Exercise (int operation, bool fails)
    {
        using var surface = new ImageSurface (Format.Argb32, 8, 8);
        using var context = new Context (surface);
        using var mask = new ImageSurface (Format.Argb32, operation == 11 ? 0 : 8, operation == 11 ? 0 : 8);
        if (operation == 12) {
            using var drawing = new Context (mask);
            drawing.SetSourceRGBA (1, 1, 1, 1); drawing.Paint ();
        }
        using var pattern = new SolidPattern (new Color (1, 1, 1, operation == 9 ? 0 : 1));
        Assert.Equal (Status.Success, surface.Finish ());
        switch (operation) {
            case 0: context.MoveTo (1, 1); context.Fill (); break;
            case 1: context.MoveTo (1, 1); context.LineTo (4, 4); context.Fill (); break;
            case 2: context.Rectangle (1, 1, 0, 4); context.Fill (); break;
            case 3:
                context.Rectangle (0, 0, 2, 2); context.Clip ();
                context.Rectangle (5, 5, 2, 2); context.Clip (); context.Paint (); break;
            case 4: context.Rectangle (20, 20, 2, 2); context.Clip (); context.Paint (); break;
            case 5: context.LineWidth = 0; context.Rectangle (0, 0, 4, 4); context.Stroke (); break;
            case 6: context.Rectangle (0, 0, 0, 4); context.Clip (); context.Paint (); break;
            case 7: context.Rectangle (0, 0, 2, 2); context.Clip (); context.Paint (); break;
            case 8: context.Rectangle (20, 20, 2, 2); context.Fill (); break;
            case 9: case 10: context.Mask (pattern); break;
            case 11: case 12: context.MaskSurface (mask, 0, 0); break;
            case 13: context.Operator = Operator.Dest; context.Mask (pattern); break;
            case 14: context.MoveTo (1, 1); context.LineTo (4, 4); context.Clip (); context.Paint (); break;
            case 15:
                context.MoveTo (0, 0); context.LineTo (4, 0); context.LineTo (0, 4); context.ClosePath (); context.Clip ();
                context.MoveTo (4, 4); context.LineTo (4, 1); context.LineTo (1, 4); context.ClosePath (); context.Clip ();
                context.Paint (); break;
            case 16:
                context.Rectangle (0, 0, 2, 2); context.Clip ();
                context.Rectangle (2, 0, 2, 2); context.Clip (); context.Paint (); break;
            case 17: context.Save (); context.Clip (); context.Restore (); context.Paint (); break;
            case 18: context.Clip (); context.ResetClip (); context.Paint (); break;
        }
        Status expected = fails ? Status.SurfaceFinished : Status.Success;
        Assert.Equal (expected, context.Status);
        Assert.Equal (expected, surface.Status);
        if (!fails) {
            context.LineWidth = 7;
            Assert.Equal (7, context.LineWidth);
            context.ResetClip ();
            context.Operator = Operator.Over;
            context.Paint ();
            Assert.Equal (Status.SurfaceFinished, context.Status);
        }
    }
}
