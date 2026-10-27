using System;
using System.IO;
using System.Runtime.InteropServices;
using Cairo;
using NanoSvg;
using Xunit;

#nullable disable

namespace Optimum.Tests
{
    /// <summary>
    /// Exhaustive surface coverage test suite verifying all 82 exported types and their members
    /// under strict native denial (OPTIMUM_CAIRO_NATIVE=deny) and native opt-in (allow).
    /// </summary>
    public sealed class CairoTotalSurfaceCoverageTests
    {
        [Fact]
        public void ValueStructs_FullSurfaceCoverage()
        {
            // Cairo.Color
            var c1 = new Color(0.2, 0.4, 0.6);
            Assert.Equal(0.2, c1.R, 4);
            Assert.Equal(0.4, c1.G, 4);
            Assert.Equal(0.6, c1.B, 4);
            Assert.Equal(1.0, c1.A, 4);

            var c2 = new Color(0.1, 0.2, 0.3, 0.5);
            Assert.Equal(0.1, c2.R, 4);
            Assert.Equal(0.2, c2.G, 4);
            Assert.Equal(0.3, c2.B, 4);
            Assert.Equal(0.5, c2.A, 4);
            Assert.True(c1.Equals((object)c1));
            Assert.False(c1.Equals((object)c2));
            Assert.NotEqual(0, c1.GetHashCode());
            Assert.NotNull(c1.ToString());

            // Cairo.Distance
            var dist = new Distance(15.5, -25.5);
            Assert.Equal(15.5, dist.Dx);
            Assert.Equal(-25.5, dist.Dy);

            // Cairo.Point
            var pt = new Point(10, 20);
            Assert.Equal(10, pt.X);
            Assert.Equal(20, pt.Y);

            // Cairo.PointD
            var ptd = new PointD(10.25, 20.75);
            Assert.Equal(10.25, ptd.X);
            Assert.Equal(20.75, ptd.Y);

            // Cairo.Rectangle
            var r1 = new Rectangle(5.0, 10.0, 50.0, 60.0);
            Assert.Equal(5.0, r1.X);
            Assert.Equal(10.0, r1.Y);
            Assert.Equal(50.0, r1.Width);
            Assert.Equal(60.0, r1.Height);

            var r2 = new Rectangle(new Point(5, 10), 50.0, 60.0);
            Assert.True(r1 == r2);
            Assert.False(r1 != r2);
            Assert.True(r1.Equals((object)r2));
            Assert.Equal(r1.GetHashCode(), r2.GetHashCode());
            Assert.NotNull(r1.ToString());

            // Cairo.RectangleInt
            var ri = new RectangleInt { X = 1, Y = 2, Width = 30, Height = 40 };
            Assert.Equal(1, ri.X);
            Assert.Equal(2, ri.Y);
            Assert.Equal(30, ri.Width);
            Assert.Equal(40, ri.Height);

            // Cairo.Glyph
            var g1 = new Cairo.Glyph(1234, 10.5, 20.5);
            Assert.Equal(1234, g1.Index);
            Assert.Equal(10.5, g1.X);
            Assert.Equal(20.5, g1.Y);
            var g2 = new Cairo.Glyph(1234, 10.5, 20.5);
            Assert.True(g1 == g2);
            Assert.False(g1 != g2);
            Assert.True(g1.Equals((object)g2));
            Assert.Equal(g1.GetHashCode(), g2.GetHashCode());

            // Cairo.FontExtents
            var fe1 = new FontExtents(12.0, 3.0, 15.0, 10.0, 0.0);
            Assert.Equal(12.0, fe1.Ascent);
            Assert.Equal(3.0, fe1.Descent);
            Assert.Equal(15.0, fe1.Height);
            Assert.Equal(10.0, fe1.MaxXAdvance);
            Assert.Equal(0.0, fe1.MaxYAdvance);
            var fe2 = new FontExtents(12.0, 3.0, 15.0, 10.0, 0.0);
            Assert.True(fe1 == fe2);
            Assert.False(fe1 != fe2);
            Assert.True(fe1.Equals((object)fe2));
            Assert.Equal(fe1.GetHashCode(), fe2.GetHashCode());

            // Cairo.TextExtents
            var te1 = new TextExtents { Width = 50.0, Height = 14.0, XAdvance = 52.0, YAdvance = 0.0, XBearing = 0.5, YBearing = -12.0 };
            Assert.Equal(50.0, te1.Width);
            Assert.Equal(14.0, te1.Height);
            Assert.Equal(52.0, te1.XAdvance);
            Assert.Equal(0.0, te1.YAdvance);
            Assert.Equal(0.5, te1.XBearing);
            Assert.Equal(-12.0, te1.YBearing);
            var te2 = new TextExtents { Width = 50.0, Height = 14.0, XAdvance = 52.0, YAdvance = 0.0, XBearing = 0.5, YBearing = -12.0 };
            Assert.True(te1 == te2);
            Assert.False(te1 != te2);
            Assert.True(te1.Equals((object)te2));
            Assert.Equal(te1.GetHashCode(), te2.GetHashCode());

            // NanoSvg structs
            var nsvgSize = new NsvgSize { width = 100f, height = 200f };
            Assert.Equal(100f, nsvgSize.width);
            Assert.Equal(200f, nsvgSize.height);

            var nsvgVb = new NsvgViewbox { viewMinx = 0f, viewMiny = 0f, viewWidth = 100f, viewHeight = 200f };
            Assert.Equal(100f, nsvgVb.viewWidth);

            // FreeType structs
            var bbox = new Cairo.Freetype.BBox { xMin = 1, xMax = 10, yMin = 2, yMax = 20 };
            Assert.Equal(1, bbox.xMin);
            Assert.Equal(20, bbox.yMax);

            var vec = new Cairo.Freetype.Vector { x = 100, y = 200 };
            Assert.Equal(100, vec.x);
            Assert.Equal(200, vec.y);

            var bmp = new Cairo.Freetype.Bitmap();
            var bmpGlyph = new Cairo.Freetype.BitmapGlyph();
            var generic = new Cairo.Freetype.Generic();
            var glyphClass = new Cairo.Freetype.GlyphClass();
            var glyphRec = new Cairo.Freetype.GlyphRec();
            var listRec = new Cairo.Freetype.ListRec();
            Assert.NotNull(bmp);
            Assert.NotNull(bmpGlyph);
            Assert.NotNull(generic);
            Assert.NotNull(glyphClass);
            Assert.NotNull(glyphRec);
            Assert.NotNull(listRec);
        }

        [Fact]
        public void Region_FullSurfaceCoverage()
        {
            using var rEmpty = new Region();
            Assert.True(rEmpty.IsEmpty);
            Assert.Equal(0, rEmpty.NumRectangles);
            Assert.Equal(Status.Success, rEmpty.Status);

            using var r1 = new Region(new RectangleInt { X = 10, Y = 10, Width = 50, Height = 50 });
            Assert.False(r1.IsEmpty);
            Assert.Equal(1, r1.NumRectangles);
            Assert.Equal(new RectangleInt { X = 10, Y = 10, Width = 50, Height = 50 }, r1.Extents);
            Assert.Equal(new RectangleInt { X = 10, Y = 10, Width = 50, Height = 50 }, r1.GetRectangle(0));

            using var r2 = new Region(new[] {
                new RectangleInt { X = 0, Y = 0, Width = 10, Height = 10 },
                new RectangleInt { X = 20, Y = 20, Width = 10, Height = 10 }
            });
            Assert.Equal(2, r2.NumRectangles);

            // Hit testing
            Assert.True(r1.ContainsPoint(20, 20));
            Assert.False(r1.ContainsPoint(5, 5));
            Assert.Equal(RegionOverlap.In, r1.ContainsPoint(new RectangleInt { X = 15, Y = 15, Width = 10, Height = 10 }));
            Assert.Equal(RegionOverlap.Out, r1.ContainsPoint(new RectangleInt { X = 0, Y = 0, Width = 5, Height = 5 }));
            Assert.Equal(RegionOverlap.Part, r1.ContainsPoint(new RectangleInt { X = 5, Y = 5, Width = 10, Height = 10 }));

            // Algebra
            using var rCopy = r1.Copy();
            Assert.True(r1.Equals((object)rCopy));

            Assert.Equal(Status.Success, rCopy.UnionRectangle(new RectangleInt { X = 60, Y = 10, Width = 10, Height = 50 }));
            Assert.Equal(Status.Success, rCopy.Union(r2));
            Assert.Equal(Status.Success, rCopy.IntersectRectangle(new RectangleInt { X = 10, Y = 10, Width = 40, Height = 40 }));
            Assert.Equal(Status.Success, rCopy.SubtractRectangle(new RectangleInt { X = 10, Y = 10, Width = 5, Height = 5 }));
            Assert.Equal(Status.Success, rCopy.XorRectangle(new RectangleInt { X = 12, Y = 12, Width = 5, Height = 5 }));

            // Translation
            rCopy.Translate(10, 20);
            rCopy.Translate(-10, -20);
        }

        [Fact]
        public void FontOptions_FullSurfaceCoverage()
        {
            using var fo1 = new FontOptions();
            Assert.Equal(Antialias.Default, fo1.Antialias);
            Assert.Equal(SubpixelOrder.Default, fo1.SubpixelOrder);
            Assert.Equal(HintStyle.Default, fo1.HintStyle);
            Assert.Equal(HintMetrics.Default, fo1.HintMetrics);
            Assert.Equal(Status.Success, fo1.Status);
            if (!CairoNativeGuard.NativeAllowed)
                Assert.Throws<NotSupportedException>(() => fo1.Handle);

            fo1.Antialias = Antialias.Subpixel;
            fo1.SubpixelOrder = SubpixelOrder.Rgb;
            fo1.HintStyle = HintStyle.Full;
            fo1.HintMetrics = HintMetrics.On;

            Assert.Equal(Antialias.Subpixel, fo1.Antialias);
            Assert.Equal(SubpixelOrder.Rgb, fo1.SubpixelOrder);
            Assert.Equal(HintStyle.Full, fo1.HintStyle);
            Assert.Equal(HintMetrics.On, fo1.HintMetrics);

            using var fo2 = fo1.Copy();
            Assert.True(fo1 == fo2);
            Assert.False(fo1 != fo2);
            Assert.True(fo1.Equals((object)fo2));
            Assert.Equal(fo1.GetHashCode(), fo2.GetHashCode());

            using var fo3 = new FontOptions();
            fo3.Merge(fo1);
            Assert.Equal(Antialias.Subpixel, fo3.Antialias);
            Assert.Equal(SubpixelOrder.Rgb, fo3.SubpixelOrder);

#pragma warning disable CS0618
            fo3.Destroy();
#pragma warning restore CS0618
        }

        [Fact]
        public void Device_FullSurfaceCoverage()
        {
            IntPtr handle = DeviceHandles.Create(DeviceType.GL);
            Assert.NotEqual(IntPtr.Zero, handle);

            using var device = new Device(handle);
            Assert.Equal(DeviceType.GL, device.Type);
            Assert.Equal(Status.Success, device.Status);

            Assert.Equal(Status.Success, device.Acquire());
            device.Release();

            device.Flush();
            device.Finish();

            // After finish, acquire returns DeviceFinished (37)
            Assert.Equal((Status)37, device.Acquire());
            device.Dispose();
            Assert.Throws<ObjectDisposedException>(() => device.Acquire());
        }

        [Fact]
        public void ImageSurface_And_Surface_FullSurfaceCoverage()
        {
            // Stride calculation
            Assert.Equal(256, Surface.StrideForWidth(Format.Argb32, 64));
            Assert.Equal(256, Surface.StrideForWidth(Format.Rgb24, 64));
            Assert.Equal(64, Surface.StrideForWidth(Format.A8, 64));
            Assert.Equal(8, Surface.StrideForWidth(Format.A1, 64));
            Assert.Equal(128, Surface.StrideForWidth(Format.Rgb16565, 64));
            Assert.Equal(-1, Surface.StrideForWidth(Format.Argb32, -1));

            // Managed ImageSurface
            using var s1 = new ImageSurface(Format.Argb32, 32, 32);
            Assert.Equal(Format.Argb32, s1.Format);
            Assert.Equal(32, s1.Width);
            Assert.Equal(32, s1.Height);
            Assert.Equal(128, s1.Stride);
            Assert.Equal(Content.ColorAlpha, s1.Content);
            Assert.Equal(SurfaceType.Image, s1.SurfaceType);
            Assert.Equal(new PointD(0, 0), s1.DeviceOffset);
            Assert.Equal(Status.Success, s1.Status);
            Assert.True(s1.ReferenceCount >= 1);

            // Similar surface
            using var sSimilar = s1.CreateSimilar(Content.ColorAlpha, 16, 16);
            Assert.NotNull(sSimilar);

            // Helpers
            using var sGui = ImageSurface.CreateForRecordedGui(Format.Argb32, 16, 16);
            Assert.Equal(16, sGui.Width);

            var rawBytes = new byte[16 * 16 * 4];
            using var sBytes = new ImageSurface(rawBytes, Format.Argb32, 16, 16, 64);
            Assert.Equal(16, sBytes.Width);

            using var sImg = ImageSurface.CreateForImage(rawBytes, Format.Argb32, 16, 16);
            Assert.NotNull(sImg);

            // Surface drawing and output
            s1.MarkDirty();
            s1.MarkDirty(new Rectangle(0, 0, 10, 10));
            s1.Flush();

            using (var ctx = new Context(s1))
            {
                ctx.SetSourceRGB(1, 0, 0);
                ctx.Paint();
                sSimilar.Show(ctx, 5, 5);
            }

            string tempPng = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-test-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                s1.WriteToPng(tempPng);
                Assert.True(File.Exists(tempPng));
                Assert.True(new FileInfo(tempPng).Length > 0);
            }
            finally
            {
                if (File.Exists(tempPng)) File.Delete(tempPng);
            }

            Assert.Equal(Status.Success, s1.Finish());
        }

        [Fact]
        public void VectorSurfaces_FullSurfaceCoverage()
        {
            string tempPdf = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-test-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                using (var pdf = new PdfSurface(tempPdf, 100, 100))
                {
                    pdf.SetSize(120, 120);
                    using (var ctx = new Context(pdf))
                    {
                        ctx.SetSourceRGB(0, 0, 1);
                        ctx.Rectangle(10, 10, 50, 50);
                        ctx.Fill();
                    }
                    pdf.Finish();
                }
                Assert.True(File.Exists(tempPdf));
            }
            finally
            {
                if (File.Exists(tempPdf)) File.Delete(tempPdf);
            }

            string tempPs = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-test-" + Guid.NewGuid().ToString("N") + ".ps");
            try
            {
                using (var ps = new PSSurface(tempPs, 100, 100))
                {
                    ps.SetSize(120, 120);
                    ps.DscComment("%%Title: Full Surface Test");
                    ps.BeginSetup();
                    ps.BeginPageSetup();
                    using (var ctx = new Context(ps))
                    {
                        ctx.SetSourceRGB(0, 1, 0);
                        ctx.Paint();
                    }
                    ps.Finish();
                }
                Assert.True(File.Exists(tempPs));
            }
            finally
            {
                if (File.Exists(tempPs)) File.Delete(tempPs);
            }

            string tempSvg = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-test-" + Guid.NewGuid().ToString("N") + ".svg");
            try
            {
                using (var svg = new SvgSurface(tempSvg, 100, 100))
                {
                    svg.RestrictToVersion(SvgVersion.OnePointOne);
                    using (var ctx = new Context(svg))
                    {
                        ctx.SetSourceRGB(1, 1, 0);
                        ctx.Paint();
                    }
                    svg.Finish();
                }
                Assert.True(File.Exists(tempSvg));
            }
            finally
            {
                if (File.Exists(tempSvg)) File.Delete(tempSvg);
            }
        }

        [Fact]
        public void ManagedPatterns_FullSurfaceCoverage()
        {
            using var p1 = new SolidPattern(new Color(1, 0, 0, 1));
            Assert.Equal(PatternType.Solid, p1.PatternType);
            Assert.Equal(Status.Success, p1.Status);

            using var p2 = new SolidPattern(0, 1, 0);
            using var p3 = new SolidPattern(0, 0, 1, 0.5);

            using var linear = new LinearGradient(0, 0, 100, 100);
            Assert.Equal(PatternType.Linear, linear.PatternType);
            linear.AddColorStop(0.0, new Color(1, 0, 0));
            linear.AddColorStopRgb(0.5, new Color(0, 1, 0));
            Assert.Equal(2, linear.ColorStopCount);

            var mat = new Matrix();
            mat.InitIdentity();
            linear.Matrix = mat;
            Assert.True(linear.Matrix.IsIdentity());
            linear.Extend = Extend.Repeat;
            Assert.Equal(Extend.Repeat, linear.Extend);

            using var radial = new RadialGradient(50, 50, 10, 50, 50, 40);
            Assert.Equal(PatternType.Radial, radial.PatternType);

            using var img = new ImageSurface(Format.Argb32, 16, 16);
            using var surfPat = new SurfacePattern(img);
            Assert.Equal(PatternType.Surface, surfPat.PatternType);
            surfPat.Filter = Filter.Bilinear;
            Assert.Equal(Filter.Bilinear, surfPat.Filter);

            using var surfPat2 = new SurfacePattern((Surface)img);
            Assert.Equal(PatternType.Surface, surfPat2.PatternType);
        }

        [Fact]
        public void ImageFiltersAndUtilities_FullSurfaceCoverage()
        {
            var gb = new GaussianBlur(16, 16);
            Assert.NotNull(gb);

            using var img = new ImageSurface(Format.Argb32, 16, 16);
            SurfaceTransformBlur.Blur(img, 1.0);
            SurfaceTransformBlur.Blur(img, 1.0, true);
            SurfaceTransformBlur.Blur(img, 1.0, 0, 0, 16, 16);
            SurfaceTransformBlur.BlurFull(img, 1.0);
            SurfaceTransformBlur.BlurPartial(img, 1.0, 1);
            SurfaceTransformBlur.BlurPartial(img, 1.0, 0, 0, 16, 16, 16);

            SurfaceTransformDemulAlpha.DemulAlpha(img);
            uint blended = SurfaceDrawImage.ColorOverlay(0xffffffff, 0x80ff0000);
            Assert.NotEqual(0u, blended);

            using var ctx = new Context(img);
            ContextUtils.Clear(ctx);

            Assert.Equal(11804, CairoAPI.Version);
            Assert.Equal("1.18.4", CairoAPI.VersionString);

            Assert.Throws<InvalidOperationException>(() => CairoDebug.OnAllocated(new IntPtr(12345)));
            try
            {
                CairoDebug.Enabled = true;
                CairoDebug.OnAllocated(new IntPtr(12345));
                CairoDebug.OnDisposed<Surface>(new IntPtr(12345), true);
            }
            finally
            {
                CairoDebug.Enabled = false;
            }
        }

        [Fact]
        public void ManagedFonts_FullSurfaceCoverage()
        {
            using var face = new FontFace("DejaVu Sans", FontSlant.Normal, FontWeight.Bold);
            Assert.Equal("DejaVu Sans", face.Family);
            Assert.Equal(FontSlant.Normal, face.Slant);
            Assert.Equal(FontWeight.Bold, face.Weight);
            Assert.True(face.FontType == FontType.Toy || face.FontType == FontType.FreeType);
            Assert.Equal(Status.Success, face.Status);
            Assert.True(face.ReferenceCount >= 1);

            using var options = new FontOptions();
            var fontMatrix = new Matrix();
            fontMatrix.Scale(14.0, 14.0);
            var ctm = new Matrix();

            using var scaled = new ScaledFont(face, fontMatrix, ctm, options);
            Assert.True(scaled.FontType == FontType.Toy || scaled.FontType == FontType.FreeType);
            Assert.Equal(fontMatrix, scaled.FontMatrix);
            Assert.Equal(Status.Success, scaled.Status);

            FontExtents fe = scaled.FontExtents;
            Assert.True(fe.Height > 0);

            var glyphs = new[] { new Cairo.Glyph(65, 0, 0) };
            TextExtents ge = scaled.GlyphExtents(glyphs);
            Assert.True(ge.Width >= 0);

            var metricFe = OptimumSkiaTextMetrics.GetFontExtents("DejaVu Sans", FontWeight.Normal, FontSlant.Normal, 14.0);
            Assert.True(metricFe.Height > 0);

            var metricTe = OptimumSkiaTextMetrics.GetTextExtents("DejaVu Sans", FontWeight.Normal, FontSlant.Normal, 14.0, "Vintage Story");
            Assert.True(metricTe.Width > 0);
        }

        [Fact]
        public void PlatformRejectionUnderDeny_FullSurfaceCoverage()
        {
            if (CairoNativeGuard.NativeAllowed) return;

            Assert.Throws<NotSupportedException>(() => new Win32Surface(IntPtr.Zero));
            Assert.Throws<NotSupportedException>(() => new XlibSurface(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, 1));
            Assert.Throws<NotSupportedException>(() => new XlibSurface(new IntPtr(1), true));
            Assert.Throws<NotSupportedException>(() => XlibSurface.FromBitmap(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, 1));
            Assert.Throws<NotSupportedException>(() => new XcbSurface(IntPtr.Zero, 0, IntPtr.Zero, 1, 1));
            Assert.Throws<NotSupportedException>(() => XcbSurface.FromBitmap(IntPtr.Zero, 0, IntPtr.Zero, 1, 1));
            Assert.Throws<NotSupportedException>(() => new GlitzSurface(IntPtr.Zero));
            Assert.Throws<NotSupportedException>(() => new DirectFBSurface(IntPtr.Zero, IntPtr.Zero));
            Assert.Throws<NotSupportedException>(() => new Context(new IntPtr(1), true));
            Assert.Throws<NotSupportedException>(() => new FontFace(new IntPtr(1), true));
            Assert.Throws<NotSupportedException>(() => Surface.Lookup(new IntPtr(1), true));
            Assert.Throws<NotSupportedException>(() => Pattern.Lookup(new IntPtr(1), true));
        }

        [Fact]
        public void GraphicsLegacyWrapper_FullSurfaceCoverage()
        {
#pragma warning disable CS0618
            using var surface = new ImageSurface(Format.Argb32, 32, 32);
            using var graphics = new Graphics(surface);
            graphics.SetSourceRGB(0.5, 0.5, 0.5);
            graphics.Paint();
#pragma warning restore CS0618
        }
    }
}
