using System;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Pure-math coverage for Optimum's TAA conventions: the Halton jitter sequence,
/// the projection-shear jitter applied to Mat4d.Perspective output, and the
/// per-upscaler motion vector unit conventions. All of it lives in
/// OptimumTemporalMath so it can be tested without a render context.
/// </summary>
public class TemporalConventionsTests
{
    // --- Halton(2,3) sequence -------------------------------------------------

    [Theory]
    [InlineData(1, 0.5)]
    [InlineData(2, 0.25)]
    [InlineData(3, 0.75)]
    [InlineData(4, 0.125)]
    [InlineData(5, 0.625)]
    [InlineData(6, 0.375)]
    [InlineData(7, 0.875)]
    [InlineData(8, 0.0625)]
    public void HaltonBase2MatchesKnownValues(int index, double expected)
    {
        Assert.Equal(expected, OptimumTemporalMath.Halton(index, 2), 12);
    }

    [Theory]
    [InlineData(1, 1.0 / 3.0)]
    [InlineData(2, 2.0 / 3.0)]
    [InlineData(3, 1.0 / 9.0)]
    [InlineData(4, 4.0 / 9.0)]
    [InlineData(5, 7.0 / 9.0)]
    [InlineData(6, 2.0 / 9.0)]
    [InlineData(7, 5.0 / 9.0)]
    [InlineData(8, 8.0 / 9.0)]
    public void HaltonBase3MatchesKnownValues(int index, double expected)
    {
        Assert.Equal(expected, OptimumTemporalMath.Halton(index, 3), 12);
    }

    // --- Jitter phase count -----------------------------------------------------

    [Theory]
    [InlineData(1.0f, 8)]
    [InlineData(0.5f, 2)]
    [InlineData(0.75f, 5)] // ceil(8 * 0.5625) = ceil(4.5) = 5
    [InlineData(2.0f, 32)]
    public void JitterPhaseCountIsCeilingOfEightScaleSquared(float scale, int expected)
    {
        Assert.Equal(expected, OptimumTemporalMath.JitterPhaseCount(scale));
    }

    // --- Projection shear --------------------------------------------------------

    [Theory]
    [InlineData(1.5)]
    [InlineData(-2.0)]
    [InlineData(0.0)]
    public void JitterShearMovesStaticPointByExactlyJPixelsX(double jitterX)
    {
        const double width = 1920.0;
        const double height = 1080.0;

        double[] unjittered = Perspective(width, height);
        double[] jittered = (double[])unjittered.Clone();
        OptimumTemporalMath.ApplyProjectionJitter(jittered, jitterX, 0, width, height);

        // A static point somewhere in front of the camera.
        double viewX = 3.2;
        double viewY = -1.7;
        double viewZ = -10.0; // negative: in front of the camera

        (double px0, double py0) = ProjectToPixel(unjittered, viewX, viewY, viewZ, width, height);
        (double px1, double py1) = ProjectToPixel(jittered, viewX, viewY, viewZ, width, height);

        Assert.Equal(jitterX, px1 - px0, 9);
        Assert.Equal(0.0, py1 - py0, 9);
    }

    [Theory]
    [InlineData(2.25)]
    [InlineData(-0.6)]
    [InlineData(0.0)]
    public void JitterShearMovesStaticPointByExactlyJPixelsY(double jitterY)
    {
        const double width = 1920.0;
        const double height = 1080.0;

        double[] unjittered = Perspective(width, height);
        double[] jittered = (double[])unjittered.Clone();
        OptimumTemporalMath.ApplyProjectionJitter(jittered, 0, jitterY, width, height);

        double viewX = -0.4;
        double viewY = 2.1;
        double viewZ = -25.0;

        (double px0, double py0) = ProjectToPixel(unjittered, viewX, viewY, viewZ, width, height);
        (double px1, double py1) = ProjectToPixel(jittered, viewX, viewY, viewZ, width, height);

        Assert.Equal(0.0, px1 - px0, 9);
        Assert.Equal(jitterY, py1 - py0, 9);
    }

    /// <summary>
    /// Builds a column-major perspective matrix exactly as Mat4d.Perspective does
    /// (float[16]/double[16] layout, clip.w = -z_view via row 3 = (0,0,-1,0)).
    /// </summary>
    private static double[] Perspective(double width, double height)
    {
        double fovy = 70.0 * Math.PI / 180.0;
        double aspect = width / height;
        double near = 0.1;
        double far = 1000.0;

        double f = 1.0 / Math.Tan(fovy / 2.0);
        double nf = 1.0 / (near - far);

        double[] output = new double[16];
        output[0] = f / aspect;
        output[5] = f;
        output[10] = (far + near) * nf;
        output[11] = -1;
        output[14] = (2 * far * near) * nf;
        return output;
    }

    /// <summary>
    /// Projects a view-space point through a column-major clip matrix (Mat4d.Perspective
    /// layout) to raster pixel coordinates, matching OpenGL's NDC-to-viewport mapping.
    /// </summary>
    private static (double X, double Y) ProjectToPixel(double[] m, double x, double y, double z, double width, double height)
    {
        double clipX = m[0] * x + m[4] * y + m[8] * z + m[12];
        double clipY = m[1] * x + m[5] * y + m[9] * z + m[13];
        double clipW = m[3] * x + m[7] * y + m[11] * z + m[15];

        double ndcX = clipX / clipW;
        double ndcY = clipY / clipW;

        double pixelX = (ndcX * 0.5 + 0.5) * width;
        double pixelY = (ndcY * 0.5 + 0.5) * height;
        return (pixelX, pixelY);
    }

    // --- Motion vector adapters -----------------------------------------------

    [Fact]
    public void FsrAdapterLeavesRenderPixelVectorUnchanged()
    {
        (float x, float y) = OptimumTemporalMath.AdaptMotionVector(1f, 0f, 1920, 1080, OptimumTemporalMath.MotionVectorAdapter.Fsr);
        Assert.Equal(1f, x);
        Assert.Equal(0f, y);
    }

    [Fact]
    public void XessAdapterLeavesRenderPixelVectorUnchanged()
    {
        (float x, float y) = OptimumTemporalMath.AdaptMotionVector(0f, 1f, 1920, 1080, OptimumTemporalMath.MotionVectorAdapter.Xess);
        Assert.Equal(0f, x);
        Assert.Equal(1f, y);
    }

    [Fact]
    public void DlssAdapterNormalizesByRenderTargetSize()
    {
        (float x, float y) = OptimumTemporalMath.AdaptMotionVector(1f, 1f, 1920, 1080, OptimumTemporalMath.MotionVectorAdapter.Dlss);
        Assert.Equal(1f / 1920f, x);
        Assert.Equal(1f / 1080f, y);
    }

    [Theory]
    [InlineData(OptimumTemporalMath.MotionVectorAdapter.Fsr)]
    [InlineData(OptimumTemporalMath.MotionVectorAdapter.Dlss)]
    [InlineData(OptimumTemporalMath.MotionVectorAdapter.Xess)]
    public void OnePixelDisplacementRoundTripsThroughEachAdapter(OptimumTemporalMath.MotionVectorAdapter adapter)
    {
        const int width = 1920;
        const int height = 1080;

        // A one render-pixel displacement, stored as previousPixel - currentPixel.
        float storedX = 1f;
        float storedY = 1f;

        (float adaptedX, float adaptedY) = OptimumTemporalMath.AdaptMotionVector(storedX, storedY, width, height, adapter);

        // Round trip back to render pixels using each adapter's own scale.
        float scaleX = adapter == OptimumTemporalMath.MotionVectorAdapter.Dlss ? width : 1;
        float scaleY = adapter == OptimumTemporalMath.MotionVectorAdapter.Dlss ? height : 1;

        Assert.Equal(storedX, adaptedX * scaleX, 5);
        Assert.Equal(storedY, adaptedY * scaleY, 5);
    }
}
