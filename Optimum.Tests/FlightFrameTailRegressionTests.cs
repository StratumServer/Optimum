using System;
using System.Reflection;
using Cairo;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

public sealed class FlightFrameTailRegressionTests
{
    private class ProxyDispatcher : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Handler = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod == null ? null : Handler(targetMethod, args);
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ProxyDispatcher>();
        ((ProxyDispatcher)(object)proxy).Handler = handler;
        return proxy;
    }

    [Theory]
    [InlineData(256)]
    [InlineData(1024)]
    [InlineData(2048)]
    public void MeshDataRecycler_And_Bridge_RetainMeshesAboveMinimumByteThreshold(int verticesMax)
    {
        long elapsedMs = 1000L;
        var world = CreateProxy<IClientWorldAccessor>((method, _) =>
            method.Name == "get_ElapsedMilliseconds" ? elapsedMs : null);
        var recycler = new MeshDataRecycler(world);
        var bridgeRecycler = new MeshDataRecycler(world);

        try
        {
            var direct = new MeshData(verticesMax) { Recyclable = true };
            var bridged = new MeshData(verticesMax) { Recyclable = true };

            recycler.Recycle(direct);
            OptimumMeshRecyclerBridge.Recycle(bridgeRecycler, bridged);

            recycler.DoRecycling();
            OptimumMeshRecyclerBridge.DoRecycling(bridgeRecycler);

            MeshData reusedDirect = recycler.GetOrCreateMesh(verticesMax);
            MeshData reusedBridge = OptimumMeshRecyclerBridge.GetOrCreateMesh(bridgeRecycler, verticesMax);

            Assert.Same(direct, reusedDirect);
            Assert.Same(bridged, reusedBridge);
        }
        finally
        {
            recycler.Dispose();
            OptimumMeshRecyclerBridge.Dispose(bridgeRecycler);
        }
    }

    [Fact]
    public void MeshDataPool_RemoveLocation_RecomputesOuterBoundsOnlyWhenBoundarySphereRemoved()
    {
        var renderApi = CreateProxy<IRenderAPI>((_, _) => null);
        var capi = CreateProxy<ICoreClientAPI>((method, _) =>
            method.Name == "get_Render" ? renderApi : null);
        var pool = MeshDataPool.AllocateNewPool(capi, 512, 512, 16, null);

        var left = new ModelDataPoolLocation { IndicesStart = 0, IndicesEnd = 5, FrustumCullSphere = new Sphere(0f, 10f, 10f, 4f, 4f, 4f) };
        var middle = new ModelDataPoolLocation { IndicesStart = 6, IndicesEnd = 11, FrustumCullSphere = new Sphere(20f, 10f, 10f, 4f, 4f, 4f) };
        var right = new ModelDataPoolLocation { IndicesStart = 12, IndicesEnd = 17, FrustumCullSphere = new Sphere(50f, 10f, 10f, 4f, 4f, 4f) };

        var locs = (System.Collections.Generic.List<ModelDataPoolLocation>)typeof(MeshDataPool)
            .GetField("poolLocations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pool)!;
        locs.Add(left);
        locs.Add(middle);
        locs.Add(right);
        pool.RenderedTriangles = 6;
        pool.RecomputeTightBounds();

        // Removing interior middle sphere keeps tight hull intact without shrinking X1/X2.
        pool.RemoveLocation(middle);
        Assert.Equal(-left.FrustumCullSphere.radius, pool.BoundMinX, 3);
        Assert.Equal(50f + right.FrustumCullSphere.radius, pool.BoundMaxX, 3);

        // Removing outer right sphere must recompute X2 back to left's outer boundary.
        pool.RemoveLocation(right);
        Assert.Equal(left.FrustumCullSphere.radius, pool.BoundMaxX, 3);
    }

    [Fact]
    public void SkiaFontResolver_BorrowTypeface_And_ResolveOptions_ReuseCachedEntries()
    {
        SKTypeface? first = SkiaFontResolver.BorrowTypeface("sans-serif", SKFontStyleWeight.Normal, SKFontStyleSlant.Upright, out SKFontHinting hinting1);
        SKTypeface? second = SkiaFontResolver.BorrowTypeface("sans-serif", SKFontStyleWeight.Normal, SKFontStyleSlant.Upright, out SKFontHinting hinting2);

        Assert.Same(first, second);
        Assert.Equal(hinting1, hinting2);

        SkiaFontResolver.ResolveOptions("sans-serif", SKFontStyleWeight.Normal, SKFontStyleSlant.Upright, out SKFontHinting optHint1, out _, out _);
        SkiaFontResolver.ResolveOptions("sans-serif", SKFontStyleWeight.Normal, SKFontStyleSlant.Upright, out SKFontHinting optHint2, out _, out _);
        Assert.Equal(optHint1, optHint2);
    }

    [Fact]
    public void ShadowFarReuse_FastMotionGate_SkipsDuringFlightAndRearmsWhenSlow()
    {
        var cache = new OptimumShadowFarReuse();
        double[] pv = new double[16];
        pv[0] = 1; pv[5] = 1; pv[10] = 1; pv[15] = 1;

        Assert.True(cache.UpdateMotionAndShouldPrepareReuse(100.0, 120.0, 100.0));
        cache.CommitFreshRender(
            pv, 100.0, 120.0, 100.0, 0, 2, 3840, 2160, 1, 2, 4096, 4096, 70, 0.1,
            270f, 135.0, 286.0, 168.0, 100.0, 120.0, 50.0, false, 120.0, true, true, 64, true, 80, 7);
        Assert.True(cache.IsValid);

        // Slow camera drift keeps reuse enabled and cache valid.
        Assert.True(cache.UpdateMotionAndShouldPrepareReuse(100.1, 120.0, 100.0));
        Assert.True(cache.IsValid);

        // High-speed flight (> 0.25 blocks/frame) skips guard-margin prep and invalidates cache.
        Assert.False(cache.UpdateMotionAndShouldPrepareReuse(101.2, 120.0, 100.0));
        Assert.False(cache.IsValid);

        // Stopping re-arms snapshot prep on the next frame.
        Assert.True(cache.UpdateMotionAndShouldPrepareReuse(101.2, 120.0, 100.0));
    }

    [Fact]
    public void ApiBridge_SetChiselLodDistance_TracksEmptyFastPathAndRemoval()
    {
        var loc = new ModelDataPoolLocation();
        OptimumApiBridge.SetChiselLodDistance(loc, false);
        Assert.False(OptimumApiBridge.IsChiselTracked(loc));

        OptimumApiBridge.SetChiselLodDistance(loc, true);
        Assert.True(OptimumApiBridge.IsChiselTracked(loc));

        OptimumApiBridge.SetChiselLodDistance(loc, false);
        Assert.False(OptimumApiBridge.IsChiselTracked(loc));
    }
}
