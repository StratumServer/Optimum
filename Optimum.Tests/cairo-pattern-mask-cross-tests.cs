using System;
using Cairo;
using SkiaSharp;
using Xunit;

[Collection ("Cairo materialization metrics")]
public sealed class CairoPatternMaskCrossTests
{
    static CairoPatternMaskCrossTests() { _ = CairoAPI.Version; }

    [Theory]
    [InlineData(Extend.None)]
    [InlineData(Extend.Pad)]
    [InlineData(Extend.Repeat)]
    [InlineData(Extend.Reflect)]
    public void SurfacePatternWithExtendModesRendersUnderDenial(Extend extend)
    {
        int width = 32, height = 32;

        // Create an 8x8 source tile: top-left quadrant red, rest blue
        using var tile = new ImageSurface(Format.Argb32, 8, 8);
        using (var tc = new Context(tile))
        {
            tc.SetSourceRGB(0, 0, 1);
            tc.Paint();
            tc.SetSourceRGB(1, 0, 0);
            tc.Rectangle(0, 0, 4, 4);
            tc.Fill();
        }

        using var dest = new ImageSurface(Format.Argb32, width, height);
        using (var dc = new Context(dest))
        {
            using var pattern = new SurfacePattern(tile);
            pattern.Extend = extend;
            dc.SetSource(pattern);
            dc.Paint();
        }

        byte[] pixels = dest.Data;
        Assert.NotNull(pixels);
        Assert.Equal(width * 4 * height, pixels.Length);
        AssertNoNativeLoad();

        if (CairoNativeGuard.NativeAllowed)
        {
            using var nativeDest = new ImageSurface(Format.Argb32, width, height);
            _ = nativeDest.Handle; // force native
            using (var nc = new Context(nativeDest))
            {
                using var nativeTile = new ImageSurface(Format.Argb32, 8, 8);
                _ = nativeTile.Handle;
                using (var ntc = new Context(nativeTile))
                {
                    ntc.SetSourceRGB(0, 0, 1);
                    ntc.Paint();
                    ntc.SetSourceRGB(1, 0, 0);
                    ntc.Rectangle(0, 0, 4, 4);
                    ntc.Fill();
                }

                using var nativePattern = new SurfacePattern(nativeTile);
                nativePattern.Extend = extend;
                nc.SetSource(nativePattern);
                nc.Paint();
            }

            byte[] nativePixels = nativeDest.Data;
            Assert.NotNull(nativePixels);
            AssertPixelsMatchWithTolerance(nativePixels, pixels, width, height, 4);
        }
    }

    [Theory]
    [InlineData(Operator.Over)]
    [InlineData(Operator.In)]
    [InlineData(Operator.Out)]
    [InlineData(Operator.Atop)]
    [InlineData(Operator.DestOver)]
    [InlineData(Operator.Add)]
    public void LinearGradientSourceWithRadialMaskAndOperators(Operator op)
    {
        int width = 32, height = 32;

        using var dest = new ImageSurface(Format.Argb32, width, height);
        using (var context = new Context(dest))
        {
            // Initial background
            context.SetSourceRGBA(0.1, 0.2, 0.3, 0.5);
            context.Paint();

            // Linear gradient source
            using var source = new LinearGradient(0, 0, width, height);
            source.AddColorStop(0.0, new Color(1.0, 0.0, 0.0, 1.0));
            source.AddColorStop(1.0, new Color(0.0, 1.0, 0.0, 1.0));
            context.SetSource(source);

            // Radial gradient mask
            using var mask = new RadialGradient(width / 2.0, height / 2.0, 2, width / 2.0, height / 2.0, 14);
            mask.AddColorStop(0.0, new Color(1.0, 1.0, 1.0, 1.0));
            mask.AddColorStop(1.0, new Color(0.0, 0.0, 0.0, 0.0));

            context.Operator = op;
            context.Mask(mask);
        }

        byte[] pixels = dest.Data;
        Assert.NotNull(pixels);
        AssertNoNativeLoad();

        if (CairoNativeGuard.NativeAllowed)
        {
            using var nativeDest = new ImageSurface(Format.Argb32, width, height);
            _ = nativeDest.Handle;
            using (var nc = new Context(nativeDest))
            {
                nc.SetSourceRGBA(0.1, 0.2, 0.3, 0.5);
                nc.Paint();

                using var nSource = new LinearGradient(0, 0, width, height);
                nSource.AddColorStop(0.0, new Color(1.0, 0.0, 0.0, 1.0));
                nSource.AddColorStop(1.0, new Color(0.0, 1.0, 0.0, 1.0));
                nc.SetSource(nSource);

                using var nMask = new RadialGradient(width / 2.0, height / 2.0, 2, width / 2.0, height / 2.0, 14);
                nMask.AddColorStop(0.0, new Color(1.0, 1.0, 1.0, 1.0));
                nMask.AddColorStop(1.0, new Color(0.0, 0.0, 0.0, 0.0));

                nc.Operator = op;
                nc.Mask(nMask);
            }

            AssertPixelsMatchWithTolerance(nativeDest.Data, pixels, width, height, 4);
        }
    }

    [Fact]
    public void RotatedSurfacePatternWithRadialGradientMaskParity()
    {
        int width = 32, height = 32;

        using var tile = new ImageSurface(Format.Argb32, 16, 16);
        using (var tc = new Context(tile))
        {
            tc.SetSourceRGB(0.8, 0.4, 0.1);
            tc.Paint();
            tc.SetSourceRGB(0.1, 0.9, 0.3);
            tc.Rectangle(4, 4, 8, 8);
            tc.Fill();
        }

        using var dest = new ImageSurface(Format.Argb32, width, height);
        using (var dc = new Context(dest))
        {
            using var surfacePattern = new SurfacePattern(tile);
            surfacePattern.Extend = Extend.Repeat;

            // Apply 45-degree rotation matrix to pattern
            var m = new Matrix();
            m.InitRotate(Math.PI / 4.0);
            surfacePattern.Matrix = m;

            dc.SetSource(surfacePattern);

            using var radialMask = new RadialGradient(16, 16, 4, 16, 16, 15);
            radialMask.AddColorStop(0.0, new Color(1, 1, 1, 1));
            radialMask.AddColorStop(1.0, new Color(0, 0, 0, 0));

            dc.Mask(radialMask);
        }

        byte[] pixels = dest.Data;
        Assert.NotNull(pixels);
        AssertNoNativeLoad();

        if (CairoNativeGuard.NativeAllowed)
        {
            using var nativeDest = new ImageSurface(Format.Argb32, width, height);
            _ = nativeDest.Handle;

            using var nTile = new ImageSurface(Format.Argb32, 16, 16);
            _ = nTile.Handle;
            using (var ntc = new Context(nTile))
            {
                ntc.SetSourceRGB(0.8, 0.4, 0.1);
                ntc.Paint();
                ntc.SetSourceRGB(0.1, 0.9, 0.3);
                ntc.Rectangle(4, 4, 8, 8);
                ntc.Fill();
            }

            using (var nc = new Context(nativeDest))
            {
                using var nPattern = new SurfacePattern(nTile);
                nPattern.Extend = Extend.Repeat;
                var nm = new Matrix();
                nm.InitRotate(Math.PI / 4.0);
                nPattern.Matrix = nm;

                nc.SetSource(nPattern);

                using var nRadialMask = new RadialGradient(16, 16, 4, 16, 16, 15);
                nRadialMask.AddColorStop(0.0, new Color(1, 1, 1, 1));
                nRadialMask.AddColorStop(1.0, new Color(0, 0, 0, 0));

                nc.Mask(nRadialMask);
            }

            AssertPixelsMatchWithTolerance(nativeDest.Data, pixels, width, height, 8);
        }
    }

    [Fact]
    public void SurfacePatternMaskOnLinearGradientSource()
    {
        int width = 24, height = 24;

        // Mask surface: diamond shape
        using var maskSurface = new ImageSurface(Format.A8, 24, 24);
        using (var mc = new Context(maskSurface))
        {
            mc.MoveTo(12, 2);
            mc.LineTo(22, 12);
            mc.LineTo(12, 22);
            mc.LineTo(2, 12);
            mc.ClosePath();
            mc.SetSourceRGB(1, 1, 1);
            mc.Fill();
        }

        using var dest = new ImageSurface(Format.Argb32, width, height);
        using (var dc = new Context(dest))
        {
            using var grad = new LinearGradient(0, 0, 24, 0);
            grad.AddColorStop(0, new Color(1, 0, 0, 1));
            grad.AddColorStop(1, new Color(0, 0, 1, 1));
            dc.SetSource(grad);
            dc.MaskSurface(maskSurface, 0, 0);
        }

        byte[] pixels = dest.Data;
        Assert.NotNull(pixels);
        AssertNoNativeLoad();

        // Center pixel (12, 12) must be inside the diamond and opaque
        int centerIdx = (12 * 24 + 12) * 4;
        Assert.True(pixels[centerIdx + 3] > 200, $"Expected opaque center pixel, got alpha {pixels[centerIdx + 3]}");

        // Corner pixel (0, 0) must be outside the diamond and transparent
        Assert.Equal(0, pixels[3]);
    }

    static void AssertPixelsMatchWithTolerance(byte[] expected, byte[] actual, int width, int height, int maxDiff)
    {
        Assert.Equal(expected.Length, actual.Length);
        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * stride + x * 4;
                for (int c = 0; c < 4; c++)
                {
                    int diff = Math.Abs(expected[idx + c] - actual[idx + c]);
                    Assert.True(diff <= maxDiff,
                        $"Pixel diff at ({x},{y}) ch={c}: expected {expected[idx + c]}, got {actual[idx + c]} (diff {diff} > {maxDiff})");
                }
            }
        }
    }

    static void AssertNoNativeLoad()
    {
        if (!CairoNativeGuard.NativeAllowed && CairoNativeGuard.LoadedLibcairoMappings() is string[] mappings)
            Assert.Empty(mappings);
    }
}
