using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoGradientTests
{
    static CairoGradientTests() { _ = CairoAPI.Version; }

    public static IEnumerable<object[]> Cases()
    {
        var geometries = new (string Name, bool Radial, double[] Coordinates)[] {
            ("linear", false, new double[] { 8, 4, 35, 25 }),
            ("reverse", false, new double[] { 35, 25, 8, 4 }),
            ("horizontal-boundary", false, new double[] { 8.5, 4, 24.5, 4 }),
            ("vertical-boundary", false, new double[] { 8, 4.5, 8, 20.5 }),
            ("fractional", false, new double[] { 8.123, 4.789, 35.321, 25.456 }),
            ("degenerate-linear", false, new double[] { 16, 16, 16, 16 }),
            ("radial", true, new double[] { 14, 12, 2, 25, 18, 14 }),
            ("concentric", true, new double[] { 16, 16, 0, 16, 16, 12 }),
            ("shrinking", true, new double[] { 16, 16, 12, 16, 16, 0 }),
            ("equal-radii", true, new double[] { 12, 16, 8, 28, 16, 8 }),
            ("cone", true, new double[] { 12, 12, 2, 24, 12, 14 }),
            ("identical-circles", true, new double[] { 16, 16, 8, 16, 16, 8 }),
            ("zero-radii", true, new double[] { 12, 16, 0, 28, 16, 0 })
        };
        foreach (var geometry in geometries)
        foreach (Extend extend in new[] { Extend.None, Extend.Pad, Extend.Repeat, Extend.Reflect })
        foreach (int stopSet in new[] { 0, 1, 2, 3 })
        foreach (double scale in new[] { 1.0, 1.5, 2 })
            yield return new object[] { geometry.Name, geometry.Radial, geometry.Coordinates, extend, stopSet, scale };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RecordedGradientMatchesIndependentNativeAndFrozenReplay(string name, bool radial, double[] coordinates, Extend extend, int stopSet, double scale)
    {
        int width = (int)(48 * scale), height = (int)(32 * scale);
        using var native = new ImageSurface(Format.Argb32, width, height); _ = native.Handle;
        using var recorded = new ImageSurface(Format.Argb32, width, height); recorded.BeginRecording();
            foreach (var surface in new[] { native, recorded }) {
                using var context = new Context(surface);
                context.Scale(scale, scale); context.Antialias = Antialias.None; context.Operator = Operator.Source;
                var c = coordinates;
                using Gradient gradient = radial ? new RadialGradient(c[0], c[1], c[2], c[3], c[4], c[5]) : new LinearGradient(c[0], c[1], c[2], c[3]);
                gradient.Extend = extend;
                if (stopSet == 1) gradient.AddColorStop(.4, new Color(.9, .3, .7, .35));
                if (stopSet >= 2) {
                    gradient.AddColorStop(.2, new Color(.1, .4, .8, .15));
                    gradient.AddColorStop(.5, new Color(.9, .2, .4, .7));
                    if (stopSet == 3) gradient.AddColorStop(.5, new Color(.2, .9, .1, .5));
                    gradient.AddColorStop(.8, new Color(.3, .7, .9, .35));
                }
                context.SetSource(gradient); context.Paint();
                gradient.AddColorStop(.1, new Color(0, 1, 0, 1));
                gradient.Matrix = new Matrix(1, 0, 0, 1, 50, 50);
            }
        Assert.Equal(SurfaceRecordingState.Recording, recorded.RecordingState);
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap)) {
            canvas.Clear(SKColors.Transparent);
            Assert.True(recorded.TryDrawRecordedCommands(canvas));
        }
        var actual = new byte[width * height * 4];
        Marshal.Copy(bitmap.GetPixels(), actual, 0, actual.Length);
        byte[] expected = native.Data;
        for (int i = 0; i < actual.Length; i++)
            Assert.True(Math.Abs(actual[i] - expected[i]) <= 1, $"{name}/{extend}/{stopSet}/{scale}, byte {i}: {actual[i]} != {expected[i]}");
        Assert.Equal(expected, recorded.Data);
    }
}
