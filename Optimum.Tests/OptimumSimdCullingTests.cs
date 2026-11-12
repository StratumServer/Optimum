using System;
using System.Runtime.Intrinsics;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

[Collection("OptimumConfig")]
public class OptimumSimdCullingTests
{
    [Fact]
    public void SimdCullingHardwareDetectionReflectsVectorSupport()
    {
        Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.SimdCullingSupported);
        Assert.Equal(OptimumConfig.SimdCullingEnabled && Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);
    }

    [Fact]
    public void SimdCullingConfigToggleDisablesEffectiveState()
    {
        bool original = OptimumConfig.SimdCullingEnabled;
        try
        {
            OptimumConfig.SimdCullingEnabled = false;
            Assert.False(OptimumConfig.EffectiveSimdCulling);

            OptimumConfig.SimdCullingEnabled = true;
            Assert.Equal(Vector128.IsHardwareAccelerated, OptimumConfig.EffectiveSimdCulling);
        }
        finally
        {
            OptimumConfig.SimdCullingEnabled = original;
        }
    }

    [Fact]
    public void SimdCullingCountersIncrementAndReset()
    {
        OptimumDiagnostics.ResetSimdFrustumCounters();
        Assert.Equal(0, OptimumDiagnostics.SimdFrustumTests);
        Assert.Equal(0, OptimumDiagnostics.SimdFrustumCulled);

        OptimumDiagnostics.RecordSimdFrustumTest(culled: false);
        Assert.Equal(1, OptimumDiagnostics.SimdFrustumTests);
        Assert.Equal(0, OptimumDiagnostics.SimdFrustumCulled);

        OptimumDiagnostics.RecordSimdFrustumTest(culled: true);
        Assert.Equal(2, OptimumDiagnostics.SimdFrustumTests);
        Assert.Equal(1, OptimumDiagnostics.SimdFrustumCulled);

        OptimumDiagnostics.ResetSimdFrustumCounters();
        Assert.Equal(0, OptimumDiagnostics.SimdFrustumTests);
        Assert.Equal(0, OptimumDiagnostics.SimdFrustumCulled);
    }

    [Fact]
    public void ChunkRenderSummaryIncludesSimdCullStats()
    {
        OptimumDiagnostics.ResetSimdFrustumCounters();
        OptimumDiagnostics.RecordSimdFrustumTest(culled: true);
        OptimumDiagnostics.RecordSimdFrustumTest(culled: false);

        string summary = OptimumDiagnostics.GetChunkRenderSummary();
        Assert.Contains("simdTests/frame=", summary);
        Assert.Contains("simdCulled/frame=", summary);
    }

    [Fact]
    public void SimdCullingMatchesScalarInFrustumAndRangeAcrossRandomSpheres()
    {
        var culler = new FrustumCulling();
        var playerPos = new BlockPos(1000, 150, 1000);
        culler.UpdateViewDistance(256);
        culler.lod0BiasSq = 32 * 32;
        culler.lod2BiasSq = 128 * 128;

        double[] proj = Mat4d.Create();
        Mat4d.Perspective(proj, 70f * GameMath.DEG2RAD, 1.777f, 0.1f, 500f);

        double[] cam = Mat4d.Create();
        Mat4d.LookAt(cam, [1000, 150, 1000], [1000, 150, 1100], [0, 1, 0]);

        culler.CalcFrustumEquations(playerPos, proj, cam);

        var rng = new Random(12345);
        int totalTests = 500;

        for (int i = 0; i < totalTests; i++)
        {
            float sx = (float)(1000 + (rng.NextDouble() - 0.5) * 600);
            float sy = (float)(150 + (rng.NextDouble() - 0.5) * 100);
            float sz = (float)(1000 + (rng.NextDouble() - 0.1) * 600);
            var sphere = new Sphere(sx, sy, sz, 16f * Sphere.sqrt3half, 16f * Sphere.sqrt3half, 16f * Sphere.sqrt3half);

            for (int lod = 0; lod <= 3; lod++)
            {
                bool scalarResult = culler.InFrustumAndRange(sphere, nowVisible: false, lodLevel: lod);
                bool simdResult = OptimumFrustumCullSimd.InFrustumAndRange(culler, sphere, nowVisible: false, lodLevel: lod, location: null!);

                Assert.True(scalarResult == simdResult,
                    $"Mismatch at test {i}, LOD {lod}: scalar={scalarResult}, simd={simdResult}, sphere=({sx}, {sy}, {sz})");
            }
        }
    }

    [Fact]
    public void SimdCullingMatchesScalarInFrustum()
    {
        var culler = new FrustumCulling();
        var playerPos = new BlockPos(5000, 100, 5000);

        double[] proj = Mat4d.Create();
        Mat4d.Perspective(proj, 75f * GameMath.DEG2RAD, 1.6f, 0.5f, 400f);

        double[] cam = Mat4d.Create();
        Mat4d.LookAt(cam, [5000, 100, 5000], [5000, 100, 5100], [0, 1, 0]);

        culler.CalcFrustumEquations(playerPos, proj, cam);

        var rng = new Random(67890);
        for (int i = 0; i < 300; i++)
        {
            float sx = (float)(5000 + (rng.NextDouble() - 0.5) * 500);
            float sy = (float)(100 + (rng.NextDouble() - 0.5) * 100);
            float sz = (float)(5000 + (rng.NextDouble() - 0.2) * 500);
            var sphere = new Sphere(sx, sy, sz, 16f * Sphere.sqrt3half, 16f * Sphere.sqrt3half, 16f * Sphere.sqrt3half);

            bool scalarInFrustum = culler.InFrustum(sphere);
            bool simdInFrustum = OptimumFrustumCullSimd.InFrustum(culler, sphere);

            Assert.True(scalarInFrustum == simdInFrustum,
                $"InFrustum mismatch at {i}: scalar={scalarInFrustum}, simd={simdInFrustum}");
        }
    }

    [Fact]
    public void UploadVisibilityUsesCurrentFrustumWhenCameraRotatesAtTheSamePosition()
    {
        bool original = OptimumConfig.SimdCullingEnabled;
        try
        {
            OptimumConfig.SimdCullingEnabled = true;
            var culler = new FrustumCulling();
            var origin = new BlockPos(0, 0, 0);
            double[] projection = Mat4d.Create();
            Mat4d.Perspective(projection, 70f * GameMath.DEG2RAD, 1.6f, 0.1f, 500f);
            double[] view = Mat4d.Create();
            Mat4d.LookAt(view, [0, 0, 0], [0, 0, 100], [0, 1, 0]);
            culler.CalcFrustumEquations(origin, projection, view);

            var towardFirstDirection = new Sphere(0, 0, 20, 0.5f, 0.5f, 0.5f);
            Assert.True(OptimumFrustumCullSimd.InFrustum(culler, towardFirstDirection));
            var frustum = OptimumFrustumCullSimd.GetFrustum(culler);
            var context = OptimumFrustumCullSimd.GetOrCreateContext(culler, frustum);
            double[] firstD = new double[frustum.Length];
            double[] firstNormalX = new double[frustum.Length];
            double[] firstNormalY = new double[frustum.Length];
            double[] firstNormalZ = new double[frustum.Length];
            for (int i = 0; i < frustum.Length; i++)
            {
                firstD[i] = frustum[i].D;
                firstNormalX[i] = frustum[i].normalX;
                firstNormalY[i] = frustum[i].normalY;
                firstNormalZ[i] = frustum[i].normalZ;
            }

            Mat4d.LookAt(view, [0, 0, 0], [100, 0, 0], [0, 1, 0]);
            culler.CalcFrustumEquations(origin, projection, view);
            bool normalsChanged = false;
            for (int i = 0; i < frustum.Length; i++)
            {
                Assert.Equal(firstD[i], frustum[i].D);
                normalsChanged |= firstNormalX[i] != frustum[i].normalX
                    || firstNormalY[i] != frustum[i].normalY
                    || firstNormalZ[i] != frustum[i].normalZ;
            }
            Assert.True(normalsChanged, "The fixture must rotate frustum normals while preserving every plane constant.");
            Assert.False(context.IsUpToDate(frustum), "The cached SIMD planes must be invalidated when normals change, even if plane constants do not.");

            OptimumDiagnostics.ResetSimdFrustumCounters();
            Assert.False(OptimumFrustumCullSimd.InFrustumForTelemetry(culler, towardFirstDirection));
            Assert.Equal(0, OptimumDiagnostics.SimdFrustumTests);
            Assert.True(context.IsUpToDate(frustum), "Telemetry culling must refresh the warmed SIMD context without recording renderer diagnostics.");
        }
        finally
        {
            OptimumConfig.SimdCullingEnabled = original;
        }
    }

    [Fact]
    public void SimdCullingMatchesScalarInFrustumShadowPass()
    {
        var culler = new FrustumCulling();
        var playerPos = new BlockPos(2000, 80, 2000);
        culler.shadowRangeX = 80;
        culler.shadowRangeZ = 80;

        double[] proj = Mat4d.Create();
        Mat4d.Ortho(proj, -80, 80, -80, 80, -100, 100);

        double[] cam = Mat4d.Create();
        Mat4d.LookAt(cam, [2000, 80, 2000], [2000, 0, 2000], [0, 0, 1]);

        culler.CalcFrustumEquations(playerPos, proj, cam);

        var rng = new Random(112233);
        for (int i = 0; i < 300; i++)
        {
            float sx = (float)(2000 + (rng.NextDouble() - 0.5) * 200);
            float sy = (float)(80 + (rng.NextDouble() - 0.5) * 100);
            float sz = (float)(2000 + (rng.NextDouble() - 0.5) * 200);
            var sphere = new Sphere(sx, sy, sz, 16f * Sphere.sqrt3half, 16f * Sphere.sqrt3half, 16f * Sphere.sqrt3half);

            bool scalarShadow = culler.InFrustumShadowPass(sphere);
            bool simdShadow = OptimumFrustumCullSimd.InFrustumShadowPass(culler, sphere);

            Assert.True(scalarShadow == simdShadow,
                $"Shadow mismatch at {i}: scalar={scalarShadow}, simd={simdShadow}");
        }
    }

    [Fact]
    public void SimdCullingMatchesScalarSphereInFrustumAcrossRandomSpheres()
    {
        var culler = new FrustumCulling();
        var playerPos = new BlockPos(1000, 150, 1000);

        double[] proj = Mat4d.Create();
        Mat4d.Perspective(proj, 70f * GameMath.DEG2RAD, 1.777f, 0.1f, 500f);

        double[] cam = Mat4d.Create();
        Mat4d.LookAt(cam, [1000, 150, 1000], [1000, 150, 1100], [0, 1, 0]);

        culler.CalcFrustumEquations(playerPos, proj, cam);

        var rng = new Random(445566);
        for (int i = 0; i < 500; i++)
        {
            double sx = 1000 + (rng.NextDouble() - 0.5) * 600;
            double sy = 150 + (rng.NextDouble() - 0.5) * 100;
            double sz = 1000 + (rng.NextDouble() - 0.1) * 600;
            double radius = 1.0 + rng.NextDouble() * 30.0;

            bool scalarSphere = culler.SphereInFrustum(sx, sy, sz, radius);
            bool simdSphere = OptimumFrustumCullSimd.SphereInFrustum(culler, sx, sy, sz, radius);

            Assert.True(scalarSphere == simdSphere,
                $"SphereInFrustum mismatch at {i}: scalar={scalarSphere}, simd={simdSphere}, point=({sx}, {sy}, {sz}), radius={radius}");
        }
    }
}
