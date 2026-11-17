using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;

namespace Vintagestory.API.Config;

/// <summary>
/// Manages empty-pool reclamation, slot ID reuse, delayed removal refcounting, and
/// dimension reset semantics for <see cref="MeshDataPoolMasterManager"/>,
/// <see cref="MeshDataPoolManager"/>, and <see cref="MeshDataPool"/>.
/// Stored out-of-line via <see cref="ConditionalWeakTable{TKey, TValue}"/> so
/// runtime IL patching preserves vanilla binary field layout.
/// </summary>
public static class OptimumMeshPoolLifecycle
{
    private sealed class MasterPoolState
    {
        public readonly List<MeshDataPoolManager?> PoolManagersByPoolId = new();
        public readonly List<int> PendingRemovalCountsByPoolId = new();
        public readonly Stack<int> FreePoolIds = new();
        public readonly List<MeshDataPoolManager> DirtyManagers = new();
        public readonly HashSet<MeshDataPoolManager> DirtyManagerSet = new();
        public int LivePoolCount;
    }

    private static readonly ConditionalWeakTable<MeshDataPoolMasterManager, MasterPoolState> MasterStates = new();

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "modelPools")]
    private static extern ref List<MeshDataPool> GetModelPools(MeshDataPoolMasterManager master);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "capi")]
    private static extern ref ICoreClientAPI GetMasterCapi(MeshDataPoolMasterManager master);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "removalQueue")]
    private static extern ref Queue<ModelDataPoolLocation[]>[] GetRemovalQueue(MeshDataPoolMasterManager master);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pools")]
    private static extern ref List<MeshDataPool> GetManagerPools(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "defaultVertexPoolSize")]
    private static extern ref int GetDefaultVertexPoolSize(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "defaultIndexPoolSize")]
    private static extern ref int GetDefaultIndexPoolSize(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolId")]
    private static extern ref int GetPoolId(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "modelRef")]
    private static extern ref MeshRef GetModelRef(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dimensionId")]
    private static extern ref int GetDimensionId(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolLocations")]
    private static extern ref List<ModelDataPoolLocation> GetPoolLocations(MeshDataPool pool);

    /// <summary>
    /// Validates pool capacity and resets <c>dimensionId</c> when reusing an empty pool,
    /// avoiding permanent rejection across dimensions while rejecting mismatched occupied pools.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanAcceptAndPrepareDimension(MeshDataPool pool, int dimension)
    {
        var locations = GetPoolLocations(pool);
        if (locations.Count >= pool.MaxPartsPerPool)
        {
            return false;
        }

        ref int poolDimension = ref GetDimensionId(pool);
        if (locations.Count == 0)
        {
            poolDimension = dimension;
            return true;
        }

        return dimension == poolDimension;
    }

    /// <summary>
    /// Idempotent, null-safe disposal helper for <see cref="MeshDataPool.Dispose(ICoreClientAPI)"/>.
    /// </summary>
    public static void DisposePool(MeshDataPool pool, ICoreClientAPI? capi)
    {
        if (pool == null || capi?.Render == null)
        {
            return;
        }

        ref MeshRef modelRef = ref GetModelRef(pool);
        if (modelRef != null)
        {
            capi.Render.DeleteMesh(modelRef);
            modelRef = null!;
        }
    }

    /// <summary>
    /// Associates a newly allocated pool created by <see cref="MeshDataPoolManager"/>
    /// with its owning manager so surplus empty pools can be unlinked on reclamation.
    /// Pools registered directly on <see cref="MeshDataPoolMasterManager"/> stay unmanaged.
    /// </summary>
    public static void BindPoolManager(
        MeshDataPoolMasterManager? master,
        MeshDataPool? pool,
        MeshDataPoolManager? manager)
    {
        if (master == null || pool == null || manager == null)
        {
            return;
        }

        var state = MasterStates.GetOrCreateValue(master);
        var modelPools = GetModelPools(master);
        int poolId = GetPoolId(pool);
        if ((uint)poolId < (uint)modelPools.Count && ReferenceEquals(modelPools[poolId], pool))
        {
            EnsureCapacityForPoolId(state, poolId);
            state.PoolManagersByPoolId[poolId] = manager;
        }
    }

    /// <summary>
    /// Assigns a pool ID (reusing reclaimed slots when available) and tracks live pools.
    /// </summary>
    public static void AddModelDataPool(MeshDataPoolMasterManager master, MeshDataPool pool)
    {
        if (pool == null)
        {
            return;
        }

        var state = MasterStates.GetOrCreateValue(master);
        var modelPools = GetModelPools(master);

        int poolId;
        if (OptimumConfig.MeshPoolReclaimEnabled && state.FreePoolIds.Count > 0)
        {
            poolId = state.FreePoolIds.Pop();
            while (modelPools.Count <= poolId)
            {
                modelPools.Add(null!);
            }
            modelPools[poolId] = pool;
        }
        else
        {
            poolId = modelPools.Count;
            modelPools.Add(pool);
        }

        GetPoolId(pool) = poolId;
        EnsureCapacityForPoolId(state, poolId);
        state.PoolManagersByPoolId[poolId] = null;
        state.PendingRemovalCountsByPoolId[poolId] = 0;
        state.LivePoolCount++;
        OptimumDiagnostics.RecordMeshPoolAllocated();
    }

    /// <summary>
    /// Hides and queues delayed removals (with per-pool refcounts) or executes immediate removal and reclamation.
    /// </summary>
    public static void RemoveDataPoolLocations(MeshDataPoolMasterManager master, ModelDataPoolLocation[]? locations)
    {
        if (locations == null || locations.Length == 0)
        {
            return;
        }

        var state = MasterStates.GetOrCreateValue(master);
        if (master.DelayedPoolLocationRemoval)
        {
            var modelPools = GetModelPools(master);
            for (int i = 0; i < locations.Length; i++)
            {
                var location = locations[i];
                if (location == null)
                {
                    continue;
                }

                location.Hide = true;
                int poolId = location.PoolId;
                if ((uint)poolId < (uint)modelPools.Count && modelPools[poolId] != null)
                {
                    EnsureCapacityForPoolId(state, poolId);
                    state.PendingRemovalCountsByPoolId[poolId]++;
                }
            }

            GetRemovalQueue(master)[0].Enqueue(locations);
            return;
        }

        RemoveLocationsNowCore(master, state, locations);
        ReclaimDirtyManagers(master, state);
    }

    /// <summary>
    /// Advances the 3-frame delayed removal pipeline and reclaims any newly emptied surplus pools once per frame.
    /// </summary>
    public static void OnFrame(MeshDataPoolMasterManager master, float dt, float[] modelviewMatrix, float[] shadowMVPMatrix)
    {
        master.currentDt = dt;
        master.currentModelViewMatrix = modelviewMatrix;
        master.shadowMVPMatrix = shadowMVPMatrix;

        var state = MasterStates.GetOrCreateValue(master);
        var removalQueue = GetRemovalQueue(master);

        while (removalQueue[3].Count > 0)
        {
            RemoveLocationsNowCore(master, state, removalQueue[3].Dequeue());
        }

        while (removalQueue[2].Count > 0)
        {
            removalQueue[3].Enqueue(removalQueue[2].Dequeue());
        }

        while (removalQueue[1].Count > 0)
        {
            removalQueue[2].Enqueue(removalQueue[1].Dequeue());
        }

        while (removalQueue[0].Count > 0)
        {
            removalQueue[1].Enqueue(removalQueue[0].Dequeue());
        }

        ReclaimDirtyManagers(master, state);
    }

    /// <summary>
    /// Immediately processes one location removal batch and reclaims eligible surplus empty pools.
    /// </summary>
    public static void RemoveLocationsNow(MeshDataPoolMasterManager master, ModelDataPoolLocation[]? locations)
    {
        if (locations == null || locations.Length == 0)
        {
            return;
        }

        var state = MasterStates.GetOrCreateValue(master);
        RemoveLocationsNowCore(master, state, locations);
        ReclaimDirtyManagers(master, state);
    }

    /// <summary>
    /// Disposes all currently live pools (skipping reclaimed null slots) and resets lifecycle state.
    /// </summary>
    public static void DisposeAllPools(MeshDataPoolMasterManager master, ICoreClientAPI? capi)
    {
        var modelPools = GetModelPools(master);
        for (int i = 0; i < modelPools.Count; i++)
        {
            var pool = modelPools[i];
            if (pool != null)
            {
                pool.Dispose(capi);
                modelPools[i] = null!;
            }
        }

        modelPools.Clear();

        if (MasterStates.TryGetValue(master, out var state))
        {
            for (int i = 0; i < state.PoolManagersByPoolId.Count; i++)
            {
                var manager = state.PoolManagersByPoolId[i];
                if (manager != null)
                {
                    GetManagerPools(manager).Clear();
                }
            }

            state.PoolManagersByPoolId.Clear();
            state.PendingRemovalCountsByPoolId.Clear();
            state.FreePoolIds.Clear();
            state.DirtyManagers.Clear();
            state.DirtyManagerSet.Clear();
            state.LivePoolCount = 0;
        }

        var removalQueue = GetRemovalQueue(master);
        if (removalQueue != null)
        {
            for (int i = 0; i < removalQueue.Length; i++)
            {
                removalQueue[i]?.Clear();
            }
        }
    }

    /// <summary>
    /// Computes aggregate fragmentation across live pools, skipping reclaimed null slots and returning 0 when empty.
    /// </summary>
    public static float CalcFragmentation(MeshDataPoolMasterManager master)
    {
        var modelPools = GetModelPools(master);
        long totalUsed = 0;
        long totalAllocated = 0;
        for (int i = 0; i < modelPools.Count; i++)
        {
            var pool = modelPools[i];
            if (pool == null)
            {
                continue;
            }

            totalUsed += pool.UsedVertices;
            totalAllocated += pool.VerticesPoolSize;
        }

        if (totalAllocated <= 0)
        {
            return 0f;
        }

        return (float)(1.0 - (double)totalUsed / (double)totalAllocated);
    }

    /// <summary>
    /// Returns the number of live (unreclaimed) model data pools managed by this master instance.
    /// </summary>
    public static int QuantityModelDataPools(MeshDataPoolMasterManager master)
    {
        if (MasterStates.TryGetValue(master, out var state))
        {
            return state.LivePoolCount;
        }

        return GetModelPools(master).Count;
    }

    private static void RemoveLocationsNowCore(
        MeshDataPoolMasterManager master,
        MasterPoolState state,
        ModelDataPoolLocation[]? locations)
    {
        if (locations == null)
        {
            return;
        }

        var modelPools = GetModelPools(master);
        var capi = GetMasterCapi(master);

        for (int i = 0; i < locations.Length; i++)
        {
            var location = locations[i];
            if (location == null)
            {
                capi?.World?.Logger.Error("Could not remove model data from the master pool. Something wonky is happening. Ignoring for now.");
                continue;
            }

            int poolId = location.PoolId;
            if ((uint)poolId < (uint)state.PendingRemovalCountsByPoolId.Count && state.PendingRemovalCountsByPoolId[poolId] > 0)
            {
                state.PendingRemovalCountsByPoolId[poolId]--;
            }

            if ((uint)poolId >= (uint)modelPools.Count || modelPools[poolId] == null)
            {
                capi?.World?.Logger.Error("Could not remove model data from the master pool. Something wonky is happening. Ignoring for now.");
                continue;
            }

            var pool = modelPools[poolId];
            pool.RemoveLocation(location);

            if (pool.IsEmpty() && state.PendingRemovalCountsByPoolId[poolId] == 0)
            {
                var ownerManager = (uint)poolId < (uint)state.PoolManagersByPoolId.Count
                    ? state.PoolManagersByPoolId[poolId]
                    : null;
                if (ownerManager != null && state.DirtyManagerSet.Add(ownerManager))
                {
                    state.DirtyManagers.Add(ownerManager);
                }
            }
        }
    }

    private static void ReclaimDirtyManagers(MeshDataPoolMasterManager master, MasterPoolState state)
    {
        if (state.DirtyManagers.Count == 0)
        {
            return;
        }

        if (!OptimumConfig.MeshPoolReclaimEnabled)
        {
            state.DirtyManagers.Clear();
            state.DirtyManagerSet.Clear();
            return;
        }

        var capi = GetMasterCapi(master);
        var modelPools = GetModelPools(master);

        for (int i = 0; i < state.DirtyManagers.Count; i++)
        {
            ReclaimManagerEmptyPools(state, capi, modelPools, state.DirtyManagers[i]);
        }

        state.DirtyManagers.Clear();
        state.DirtyManagerSet.Clear();
    }

    private static void ReclaimManagerEmptyPools(
        MasterPoolState state,
        ICoreClientAPI? capi,
        List<MeshDataPool> modelPools,
        MeshDataPoolManager manager)
    {
        var pools = GetManagerPools(manager);
        if (pools.Count == 0)
        {
            return;
        }

        int defaultVerts = GetDefaultVertexPoolSize(manager);
        int defaultIndices = GetDefaultIndexPoolSize(manager);
        MeshDataPool? keptWarmPool = null;
        bool reclaimedAny = false;

        for (int i = pools.Count - 1; i >= 0; i--)
        {
            var pool = pools[i];
            if (pool == null)
            {
                int lastNullSwap = pools.Count - 1;
                if (i != lastNullSwap)
                {
                    pools[i] = pools[lastNullSwap];
                }
                pools.RemoveAt(lastNullSwap);
                continue;
            }

            if (!pool.IsEmpty())
            {
                continue;
            }

            int poolId = GetPoolId(pool);
            if ((uint)poolId < (uint)state.PendingRemovalCountsByPoolId.Count &&
                state.PendingRemovalCountsByPoolId[poolId] > 0)
            {
                continue;
            }

            bool isDefaultSized = defaultVerts > 0 && defaultIndices > 0 &&
                pool.VerticesPoolSize <= defaultVerts && pool.IndicesPoolSize <= defaultIndices;
            if (isDefaultSized && keptWarmPool == null)
            {
                keptWarmPool = pool;
                continue;
            }

            long estimatedBytes = EstimatePoolGpuBytes(capi, pool);
            pool.Dispose(capi);

            int lastIndex = pools.Count - 1;
            if (i != lastIndex)
            {
                pools[i] = pools[lastIndex];
            }
            pools.RemoveAt(lastIndex);

            if ((uint)poolId < (uint)modelPools.Count && ReferenceEquals(modelPools[poolId], pool))
            {
                modelPools[poolId] = null!;
                state.FreePoolIds.Push(poolId);
                state.PoolManagersByPoolId[poolId] = null;
                state.PendingRemovalCountsByPoolId[poolId] = 0;
                if (state.LivePoolCount > 0)
                {
                    state.LivePoolCount--;
                }
            }

            reclaimedAny = true;
            OptimumDiagnostics.RecordMeshPoolReclaimed(estimatedBytes);
            OptimumChunkStreamStats.RecordPoolReclamation();
        }

        // Keep the single warm default pool at the tail so MeshDataPoolManager.AddModel
        // fills partial fragmentation holes in occupied pools before claiming the warm buffer.
        if (reclaimedAny && keptWarmPool != null && pools.Count > 1 && !ReferenceEquals(pools[pools.Count - 1], keptWarmPool))
        {
            int warmIndex = pools.IndexOf(keptWarmPool);
            if (warmIndex >= 0)
            {
                int lastIndex = pools.Count - 1;
                pools[warmIndex] = pools[lastIndex];
                pools[lastIndex] = keptWarmPool;
            }
        }
    }

    private static long EstimatePoolGpuBytes(ICoreClientAPI? capi, MeshDataPool pool)
    {
        bool useSSBOs = capi?.Render?.UseSSBOs == true;
        long bytesPerVertex = useSSBOs ? 16L : 28L;
        long bytesPerIndex = useSSBOs ? 0L : 4L;
        return pool.VerticesPoolSize * bytesPerVertex + pool.IndicesPoolSize * bytesPerIndex;
    }

    private static void EnsureCapacityForPoolId(MasterPoolState state, int poolId)
    {
        while (state.PoolManagersByPoolId.Count <= poolId)
        {
            state.PoolManagersByPoolId.Add(null);
            state.PendingRemovalCountsByPoolId.Add(0);
        }
    }
}
