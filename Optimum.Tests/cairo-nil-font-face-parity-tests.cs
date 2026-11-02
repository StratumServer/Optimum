using System;
using Cairo;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoNilFontFaceParityTests
{
    [Theory]
    [InlineData (Status.Success)]
    [InlineData (Status.InvalidRestore)]
    [InlineData (Status.InvalidString)]
    [InlineData (Status.SurfaceFinished)]
    [InlineData (Status.InvalidSize)]
    public void ContextFontFaceAndAssignmentMatchNative (Status fault)
    {
        RunBoth (() => {
            using var surface = CreateTarget (fault);
            using var context = new Context (surface);
            Fault (surface, context, fault);
            using var face = context.GetContextFontFace ();
            Status expected = fault == Status.Success ? Status.Success : Status.NoMemory;
            Assert.Equal (fault, context.Status);
            Assert.Equal (expected, face.Status);
            Assert.Equal (FontType.Toy, face.FontType);
            if (fault != Status.Success) Assert.Equal (0u, face.ReferenceCount);
            context.SetContextFontFace (face);
            Assert.Equal (fault, context.Status);
            using var target = new ImageSurface (Format.Argb32, 8, 8);
            using var other = new Context (target);
            other.SetContextFontFace (face);
            Assert.Equal (expected, other.Status);
            Assert.Equal (Status.Success, target.Status);
            other.SetContextFontFace (null);
            Assert.Equal (expected, other.Status);
        });
    }

    [Theory]
    [InlineData (Status.InvalidRestore)]
    [InlineData (Status.InvalidString)]
    [InlineData (Status.SurfaceFinished)]
    [InlineData (Status.InvalidSize)]
    public void ErrorFacesCreateNilScaledFonts (Status fault)
    {
        RunBoth (() => {
            using var surface = CreateTarget (fault);
            using var context = new Context (surface);
            Fault (surface, context, fault);
            using var face = context.GetContextFontFace ();
            using var options = new FontOptions ();
            using var font = new ScaledFont (face, new Matrix (), new Matrix (), options);
            Assert.Equal (Status.NoMemory, font.Status);
            Assert.Equal (0, font.FontExtents.Height);
            Assert.Equal (0, font.GlyphExtents (new[] { new Glyph (36, 0, 0) }).XAdvance);
            Assert.Equal (1, font.FontMatrix.Xx);
            using var target = new ImageSurface (Format.Argb32, 8, 8);
            using var other = new Context (target);
            other.SetScaledFont (font);
            Assert.Equal (Status.NoMemory, other.Status);
            Assert.Equal (Status.Success, target.Status);
        });
    }

    [Theory]
    [InlineData (Status.InvalidRestore)]
    [InlineData (Status.InvalidString)]
    [InlineData (Status.SurfaceFinished)]
    [InlineData (Status.InvalidSize)]
    public void NilFaceClonesSurviveTheOriginalContext (Status fault)
    {
        RunBoth (() => {
            var surface = CreateTarget (fault);
            var context = new Context (surface);
            Fault (surface, context, fault);
            using var face = context.GetContextFontFace ();
            context.Dispose (); surface.Dispose ();
            Assert.Equal (Status.NoMemory, face.Status);
            using var target = new ImageSurface (Format.Argb32, 8, 8);
            using var other = new Context (target);
            other.SetContextFontFace (face);
            face.Dispose ();
            other.SetContextFontFace (face);
            using var clone = other.GetContextFontFace ();
            Assert.Equal (Status.NoMemory, clone.Status);
            Assert.Equal (0u, clone.ReferenceCount);
            using var font = other.GetScaledFont ();
            Assert.Equal (Status.NoMemory, font.Status);
            Assert.Equal (0, other.FontExtents.Height);
        });
    }

    static ImageSurface CreateTarget (Status fault) => new ImageSurface (Format.Argb32, fault == Status.InvalidSize ? -1 : 8, 8);
    static void Fault (Surface surface, Context context, Status fault)
    {
        if (fault == Status.InvalidRestore) context.Restore ();
        if (fault == Status.InvalidString) context.ShowText (new byte[] { 0xff });
        if (fault == Status.SurfaceFinished) { surface.Finish (); context.Paint (); }
    }
    static void RunBoth (Action exercise)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        int denied = CairoNativeGuard.DeniedCount;
        try {
            if (CairoNativeGuard.NativeAllowed) { Surface.ConfigureAutomaticRecording (false); exercise (); }
            Surface.ConfigureAutomaticRecording (true); exercise ();
            Assert.Equal (denied, CairoNativeGuard.DeniedCount);
            if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }
}
