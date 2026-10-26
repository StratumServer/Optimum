using System;
using System.Collections.Generic;
using Cairo;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoFinishedTextParityTests
{
    public static IEnumerable<object[]> Cases ()
    {
        for (int operation = 0; operation < 21; operation++) {
            yield return new object[] { operation, false };
            yield return new object[] { operation, true };
        }
    }

    [Theory]
    [MemberData (nameof (Cases))]
    public void FinishedTextAndGlyphContractsMatchNative (int operation, bool warmFont)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        int denied = CairoNativeGuard.DeniedCount;
        try {
            (Status Surface, Status Context, PointD Point)? native = null;
            if (CairoNativeGuard.NativeAllowed) {
                Surface.ConfigureAutomaticRecording (false);
                native = Exercise (operation, warmFont);
            }
            Surface.ConfigureAutomaticRecording (true);
            var actual = Exercise (operation, warmFont);
            if (native.HasValue) {
                Assert.Equal (native.Value.Surface, actual.Surface);
                Assert.Equal (native.Value.Context, actual.Context);
                Assert.InRange (Math.Abs (native.Value.Point.X - actual.Point.X), 0, 1.0 / 256);
                Assert.InRange (Math.Abs (native.Value.Point.Y - actual.Point.Y), 0, 1.0 / 256);
            }
            Assert.Equal (denied, CairoNativeGuard.DeniedCount);
            if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Theory]
    [InlineData (false, 0)]
    [InlineData (false, 1)]
    [InlineData (false, 2)]
    [InlineData (true, 0)]
    [InlineData (true, 1)]
    [InlineData (true, 2)]
    public void InvalidUtf8KeepsNativeErrorAndNilFontContracts (bool finish, int operation)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        int denied = CairoNativeGuard.DeniedCount;
        try {
            foreach (bool managed in new[] { false, true }) {
                if (!managed && !CairoNativeGuard.NativeAllowed) continue;
                Surface.ConfigureAutomaticRecording (managed);
                using var surface = new ImageSurface (Format.Argb32, 8, 8);
                using var context = new Context (surface);
                if (finish) surface.Finish ();
                var invalid = new byte[] { 0xff };
                if (operation == 0) context.ShowText (invalid);
                else if (operation == 1) context.TextPath (invalid);
                else Assert.Equal (0, context.TextExtents (invalid).XAdvance);
                Assert.Equal (operation == 1 ? Status.Success : Status.InvalidString, context.Status);
                Assert.Equal (finish && operation == 0 ? Status.SurfaceFinished : Status.Success, surface.Status);
                if (operation != 1) {
                    using var font = context.GetScaledFont ();
                    using var target = new ImageSurface (Format.Argb32, 8, 8);
                    using var other = new Context (target);
                    other.SetScaledFont (font);
                    Assert.Equal (Status.InvalidString, other.Status);
                    Assert.Equal (Status.Success, target.Status);
                    Assert.Equal (0, other.FontExtents.Height);
                    Assert.Equal (0, other.TextExtents ("A").XAdvance);
                }
            }
            Assert.Equal (denied, CairoNativeGuard.DeniedCount);
            if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Fact]
    public void FinishedTargetErrorPropagatesThroughItsScaledFont ()
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        try {
            foreach (bool managed in new[] { false, true }) {
                if (!managed && !CairoNativeGuard.NativeAllowed) continue;
                Surface.ConfigureAutomaticRecording (managed);
                using var surface = new ImageSurface (Format.Argb32, 8, 8);
                surface.Finish ();
                using var context = new Context (surface);
                using var font = context.GetScaledFont ();
                Assert.Equal (Status.SurfaceFinished, font.Status);
                Assert.Equal (0, font.FontExtents.Height);
                Assert.Equal (1, font.FontMatrix.Xx);
                using var otherSurface = new ImageSurface (Format.Argb32, 8, 8);
                using var other = new Context (otherSurface);
                other.SetScaledFont (font);
                Assert.Equal (Status.SurfaceFinished, other.Status);
                Assert.Equal (Status.Success, otherSurface.Status);
            }
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    static (Status Surface, Status Context, PointD Point) Exercise (int operation, bool warmFont)
    {
        using var surface = new ImageSurface (Format.Argb32, 32, 32);
        using var context = new Context (surface);
        context.SelectFontFace ("DejaVu Sans", FontSlant.Normal, FontWeight.Normal);
        context.SetFontSize (12);
        context.MoveTo (2, 14);
        if (warmFont) Assert.True (context.FontExtents.Height > 0);
        Assert.Equal (Status.Success, surface.Finish ());
        var glyphs = new[] { new Glyph (36, 2, 14) };
        var invalid = new byte[] { 0xff };
        switch (operation) {
            case 0: context.ShowText ("A"); break;
            case 1: context.ShowText (""); break;
            case 2: context.ShowText (new byte[] { 65 }); break;
            case 3: context.ShowText (invalid); break;
            case 4: context.TextPath ("A"); break;
            case 5: context.TextPath (invalid); break;
            case 6: Assert.True (context.TextExtents ("A").XAdvance > 0); break;
            case 7: Assert.Equal (0, context.TextExtents (invalid).XAdvance); break;
            case 8: Assert.True (context.FontExtents.Height > 0); break;
            case 9: context.ShowGlyphs (glyphs); break;
            case 10: context.ShowGlyphs (Array.Empty<Glyph> ()); break;
            case 11: context.GlyphPath (glyphs); break;
            case 12: Assert.True (context.GlyphExtents (glyphs).XAdvance > 0); break;
            case 13: using (var font = context.GetScaledFont ()) Assert.Equal (Status.Success, font.Status); break;
            case 14: context.Operator = Operator.Dest; context.ShowText ("A"); break;
            case 15: context.Clip (); context.ShowText ("A"); break;
            case 16: context.Operator = Operator.Dest; context.ShowGlyphs (glyphs); break;
            case 17: context.Clip (); context.ShowGlyphs (glyphs); break;
            case 18: context.SetFontSize (0); context.ShowText ("A"); break;
            case 19: context.ShowText (new byte[] { 65, 0, 0xff }); break;
            case 20: context.ShowText (new byte[] { 0xc0, 0xaf }); break;
        }
        bool invalidText = operation is 3 or 7 or 20;
        bool surfaceError = operation is 0 or 1 or 2 or 3 or 9 or 14 or 15 or 18 or 19 or 20;
        Assert.Equal (invalidText ? Status.InvalidString : Status.Success, context.Status);
        Assert.Equal (surfaceError ? Status.SurfaceFinished : Status.Success, surface.Status);
        var result = (surface.Status, context.Status, context.CurrentPoint);
        if (invalidText) {
            context.LineWidth = 9;
            Assert.Equal (Status.InvalidString, context.Status);
            Assert.Equal (0, context.FontExtents.Height);
            Assert.Equal (0, context.TextExtents ("A").XAdvance);
            Assert.Equal (0, context.GlyphExtents (glyphs).XAdvance);
            using var font = context.GetScaledFont ();
            Assert.Equal (Status.InvalidString, font.Status);
            Assert.Equal (0, font.FontExtents.Height);
            Assert.Equal (0, font.GlyphExtents (glyphs).XAdvance);
            Assert.Equal (1, font.FontMatrix.Xx);
            Assert.Equal (FontType.Toy, font.FontType);
        } else {
            context.ResetClip ();
            context.Operator = Operator.Over;
            context.Paint ();
            Assert.Equal (Status.SurfaceFinished, context.Status);
        }
        return result;
    }
}
