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

    private sealed class PoolCullState
    {
        public int Count = -1;
        public int IndicesPos = -1;
        public int VerticesPos = -1;
        public int UsedVertices = -1;
        public int TotalAllocatedTris;
        public ModelDataPoolLocation? First;
        public ModelDataPoolLocation? Last;
        public ModelDataPoolLocation? Mid;
        public double MinX;
        public double MinY;
        public double MinZ;
        public double MaxX;
        public double MaxY;
        public double MaxZ;
        public PoolCullData? CullData;
        public readonly List<ModelDataPoolLocation> Flagged = new();
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

    private static readonly ConditionalWeakTable<MeshDataPool, PoolCullState> PoolStates = new();

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
    [ThreadStatic] private static int[]? t_visibleIndicesScratch;

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
        SortSpansCore(indicesStartsByte, indicesSizes, distSq, groupCount, coalesceAdjacent: false);
    }

    /// <summary>
    /// Sorts visible MultiDraw span arrays front-to-back by <paramref name="distSq"/> and
    /// merges physically contiguous index spans (<c>prevStartBytes + prevSize * 4 == curStartBytes</c>)
    /// to cut GPU MultiDraw packet overhead without changing draw order or triangle count.
    /// </summary>
    public static int SortAndCoalesceVisibleSpans(
        int[] indicesStartsByte,
        int[] indicesSizes,
        float[] distSq,
        int groupCount)
    {
        return SortSpansCore(indicesStartsByte, indicesSizes, distSq, groupCount, coalesceAdjacent: true);
    }

    private static int SortSpansCore(
        int[] indicesStartsByte,
        int[] indicesSizes,
        float[] distSq,
        int groupCount,
        bool coalesceAdjacent)
    {
        if (groupCount <= 1)
        {
            return groupCount;
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

        int outCount = 0;
        if (coalesceAdjacent)
        {
            ulong prevDistBits = 0;
            for (int i = 0; i < groupCount; i++)
            {
                ulong key = keys[i];
                ulong distBits = key >> 32;
                int src = (int)(uint)key;
                int startByte = startsScratch[src];
                int size = sizesScratch[src];
                if (size <= 0)
                {
                    continue;
                }
                if (outCount > 0 && prevDistBits == distBits && indicesStartsByte[(outCount - 1) * 2] + indicesSizes[outCount - 1] * 4 == startByte)
                {
                    indicesSizes[outCount - 1] += size;
                }
                else
                {
                    indicesStartsByte[outCount * 2] = startByte;
                    indicesStartsByte[outCount * 2 + 1] = 0;
                    indicesSizes[outCount] = size;
                    prevDistBits = distBits;
                    outCount++;
                }
            }
        }
        else
        {
            for (int i = 0; i < groupCount; i++)
            {
                int src = (int)(uint)keys[i];
                indicesStartsByte[i * 2] = startsScratch[src];
                indicesStartsByte[i * 2 + 1] = 0;
                indicesSizes[i] = sizesScratch[src];
            }
            outCount = groupCount;
        }

        OptimumDiagnostics.RecordChunkDrawSort(0, outCount);
        return outCount;
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
        if (frustumCullMode != EnumFrustumCullMode.CullNormal ||
            (!IsOpaqueEarlyZSortActive && !OptimumChunkFaceBuckets.IsOpaqueFaceCullActive))
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
    /// Executes pool-level AABB early-rejection, out-of-line SoA SIMD plane culling
    /// via <see cref="PoolCullData"/>, batched diagnostics, chunk face bucket culling,
    /// and optional front-to-back opaque span sorting while maintaining <c>flagged</c> / <c>FrustumVisible</c>.
    /// </summary>
    public static bool TryFrustumCullSortedOpaque(
        MeshDataPool pool,
        FrustumCulling? frustumCuller,
        EnumFrustumCullMode frustumCullMode)
    {
        if (frustumCuller == null ||
            GetPoolDimensionId(pool) == Dimensions.MiniDimensions ||
            (frustumCullMode != EnumFrustumCullMode.CullNormal &&
             frustumCullMode != EnumFrustumCullMode.CullInstant &&
             frustumCullMode != EnumFrustumCullMode.CullInstantShadowPassNear &&
             frustumCullMode != EnumFrustumCullMode.CullInstantShadowPassFar))
        {
            return false;
        }

        List<ModelDataPoolLocation> poolLocations = GetPoolLocations(pool);
        int count = poolLocations.Count;
        if (count > ushort.MaxValue)
        {
            return false;
        }

        Plane[]? planes = OptimumFrustumCullSimd.GetFrustum(frustumCuller);
        if (planes == null || planes.Length < 6)
        {
            return false;
        }

        pool.indicesGroupsCount = 0;
        pool.RenderedTriangles = 0;

        if (count == 0)
        {
            pool.AllocatedTris = 0;
            if (frustumCullMode == EnumFrustumCullMode.CullNormal && PoolStates.TryGetValue(pool, out var emptyState))
            {
                ResetFlagged(emptyState.Flagged);
            }
            return true;
        }

        PoolCullState state = GetOrRebuildPoolState(pool, poolLocations, count);
        pool.AllocatedTris = state.TotalAllocatedTris;

        List<ModelDataPoolLocation>? flagged = frustumCullMode == EnumFrustumCullMode.CullNormal
            ? ResetFlagged(state.Flagged)
            : null;
        BlockPos? playerPos = OptimumFrustumCullSimd.GetPlayerPos(frustumCuller);
        int planeCount = frustumCullMode == EnumFrustumCullMode.CullNormal ? 5 : 6;

        if (frustumCullMode == EnumFrustumCullMode.CullInstantShadowPassNear ||
            frustumCullMode == EnumFrustumCullMode.CullInstantShadowPassFar)
        {
            if (playerPos != null && !IsBoxInShadowPass(playerPos, frustumCuller.shadowRangeX, frustumCuller.shadowRangeZ, state))
            {
                return true;
            }
        }
        else if (playerPos != null && !IsBoxInRange(playerPos, frustumCuller.ViewDistanceSq, state))
        {
            return true;
        }

        if (!IsBoxInsidePlanes(planes, planeCount, state))
        {
            return true;
        }

        PoolCullData cullData = state.CullData!;
        int candidateCount = cullData.CullPlanesSimd(planes, planeCount, count);
        OptimumDiagnostics.RecordSimdFrustumBatch(count, count - candidateCount);
        ushort[] candidates = cullData.CandidateIndices;

        float camX = 0f, camY = 0f, camZ = 0f;
        bool sortFrontToBack = frustumCullMode == EnumFrustumCullMode.CullNormal &&
            TryGetCameraPosition(frustumCuller, out camX, out camY, out camZ);
        double cullCamX = 0d, cullCamY = 0d, cullCamZ = 0d;
        bool doBucketCull = frustumCullMode == EnumFrustumCullMode.CullNormal &&
            OptimumChunkFaceBuckets.TryGetActiveCullCamera(GetPoolDimensionId(pool), out cullCamX, out cullCamY, out cullCamZ);

        OptimumChunkFaceBuckets.EnsurePoolSpanCapacity(pool, candidateCount > 0 ? candidateCount : 1);
        float[]? spanDistSq = sortFrontToBack ? EnsureSpanDistanceScratch(candidateCount > 0 ? candidateCount : 1) : null;
        int[]? visibleIndices = doBucketCull ? EnsureVisibleIndicesScratch(candidateCount > 0 ? candidateCount : 1) : null;

        int[] startsByte = pool.indicesStartsByte;
        int[] sizes = pool.indicesSizes;
        int visibleBufIndex = ModelDataPoolLocation.VisibleBufIndex;
        int visibleCount = 0;
        int renderedTris = 0;

        switch (frustumCullMode)
        {
            case EnumFrustumCullMode.CullNormal:
                for (int c = 0; c < candidateCount; c++)
                {
                    int idx = candidates[c];
                    ModelDataPoolLocation location = poolLocations[idx];
                    if (location.Hide || !location.CullVisible[visibleBufIndex])
                    {
                        continue;
                    }

                    Sphere sphere = location.FrustumCullSphere;
                    if (OptimumFrustumCullSimd.InRangeAndLodOnly(frustumCuller, sphere, location.FrustumVisible, location.LodLevel, location))
                    {
                        if (doBucketCull)
                        {
                            visibleIndices![visibleCount] = idx;
                            if (sortFrontToBack)
                            {
                                float dx = sphere.x - camX;
                                float dy = sphere.y - camY;
                                float dz = sphere.z - camZ;
                                spanDistSq![visibleCount] = dx * dx + dy * dy + dz * dz;
                            }
                            visibleCount++;
                        }
                        else
                        {
                            int startByte = cullData.IndexStartBytes[idx];
                            int size = cullData.IndexCounts[idx];
                            if (sortFrontToBack)
                            {
                                startsByte[visibleCount * 2] = startByte;
                                sizes[visibleCount] = size;
                                float dx = sphere.x - camX;
                                float dy = sphere.y - camY;
                                float dz = sphere.z - camZ;
                                spanDistSq![visibleCount] = dx * dx + dy * dy + dz * dz;
                                visibleCount++;
                            }
                            else
                            {
                                AppendCoalescedSpan(startsByte, sizes, ref visibleCount, startByte, size);
                            }
                            renderedTris += size / 3;
                        }
                        location.FrustumVisible = true;
                        flagged!.Add(location);
                    }
                }
                break;

            case EnumFrustumCullMode.CullInstant:
                for (int c = 0; c < candidateCount; c++)
                {
                    int idx = candidates[c];
                    ModelDataPoolLocation location = poolLocations[idx];
                    if (location.Hide || !location.CullVisible[visibleBufIndex])
                    {
                        continue;
                    }

                    int size = cullData.IndexCounts[idx];
                    AppendCoalescedSpan(startsByte, sizes, ref visibleCount, cullData.IndexStartBytes[idx], size);
                    renderedTris += size / 3;
                }
                break;

            case EnumFrustumCullMode.CullInstantShadowPassNear:
                double shadowNearX = frustumCuller.shadowRangeX;
                double shadowNearZ = frustumCuller.shadowRangeZ;
                for (int c = 0; c < candidateCount; c++)
                {
                    int idx = candidates[c];
                    ModelDataPoolLocation location = poolLocations[idx];
                    if (location.Hide || !location.CullVisible[visibleBufIndex])
                    {
                        continue;
                    }

                    Sphere sphere = location.FrustumCullSphere;
                    if (playerPos != null &&
                        (Math.Abs(playerPos.X - sphere.x) >= shadowNearX || Math.Abs(playerPos.Z - sphere.z) >= shadowNearZ))
                    {
                        continue;
                    }

                    if (OptimumApiBridge.InFrustumShadowPass(true, frustumCuller, location))
                    {
                        int size = cullData.IndexCounts[idx];
                        AppendCoalescedSpan(startsByte, sizes, ref visibleCount, cullData.IndexStartBytes[idx], size);
                        renderedTris += size / 3;
                    }
                }
                break;

            case EnumFrustumCullMode.CullInstantShadowPassFar:
                double shadowFarX = frustumCuller.shadowRangeX;
                double shadowFarZ = frustumCuller.shadowRangeZ;
                for (int c = 0; c < candidateCount; c++)
                {
                    int idx = candidates[c];
                    ModelDataPoolLocation location = poolLocations[idx];
                    if (location.Hide || !location.CullVisible[visibleBufIndex] || location.LodLevel < 1)
                    {
                        continue;
                    }

                    Sphere sphere = location.FrustumCullSphere;
                    if (playerPos != null &&
                        (Math.Abs(playerPos.X - sphere.x) >= shadowFarX || Math.Abs(playerPos.Z - sphere.z) >= shadowFarZ))
                    {
                        continue;
                    }

                    if (OptimumApiBridge.InFrustumShadowPass(true, frustumCuller, location))
                    {
                        int size = cullData.IndexCounts[idx];
                        AppendCoalescedSpan(startsByte, sizes, ref visibleCount, cullData.IndexStartBytes[idx], size);
                        renderedTris += size / 3;
                    }
                }
                break;
        }

        if (doBucketCull)
        {
            ulong[]? keys = null;
            if (sortFrontToBack && visibleCount > 1)
            {
                keys = EnsureSpanKeyScratch(visibleCount);
                for (int k = 0; k < visibleCount; k++)
                {
                    keys[k] = ((ulong)SingleToSortableUInt(spanDistSq![k]) << 32) | (uint)visibleIndices![k];
                }
                SortKeys(keys, visibleCount);
                for (int k = 0; k < visibleCount; k++)
                {
                    visibleIndices![k] = (int)(uint)keys[k];
                }
                OptimumDiagnostics.RecordChunkDrawSort(0, visibleCount);
            }

            int emittedGroups = 0;
            long keptTrisAcc = 0;
            long culledTrisAcc = 0;
            uint prevDistBits = 0;

            for (int k = 0; k < visibleCount; k++)
            {
                ModelDataPoolLocation location = poolLocations[visibleIndices![k]];
                uint curDistBits = keys != null ? (uint)(keys[k] >> 32) : 0u;
                int prevEmitted = emittedGroups;

                OptimumChunkFaceBuckets.EmitLocationSpans(
                    pool,
                    location,
                    true,
                    cullCamX,
                    cullCamY,
                    cullCamZ,
                    ref emittedGroups,
                    ref renderedTris,
                    ref keptTrisAcc,
                    ref culledTrisAcc);

                if (emittedGroups > prevEmitted && prevEmitted > 0 && (!sortFrontToBack || prevDistBits == curDistBits))
                {
                    int[] bStarts = pool.indicesStartsByte;
                    int[] bSizes = pool.indicesSizes;
                    if (bStarts[(prevEmitted - 1) * 2] + bSizes[prevEmitted - 1] * 4 == bStarts[prevEmitted * 2])
                    {
                        bSizes[prevEmitted - 1] += bSizes[prevEmitted];
                        for (int s = prevEmitted + 1; s < emittedGroups; s++)
                        {
                            bStarts[(s - 1) * 2] = bStarts[s * 2];
                            bStarts[(s - 1) * 2 + 1] = bStarts[s * 2 + 1];
                            bSizes[s - 1] = bSizes[s];
                        }
                        emittedGroups--;
                    }
                }

                if (emittedGroups > prevEmitted)
                {
                    prevDistBits = curDistBits;
                }
            }

            pool.indicesGroupsCount = emittedGroups;
            pool.RenderedTriangles = renderedTris;

            if (keptTrisAcc > 0 || culledTrisAcc > 0)
            {
                OptimumDiagnostics.RecordChunkFaceBucketCull(keptTrisAcc, culledTrisAcc, emittedGroups);
            }

            return true;
        }

        if (sortFrontToBack && visibleCount > 1)
        {
            visibleCount = SortAndCoalesceVisibleSpans(startsByte, sizes, spanDistSq!, visibleCount);
        }

        pool.indicesGroupsCount = visibleCount;
        pool.RenderedTriangles = renderedTris;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendCoalescedSpan(int[] startsByte, int[] sizes, ref int visibleCount, int startByte, int size)
    {
        if (visibleCount > 0 && startsByte[(visibleCount - 1) * 2] + sizes[visibleCount - 1] * 4 == startByte)
        {
            sizes[visibleCount - 1] += size;
        }
        else
        {
            startsByte[visibleCount * 2] = startByte;
            startsByte[visibleCount * 2 + 1] = 0;
            sizes[visibleCount] = size;
            visibleCount++;
        }
    }

    private static List<ModelDataPoolLocation> ResetFlagged(List<ModelDataPoolLocation> flagged)
    {
        for (int f = 0; f < flagged.Count; f++)
        {
            flagged[f].FrustumVisible = false;
        }
        flagged.Clear();
        return flagged;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBoxInRange(BlockPos playerPos, int viewDistanceSq, PoolCullState state)
    {
        double px = playerPos.X;
        double pz = playerPos.Z;
        double nx = Math.Clamp(px, state.MinX, state.MaxX) - px;
        double nz = Math.Clamp(pz, state.MinZ, state.MaxZ) - pz;
        return nx * nx + nz * nz <= (double)viewDistanceSq + 32d * 32d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBoxInShadowPass(BlockPos playerPos, double shadowRangeX, double shadowRangeZ, PoolCullState state)
    {
        double px = playerPos.X;
        double pz = playerPos.Z;
        if (state.MinX - px >= shadowRangeX || state.MaxX - px <= -shadowRangeX)
        {
            return false;
        }
        if (state.MinZ - pz >= shadowRangeZ || state.MaxZ - pz <= -shadowRangeZ)
        {
            return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBoxInsidePlanes(Plane[] planes, int planeCount, PoolCullState state)
    {
        for (int p = 0; p < planeCount; p++)
        {
            Plane pl = planes[p];
            double cx = pl.normalX > 0 ? state.MaxX : state.MinX;
            double cy = pl.normalY > 0 ? state.MaxY : state.MinY;
            double cz = pl.normalZ > 0 ? state.MaxZ : state.MinZ;
            if (pl.normalX * cx + pl.normalY * cy + pl.normalZ * cz + pl.D < 0d)
            {
                return false;
            }
        }
        return true;
    }

    private static PoolCullState GetOrRebuildPoolState(
        MeshDataPool pool,
        List<ModelDataPoolLocation> locations,
        int n)
    {
        var state = PoolStates.GetOrCreateValue(pool);
        int indicesPos = pool.indicesPosition;
        int verticesPos = pool.verticesPosition;
        int usedVertices = pool.UsedVertices;
        ModelDataPoolLocation first = locations[0];
        ModelDataPoolLocation last = locations[n - 1];
        ModelDataPoolLocation mid = locations[n >> 1];

        if (state.Count != n ||
            state.IndicesPos != indicesPos ||
            state.VerticesPos != verticesPos ||
            state.UsedVertices != usedVertices ||
            !ReferenceEquals(state.First, first) ||
            !ReferenceEquals(state.Last, last) ||
            !ReferenceEquals(state.Mid, mid) ||
            state.CullData == null)
        {
            RecomputePoolState(pool, locations, n, indicesPos, verticesPos, usedVertices, first, last, mid, state);
        }

        return state;
    }

    private static float ComputePoolNearestDistSq(MeshDataPool pool, double camX, double camY, double camZ)
    {
        List<ModelDataPoolLocation> locations = GetPoolLocations(pool);
        int n = locations.Count;
        if (n == 0)
        {
            return float.MaxValue;
        }

        PoolCullState state = GetOrRebuildPoolState(pool, locations, n);

        double nx = Math.Clamp(camX, state.MinX, state.MaxX) - camX;
        double ny = Math.Clamp(camY, state.MinY, state.MaxY) - camY;
        double nz = Math.Clamp(camZ, state.MinZ, state.MaxZ) - camZ;
        double distSq = nx * nx + ny * ny + nz * nz;
        float distSqF = (float)distSq;
        return float.IsFinite(distSqF) && distSqF >= 0f ? distSqF : float.MaxValue;
    }

    private static void RecomputePoolState(
        MeshDataPool pool,
        List<ModelDataPoolLocation> locations,
        int n,
        int indicesPos,
        int verticesPos,
        int usedVertices,
        ModelDataPoolLocation first,
        ModelDataPoolLocation last,
        ModelDataPoolLocation mid,
        PoolCullState state)
    {
        PoolCullData cullData = state.CullData ??= new PoolCullData(pool.MaxPartsPerPool > 0 ? pool.MaxPartsPerPool : n);
        cullData.EnsureCapacity(n);

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        double maxZ = double.NegativeInfinity;
        int totalAllocatedIndices = 0;
        bool hasAny = false;

        for (int i = 0; i < n; i++)
        {
            ModelDataPoolLocation loc = locations[i];
            Sphere s = loc.FrustumCullSphere;
            int size = loc.IndicesEnd - loc.IndicesStart;
            totalAllocatedIndices += size;

            cullData.SetSlot(
                i,
                s.x,
                s.y,
                s.z,
                s.radius,
                s.radiusY,
                s.radiusZ,
                loc.IndicesStart * 4,
                size);

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

        state.Count = n;
        state.IndicesPos = indicesPos;
        state.VerticesPos = verticesPos;
        state.UsedVertices = usedVertices;
        state.TotalAllocatedTris = totalAllocatedIndices / 3;
        state.First = first;
        state.Last = last;
        state.Mid = mid;
        state.MinX = minX;
        state.MinY = minY;
        state.MinZ = minZ;
        state.MaxX = maxX;
        state.MaxY = maxY;
        state.MaxZ = maxZ;
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

    private static int[] EnsureVisibleIndicesScratch(int n)
    {
        if (t_visibleIndicesScratch == null || t_visibleIndicesScratch.Length < n)
        {
            t_visibleIndicesScratch = new int[Math.Max(n, 256)];
        }
        return t_visibleIndicesScratch;
    }
}
