using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Vintagestory.API.Config;

/// <summary>
/// Issue #606: Sorts opaque terrain pools and visible draw spans front-to-back relative to the camera
/// before GPU submission so hardware Early-Z / Hi-Z rejects occluded underground and far-cliff
/// fragments before running shadow PCF, fog, and G-buffer writes.
/// Preserves zero heap allocations in steady-state frames via ThreadStatic scratch buffers.
/// </summary>
public static class OptimumMeshPoolSort
{
    private const float InvSqrt3 = 1f / 1.7320508f;
    private const int DimensionYOffset = 32768;

    private sealed class PoolBoundsCache
    {
        public int Count = -1;
        public ModelDataPoolLocation? First;
        public ModelDataPoolLocation? Last;
        public double MinX;
        public double MinY;
        public double MinZ;
        public double MaxX;
        public double MaxY;
        public double MaxZ;
    }

    private readonly struct OpaquePassScope : IDisposable
    {
        private readonly bool previous;

        public OpaquePassScope(bool previous)
        {
            this.previous = previous;
        }

        public void Dispose()
        {
            t_opaquePassActive = previous;
        }
    }

    private static readonly ConditionalWeakTable<MeshDataPool, PoolBoundsCache> PoolBounds = new();

    [ThreadStatic] private static bool t_opaquePassActive;
    [ThreadStatic] private static bool t_hasCameraPos;
    [ThreadStatic] private static double t_camX;
    [ThreadStatic] private static double t_camY;
    [ThreadStatic] private static double t_camZ;

    [ThreadStatic] private static ulong[]? t_poolSortKeys;
    [ThreadStatic] private static int[]? t_sortedPoolIndices;

    [ThreadStatic] private static float[]? t_spanDistSq;
    [ThreadStatic] private static ulong[]? t_spanSortKeys;
    [ThreadStatic] private static int[]? t_spanStartsScratch;
    [ThreadStatic] private static int[]? t_spanSizesScratch;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pools")]
    private static extern ref List<MeshDataPool> GetManagerPools(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "capi")]
    private static extern ref ICoreClientAPI GetManagerCapi(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "frustumCuller")]
    private static extern ref FrustumCulling GetManagerCuller(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "masterPool")]
    private static extern ref MeshDataPoolMasterManager GetManagerMaster(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "tmp")]
    private static extern ref Vec3f GetManagerTmp(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolOrigin")]
    private static extern ref Vec3i GetPoolOrigin(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dimensionId")]
    private static extern ref int GetPoolDimensionId(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolLocations")]
    private static extern ref List<ModelDataPoolLocation> GetPoolLocations(MeshDataPool pool);

    public static bool IsOpaqueEarlyZSortActive =>
        t_opaquePassActive && OptimumConfig.FrontToBackChunkSortEnabled;

    public static void SetOpaqueEarlyZPass(bool active)
    {
        t_opaquePassActive = active;
    }

    public static IDisposable EnterOpaquePassScope()
    {
        bool previous = t_opaquePassActive;
        t_opaquePassActive = true;
        return new OpaquePassScope(previous);
    }

    public static void SetRenderCameraPosition(Vec3d? playerpos)
    {
        if (playerpos != null)
        {
            t_camX = playerpos.X;
            t_camY = playerpos.Y;
            t_camZ = playerpos.Z;
            t_hasCameraPos = true;
        }
        else
        {
            t_hasCameraPos = false;
        }
    }

    public static void ClearRenderCameraPosition()
    {
        t_hasCameraPos = false;
    }

    /// <summary>
    /// Resolves current camera coordinates when front-to-back opaque span sorting is active.
    /// Prefers the exact render camera vector and falls back to <paramref name="culler"/>.playerPos.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetCameraPosition(
        FrustumCulling? culler,
        out float camX,
        out float camY,
        out float camZ)
    {
        if (!IsOpaqueEarlyZSortActive)
        {
            camX = 0f;
            camY = 0f;
            camZ = 0f;
            return false;
        }

        if (t_hasCameraPos)
        {
            camX = (float)t_camX;
            camY = (float)t_camY;
            camZ = (float)t_camZ;
            return true;
        }

        if (culler != null)
        {
            BlockPos? pos = OptimumFrustumCullSimd.GetPlayerPos(culler);
            if (pos != null)
            {
                camX = pos.X;
                camY = pos.Y;
                camZ = pos.Z;
                return true;
            }
        }

        camX = 0f;
        camY = 0f;
        camZ = 0f;
        return false;
    }

    /// <summary>
    /// Ensures a reusable ThreadStatic array for recording squared span distances during culling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float[] EnsureSpanDistanceScratch(int minCapacity)
    {
        float[]? arr = t_spanDistSq;
        if (arr == null || arr.Length < minCapacity)
        {
            int nextSize = Math.Max(minCapacity, arr == null ? 256 : arr.Length * 2);
            arr = new float[nextSize];
            t_spanDistSq = arr;
        }
        return arr;
    }

    /// <summary>
    /// Computes front-to-back pool indices for a pool list without mutating <paramref name="pools"/>.
    /// Records the camera position for inner span sorting and increments diagnostics.
    /// Returns <see langword="null"/> when sorting is unnecessary (<paramref name="count"/> &lt;= 1) or disabled.
    /// </summary>
    public static int[]? GetSortedPoolIndices(List<MeshDataPool> pools, int count, Vec3d? playerpos)
    {
        SetRenderCameraPosition(playerpos);
        if (count <= 1 || playerpos == null || !IsOpaqueEarlyZSortActive)
        {
            return null;
        }

        ulong[] keys = EnsurePoolKeyScratch(count);
        int[] indices = EnsurePoolIndexScratch(count);
        double camX = playerpos.X;
        double camY = playerpos.Y;
        double camZ = playerpos.Z;

        for (int i = 0; i < count; i++)
        {
            MeshDataPool? pool = pools[i];
            float distSq;
            if (pool == null || GetPoolDimensionId(pool) == 1)
            {
                distSq = float.MaxValue;
            }
            else
            {
                distSq = ComputePoolNearestDistSq(pool, camX, camY, camZ);
            }

            keys[i] = ((ulong)SingleToSortableUInt(distSq) << 32) | (uint)i;
        }

        SortKeys(keys, count);
        for (int i = 0; i < count; i++)
        {
            indices[i] = (int)(uint)keys[i];
        }

        OptimumDiagnostics.RecordChunkDrawSort(count, 0);
        return indices;
    }

    /// <summary>
    /// Sorts visible MultiDraw span arrays in place front-to-back using squared distances in <paramref name="distSq"/>.
    /// </summary>
    public static void SortVisibleSpans(
        int[] indicesStartsByte,
        int[] indicesSizes,
        float[] distSq,
        int groupCount)
    {
        if (groupCount <= 1)
        {
            return;
        }

        ulong[] keys = EnsureSpanKeyScratch(groupCount);
        int[] startsScratch = EnsureSpanStartsScratch(groupCount);
        int[] sizesScratch = EnsureSpanSizesScratch(groupCount);

        for (int i = 0; i < groupCount; i++)
        {
            keys[i] = ((ulong)SingleToSortableUInt(distSq[i]) << 32) | (uint)i;
            startsScratch[i] = indicesStartsByte[i * 2];
            sizesScratch[i] = indicesSizes[i];
        }

        SortKeys(keys, groupCount);

        for (int i = 0; i < groupCount; i++)
        {
            int src = (int)(uint)keys[i];
            indicesStartsByte[i * 2] = startsScratch[src];
            indicesStartsByte[i * 2 + 1] = 0;
            indicesSizes[i] = sizesScratch[src];
        }

        OptimumDiagnostics.RecordChunkDrawSort(0, groupCount);
    }

    /// <summary>
    /// Vanilla API IL-patched entry point for <see cref="MeshDataPoolManager.Render"/>.
    /// Returns <see langword="false"/> to execute untouched vanilla behavior for non-opaque/shadow passes.
    /// </summary>
    public static bool TryRenderSorted(
        MeshDataPoolManager manager,
        Vec3d playerpos,
        string originUniformName,
        EnumFrustumCullMode frustumCullMode)
    {
        if (frustumCullMode != EnumFrustumCullMode.CullNormal || !IsOpaqueEarlyZSortActive)
        {
            return false;
        }

        List<MeshDataPool> pools = GetManagerPools(manager);
        int count = pools.Count;
        if (count == 0)
        {
            OptimumDiagnostics.RecordChunkRenderPass(0, 0);
            return true;
        }

        ICoreClientAPI capi = GetManagerCapi(manager);
        FrustumCulling frustumCuller = GetManagerCuller(manager);
        MeshDataPoolMasterManager masterPool = GetManagerMaster(manager);
        Vec3f tmp = GetManagerTmp(manager);

        int[]? sortedOrder = GetSortedPoolIndices(pools, count, playerpos);
        int poolsRendered = 0;
        int poolsCulled = 0;

        try
        {
            for (int k = 0; k < count; k++)
            {
                int i = sortedOrder != null ? sortedOrder[k] : k;
                MeshDataPool pool = pools[i];
                if (pool == null)
                {
                    continue;
                }

                int dimId = GetPoolDimensionId(pool);
                Vec3i poolOrigin = GetPoolOrigin(pool);
                if (dimId == 1)
                {
                    bool revertModelview = false;
                    bool revertMvp = false;
                    bool revertTransparency = false;

                    if (!capi.World.TryGetMiniDimension(poolOrigin, out IMiniDimension dimension) || dimension.selectionTrackingOriginalPos == null)
                    {
                        continue;
                    }

                    pool.SetFullyVisible();
                    if (pool.indicesGroupsCount == 0)
                    {
                        continue;
                    }

                    FastVec3d renderOffset = dimension.GetRenderOffset(masterPool.currentDt);
                    IShaderProgram currShader = capi.Render.CurrentActiveShader;
                    if (currShader.HasUniform("modelViewMatrix"))
                    {
                        currShader.UniformMatrix("modelViewMatrix", dimension.GetRenderTransformMatrix(masterPool.currentModelViewMatrix, playerpos));
                        revertModelview = true;
                        if (currShader.HasUniform("forcedTransparency"))
                        {
                            currShader.Uniform("forcedTransparency", capi.Settings.Float["previewTransparency"]);
                            revertTransparency = true;
                        }
                    }
                    else if (currShader.HasUniform("mvpMatrix"))
                    {
                        currShader.UniformMatrix("mvpMatrix", dimension.GetRenderTransformMatrix(masterPool.shadowMVPMatrix, playerpos));
                        revertMvp = true;
                    }

                    currShader.Uniform(originUniformName, tmp.Set(
                        (float)(poolOrigin.X + renderOffset.X - playerpos.X),
                        (float)(poolOrigin.Y + renderOffset.Y - playerpos.Y),
                        (float)(poolOrigin.Z + renderOffset.Z - playerpos.Z)));

                    try
                    {
                        poolsRendered++;
                        pool.RenderMesh(capi.Render);
                    }
                    finally
                    {
                        if (revertModelview)
                        {
                            capi.Render.CurrentActiveShader.UniformMatrix("modelViewMatrix", masterPool.currentModelViewMatrix);
                        }
                        if (revertMvp)
                        {
                            capi.Render.CurrentActiveShader.UniformMatrix("mvpMatrix", masterPool.shadowMVPMatrix);
                        }
                        if (revertTransparency)
                        {
                            capi.Render.CurrentActiveShader.Uniform("forcedTransparency", 0f);
                        }
                    }
                }
                else
                {
                    long cullStart = Stopwatch.GetTimestamp();
                    pool.FrustumCull(frustumCuller, frustumCullMode);
                    OptimumDiagnostics.RecordChunkFrustumCullTicks(Stopwatch.GetTimestamp() - cullStart);

                    if (pool.indicesGroupsCount == 0)
                    {
                        poolsCulled++;
                        continue;
                    }

                    capi.Render.CurrentActiveShader.Uniform(originUniformName, tmp.Set(
                        (float)(poolOrigin.X - playerpos.X),
                        (float)(poolOrigin.Y + dimId * DimensionYOffset - playerpos.Y),
                        (float)(poolOrigin.Z - playerpos.Z)));

                    poolsRendered++;
                    pool.RenderMesh(capi.Render);
                }
            }
        }
        finally
        {
            ClearRenderCameraPosition();
        }

        OptimumDiagnostics.RecordChunkRenderPass(poolsRendered, poolsCulled);
        return true;
    }

    /// <summary>
    /// Vanilla API IL-patched entry point for <see cref="MeshDataPool.FrustumCull"/>.
    /// Returns <see langword="false"/> for non-opaque or non-<see cref="EnumFrustumCullMode.CullNormal"/> calls.
    /// </summary>
    public static bool TryFrustumCullSortedOpaque(
        MeshDataPool pool,
        FrustumCulling? frustumCuller,
        EnumFrustumCullMode frustumCullMode)
    {
        if (frustumCullMode != EnumFrustumCullMode.CullNormal ||
            !TryGetCameraPosition(frustumCuller, out float camX, out float camY, out float camZ))
        {
            return false;
        }

        pool.indicesGroupsCount = 0;
        pool.RenderedTriangles = 0;
        pool.AllocatedTris = 0;

        List<ModelDataPoolLocation> poolLocations = GetPoolLocations(pool);
        int count = poolLocations.Count;
        if (count == 0)
        {
            return true;
        }

        float[] spanDistSq = EnsureSpanDistanceScratch(count);
        int[] startsByte = pool.indicesStartsByte;
        int[] sizes = pool.indicesSizes;
        int visibleCount = 0;
        int renderedTris = 0;
        int allocatedTris = 0;

        for (int i = 0; i < count; i++)
        {
            ModelDataPoolLocation location = poolLocations[i];
            int size = location.IndicesEnd - location.IndicesStart;
            allocatedTris += size / 3;

            if (location.IsVisible(EnumFrustumCullMode.CullNormal, frustumCuller))
            {
                startsByte[visibleCount * 2] = location.IndicesStart * 4;
                sizes[visibleCount] = size;

                Sphere sphere = location.FrustumCullSphere;
                float dx = sphere.x - camX;
                float dy = sphere.y - camY;
                float dz = sphere.z - camZ;
                spanDistSq[visibleCount] = dx * dx + dy * dy + dz * dz;

                renderedTris += size / 3;
                visibleCount++;
            }
        }

        pool.indicesGroupsCount = visibleCount;
        pool.RenderedTriangles = renderedTris;
        pool.AllocatedTris = allocatedTris;

        if (visibleCount > 1)
        {
            SortVisibleSpans(startsByte, sizes, spanDistSq, visibleCount);
        }

        return true;
    }

    private static float ComputePoolNearestDistSq(MeshDataPool pool, double camX, double camY, double camZ)
    {
        List<ModelDataPoolLocation> locations = GetPoolLocations(pool);
        int n = locations.Count;
        if (n == 0)
        {
            return float.MaxValue;
        }

        var cache = PoolBounds.GetOrCreateValue(pool);
        ModelDataPoolLocation first = locations[0];
        ModelDataPoolLocation last = locations[n - 1];

        if (cache.Count != n || !ReferenceEquals(cache.First, first) || !ReferenceEquals(cache.Last, last))
        {
            RecomputePoolBounds(pool, locations, n, first, last, cache);
        }

        double nx = Math.Clamp(camX, cache.MinX, cache.MaxX) - camX;
        double ny = Math.Clamp(camY, cache.MinY, cache.MaxY) - camY;
        double nz = Math.Clamp(camZ, cache.MinZ, cache.MaxZ) - camZ;
        double distSq = nx * nx + ny * ny + nz * nz;
        float distSqF = (float)distSq;
        return float.IsFinite(distSqF) && distSqF >= 0f ? distSqF : float.MaxValue;
    }

    private static void RecomputePoolBounds(
        MeshDataPool pool,
        List<ModelDataPoolLocation> locations,
        int n,
        ModelDataPoolLocation first,
        ModelDataPoolLocation last,
        PoolBoundsCache cache)
    {
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        double maxZ = double.NegativeInfinity;
        bool hasAny = false;

        for (int i = 0; i < n; i++)
        {
            Sphere s = locations[i].FrustumCullSphere;

            double rx = s.radius * InvSqrt3;
            double ry = s.radiusY * InvSqrt3;
            double rz = s.radiusZ * InvSqrt3;

            double loX = s.x - rx;
            double hiX = s.x + rx;
            double loY = s.y - ry;
            double hiY = s.y + ry;
            double loZ = s.z - rz;
            double hiZ = s.z + rz;

            if (loX < minX) minX = loX;
            if (hiX > maxX) maxX = hiX;
            if (loY < minY) minY = loY;
            if (hiY > maxY) maxY = hiY;
            if (loZ < minZ) minZ = loZ;
            if (hiZ > maxZ) maxZ = hiZ;
            hasAny = true;
        }

        if (!hasAny)
        {
            Vec3i? origin = GetPoolOrigin(pool);
            int dim = GetPoolDimensionId(pool);
            if (origin != null)
            {
                minX = maxX = origin.X;
                minY = maxY = origin.Y + (double)dim * DimensionYOffset;
                minZ = maxZ = origin.Z;
            }
            else
            {
                minX = minY = minZ = maxX = maxY = maxZ = 0d;
            }
        }

        cache.Count = n;
        cache.First = first;
        cache.Last = last;
        cache.MinX = minX;
        cache.MinY = minY;
        cache.MinZ = minZ;
        cache.MaxX = maxX;
        cache.MaxY = maxY;
        cache.MaxZ = maxZ;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SingleToSortableUInt(float distSq)
    {
        if (!float.IsFinite(distSq) || distSq < 0f)
        {
            return uint.MaxValue;
        }
        return (uint)BitConverter.SingleToInt32Bits(distSq);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SortKeys(ulong[] keys, int count)
    {
        if (count <= 32)
        {
            for (int i = 1; i < count; i++)
            {
                ulong k = keys[i];
                int j = i - 1;
                while (j >= 0 && keys[j] > k)
                {
                    keys[j + 1] = keys[j];
                    j--;
                }
                keys[j + 1] = k;
            }
            return;
        }

        Array.Sort(keys, 0, count);
    }

    private static ulong[] EnsurePoolKeyScratch(int n)
    {
        if (t_poolSortKeys == null || t_poolSortKeys.Length < n)
        {
            t_poolSortKeys = new ulong[Math.Max(n, 32)];
        }
        return t_poolSortKeys;
    }

    private static int[] EnsurePoolIndexScratch(int n)
    {
        if (t_sortedPoolIndices == null || t_sortedPoolIndices.Length < n)
        {
            t_sortedPoolIndices = new int[Math.Max(n, 32)];
        }
        return t_sortedPoolIndices;
    }

    private static ulong[] EnsureSpanKeyScratch(int n)
    {
        if (t_spanSortKeys == null || t_spanSortKeys.Length < n)
        {
            t_spanSortKeys = new ulong[Math.Max(n, 256)];
        }
        return t_spanSortKeys;
    }

    private static int[] EnsureSpanStartsScratch(int n)
    {
        if (t_spanStartsScratch == null || t_spanStartsScratch.Length < n)
        {
            t_spanStartsScratch = new int[Math.Max(n, 256)];
        }
        return t_spanStartsScratch;
    }

    private static int[] EnsureSpanSizesScratch(int n)
    {
        if (t_spanSizesScratch == null || t_spanSizesScratch.Length < n)
        {
            t_spanSizesScratch = new int[Math.Max(n, 256)];
        }
        return t_spanSizesScratch;
    }
}
