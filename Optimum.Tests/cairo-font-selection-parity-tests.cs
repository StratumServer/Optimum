using System;
using System.Collections.Generic;
using Cairo;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoFontSelectionParityTests
{
    public static IEnumerable<object[]> Cases ()
    {
        string[] families = { null, "", " ", "DejaVu Sans", "DejaVu Sans\0ignored" };
        foreach (string family in families) {
            yield return new object[] { family, 0, 0, family == null ? Status.NullPointer : Status.Success };
            yield return new object[] { family, 2, 1, family == null ? Status.NullPointer : Status.Success };
            yield return new object[] { family, -1, 0, family == null ? Status.NullPointer : Status.InvalidSlant };
            yield return new object[] { family, 3, 0, family == null ? Status.NullPointer : Status.InvalidSlant };
            yield return new object[] { family, 0, -1, family == null ? Status.NullPointer : Status.InvalidWeight };
            yield return new object[] { family, 0, 2, family == null ? Status.NullPointer : Status.InvalidWeight };
            yield return new object[] { family, 9, 9, family == null ? Status.NullPointer : Status.InvalidSlant };
        }
    }

    [Theory]
    [MemberData (nameof (Cases))]
    public void SelectionMatchesNative (string family, int slant, int weight, Status expected)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        int denied = CairoNativeGuard.DeniedCount;
        try {
            (double Advance, double Height)? native = null;
            if (CairoNativeGuard.NativeAllowed) {
                Surface.ConfigureAutomaticRecording (false);
                native = Select (family, slant, weight, expected);
            }
            Surface.ConfigureAutomaticRecording (true);
            var actual = Select (family, slant, weight, expected);
            if (native.HasValue) {
                Assert.InRange (Math.Abs (native.Value.Advance - actual.Advance), 0, 1.0 / 256);
                Assert.InRange (Math.Abs (native.Value.Height - actual.Height), 0, 1.0 / 256);
            }
            Assert.Equal (denied, CairoNativeGuard.DeniedCount);
            if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    static (double Advance, double Height) Select (string family, int slant, int weight, Status expected)
    {
        using var surface = new ImageSurface (Format.Argb32, 64, 24);
        using var context = new Context (surface);
        context.SetFontSize (12);
        context.SelectFontFace (family, (FontSlant)slant, (FontWeight)weight);
        Assert.Equal (expected, context.Status);
        double advance = context.TextExtents ("Ab").XAdvance;
        double height = context.FontExtents.Height;
        Assert.Equal (Status.Success, surface.Status);
        if (expected == Status.Success) {
            Assert.True (advance > 0); Assert.True (height > 0);
            context.MoveTo (1, 16); context.ShowText ("Ab");
            Assert.Equal (Status.Success, context.Status);
        } else {
            Assert.Equal (0, advance); Assert.Equal (0, height);
            context.SelectFontFace ("DejaVu Sans", FontSlant.Normal, FontWeight.Normal);
            Assert.Equal (expected, context.Status);
            using var face = context.GetContextFontFace ();
            Assert.Equal (Status.NoMemory, face.Status);
            using var font = context.GetScaledFont ();
            Assert.Equal (expected, font.Status);
        }
        return (advance, height);
    }

    [Theory]
    [InlineData ("")]
    [InlineData (" ")]
    [InlineData ("DejaVu Sans\0ignored")]
    [InlineData ("\0ignored")]
    public void DefaultFamiliesRenderWithoutFallback (string family)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        int denied = CairoNativeGuard.DeniedCount;
        try {
            void Draw (Context context) {
                context.SetSourceRGB (.2, .3, .6); context.Paint ();
                context.SelectFontFace (family, FontSlant.Normal, FontWeight.Normal);
                context.SetFontSize (16);
                using var options = new FontOptions { Antialias = Antialias.Gray, HintStyle = HintStyle.Full, HintMetrics = HintMetrics.On };
                context.FontOptions = options;
                context.SetSourceRGB (1, 1, 1); context.MoveTo (3, 22); context.ShowText ("Abc 123");
                context.MoveTo (3, 44); context.TextPath ("Ab"); context.Fill ();
                Assert.Equal (Status.Success, context.Status);
            }
            if (CairoNativeGuard.NativeAllowed) {
                Surface.ConfigureAutomaticRecording (false);
                CairoRecorderParityTests.AssertLocalAaParity (128, 52, Draw);
            } else {
                Surface.ConfigureAutomaticRecording (true);
                using var surface = new ImageSurface (Format.Argb32, 128, 52);
                using (var context = new Context (surface)) Draw (context);
                byte[] pixels = CairoRecorderParityTests.RenderOverride != null
                    ? CairoRecorderParityTests.RenderOverride (surface, 128, 52) : surface.Data;
                bool hasText = false;
                for (int i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] > 200 && pixels[i + 1] > 200 && pixels[i + 2] > 200) { hasText = true; break; }
                Assert.True (hasText, "The default-family text did not produce white glyph pixels.");
                Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
            }
            Assert.Equal (denied, CairoNativeGuard.DeniedCount);
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }

    [Theory]
    [MemberData (nameof (Cases))]
    public void ToyFaceMetadataMatchesNativeProbe (string family, int slant, int weight, Status expected)
    {
        bool previous = Surface.AutomaticRecordingEnabled;
        int denied = CairoNativeGuard.DeniedCount;
        try {
            Surface.ConfigureAutomaticRecording (true);
            using var face = new FontFace (family, (FontSlant)slant, (FontWeight)weight);
            Assert.Equal (expected, face.Status);
            Assert.Equal (FontType.Toy, face.FontType);
            string expectedFamily = expected != Status.Success ? "" : family.Split ('\0')[0];
            Assert.Equal (expectedFamily, face.Family);
            Assert.Equal (expected == Status.Success ? (FontSlant)slant : FontSlant.Normal, face.Slant);
            Assert.Equal (expected == Status.Success ? (FontWeight)weight : FontWeight.Normal, face.Weight);
            if (expected != Status.Success) Assert.Equal (0u, face.ReferenceCount);
            using var surface = new ImageSurface (Format.Argb32, 64, 24);
            using var context = new Context (surface);
            context.SetContextFontFace (face);
            Assert.Equal (expected, context.Status);
            if (expected == Status.Success) Assert.True (context.TextExtents ("Ab").XAdvance > 0);
            Assert.Equal (denied, CairoNativeGuard.DeniedCount);
            if (!CairoNativeGuard.NativeAllowed) Assert.Empty (CairoNativeGuard.LoadedLibcairoMappings ());
        } finally { Surface.ConfigureAutomaticRecording (previous); }
    }
}
