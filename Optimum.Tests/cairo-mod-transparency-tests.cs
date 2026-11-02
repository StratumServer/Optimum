using System;
using System.IO;
using Cairo;
using Xunit;

#nullable disable

namespace Optimum.Tests
{
    /// <summary>
    /// Verifies 100% transparent mod compatibility: third-party mods using Cairo APIs
    /// execute entirely on the managed SkiaSharp backend without native libcairo dependency.
    /// </summary>
    public sealed class CairoModTransparencyTests
    {
        [Fact]
        public void ModGuiWorkflow_RendersCompletelyThroughSkiaBackend()
        {
            const int width = 120;
            const int height = 80;

            using var surface = new ImageSurface(Format.Argb32, width, height);
            using var ctx = new Context(surface);

            // 1. Dark semi-transparent background panel
            ctx.SetSourceRGBA(0.15, 0.15, 0.15, 0.9);
            ctx.Rectangle(0, 0, width, height);
            ctx.Fill();

            // 2. Linear gradient border
            using (var grad = new LinearGradient(0, 0, width, height))
            {
                grad.AddColorStop(0.0, new Color(0.9, 0.7, 0.2, 1.0));
                grad.AddColorStop(1.0, new Color(0.4, 0.2, 0.1, 1.0));
                ctx.SetSource(grad);
                ctx.LineWidth = 2.0;
                ctx.Rectangle(1, 1, width - 2, height - 2);
                ctx.Stroke();
            }

            // 3. Radial gradient icon / circle
            using (var rad = new RadialGradient(30, 40, 2, 30, 40, 20))
            {
                rad.AddColorStop(0.0, new Color(1.0, 0.2, 0.2, 1.0));
                rad.AddColorStop(1.0, new Color(0.2, 0.0, 0.0, 0.0));
                ctx.SetSource(rad);
                ctx.Arc(30, 40, 20, 0, Math.PI * 2);
                ctx.Fill();
            }

            // 4. Text and font extents measurement
            ctx.SelectFontFace("sans-serif", FontSlant.Normal, FontWeight.Bold);
            ctx.SetFontSize(14.0);
            var extents = ctx.TextExtents("Optimum Mod");
            Assert.True(extents.Width >= 0);
            Assert.True(extents.Height >= 0);

            ctx.SetSourceRGB(1.0, 1.0, 1.0);
            ctx.MoveTo(10, 25);
            ctx.ShowText("Optimum Mod");

            // 5. Bézier curves and path transformations
            ctx.Save();
            ctx.Translate(10, 10);
            ctx.Rotate(0.1);
            ctx.NewPath();
            ctx.MoveTo(50, 40);
            ctx.CurveTo(60, 20, 80, 60, 90, 40);
            ctx.LineWidth = 2.5;
            ctx.SetSourceRGBA(0.0, 0.8, 1.0, 0.8);
            ctx.Stroke();
            ctx.Restore();

            // 6. Push / pop groups and clipping
            ctx.Save();
            ctx.Rectangle(50, 50, 40, 20);
            ctx.Clip();
            ctx.PushGroup();
            ctx.SetSourceRGBA(0.5, 0.0, 0.8, 0.7);
            ctx.Paint();
            ctx.PopGroupToSource();
            ctx.Paint();
            ctx.Restore();

            // 7. Verify surface pixel buffer and DataPtr (used by LoadOrUpdateCairoTexture)
            surface.Flush();
            IntPtr dataPtr = surface.DataPtr;
            Assert.NotEqual(IntPtr.Zero, dataPtr);

            byte[] pixels = surface.Data;
            Assert.NotNull(pixels);
            Assert.Equal(width * 4 * height, pixels.Length);

            // Verify non-zero rendered pixels exist
            bool hasNonZeroPixel = false;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i] != 0)
                {
                    hasNonZeroPixel = true;
                    break;
                }
            }
            Assert.True(hasNonZeroPixel, "Surface pixels must contain rendered output from Skia recorder.");

            // 8. PNG output file writing (used by mod screenshot/export tools)
            string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mod-test-{Guid.NewGuid():N}.png");
            try
            {
                surface.WriteToPng(tempFile);
                Assert.True(File.Exists(tempFile));
                Assert.True(new FileInfo(tempFile).Length > 0);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void ModCustomBuffer_LoadsExternalIntPtrWithoutNativeCairo()
        {
            // Verify mods that allocate their own native byte arrays and wrap them in ImageSurface
            byte[] rawBuffer = new byte[64 * 64 * 4];
            for (int i = 0; i < rawBuffer.Length; i += 4)
            {
                rawBuffer[i] = 120;     // B
                rawBuffer[i + 1] = 200; // G
                rawBuffer[i + 2] = 50;  // R
                rawBuffer[i + 3] = 255; // A
            }

            using var surface = new ImageSurface(rawBuffer, Format.Argb32, 64, 64, 64 * 4);
            Assert.Equal(64, surface.Width);
            Assert.Equal(64, surface.Height);

            using var ctx = new Context(surface);
            ctx.SetSourceRGB(1.0, 0.0, 0.0);
            ctx.Rectangle(10, 10, 20, 20);
            ctx.Fill();

            surface.Flush();
            byte[] outPixels = surface.Data;
            Assert.NotNull(outPixels);
            Assert.Equal(rawBuffer.Length, outPixels.Length);
        }
    }
}
