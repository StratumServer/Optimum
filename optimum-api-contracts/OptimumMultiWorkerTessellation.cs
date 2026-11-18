using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace Vintagestory.API.Config;

/// <summary>
/// Provides per-type synchronization guards for unaudited mod Block / BlockEntity
/// tessellation callbacks and per-worker thread-safe recycling for
/// <see cref="MeshDataRecycler"/> so multi-worker chunk meshing remains safe
/// without sacrificing main-thread or third-party mod compatibility.
/// </summary>
public static class OptimumTesselationModBoundary
{
    private static readonly ConcurrentDictionary<Assembly, bool> AuditedSafeAssemblies = new();
    private static readonly ConditionalWeakTable<Type, Lock> ModTypeLocks = new();
    private static readonly Lock FallbackLock = new();

    /// <summary>
    /// Returns true when the given type belongs to core Vintage Story or Optimum
    /// assemblies whose chunk-meshing state is audited for concurrent execution.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAuditedSafeType(Type? type)
    {
        if (type == null) return true;
        return AuditedSafeAssemblies.GetOrAdd(type.Assembly, static asm =>
        {
            string? simpleName = asm.GetName().Name;
            if (string.IsNullOrEmpty(simpleName)) return false;
            return simpleName.Equals("VintagestoryAPI", StringComparison.OrdinalIgnoreCase)
                || simpleName.Equals("VintagestoryLib", StringComparison.OrdinalIgnoreCase)
                || simpleName.Equals("VSEssentials", StringComparison.OrdinalIgnoreCase)
                || simpleName.Equals("VSSurvivalMod", StringComparison.OrdinalIgnoreCase)
                || simpleName.Equals("VSCreativeMod", StringComparison.OrdinalIgnoreCase)
                || simpleName.Equals("Optimum.Api.Contracts", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Returns a stable per-type lock for unaudited third-party mod Block,
    /// BlockEntity, or BlockEntityBehavior tessellators.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Lock? TryGetModTypeLock(Type? type)
    {
        if (type == null || IsAuditedSafeType(type)) return null;
        return ModTypeLocks.GetValue(type, static _ => new Lock());
    }

    /// <summary>
    /// Returns a coarse lock when an unaudited ITexPositionSource implementation
    /// is invoked outside its declaring Block/BlockEntity guard.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Lock? TryGetForeignTexSourceLock(ITexPositionSource? source)
    {
        if (source == null) return null;
        Type type = source.GetType();
        if (IsAuditedSafeType(type)) return null;
        return ModTypeLocks.GetValue(type, static _ => new Lock());
    }

    public static Lock GetGlobalFallbackLock() => FallbackLock;
}

/// <summary>
/// Thread-local / per-worker shallow <see cref="MeshData"/> shadow view that isolates
/// in-place <see cref="MeshData.Flags"/> mutations performed by <c>Block.OnJsonTesselation</c>
/// and <c>JsonTesselator.AddJsonModelDataToMesh</c> (normal re-packing when <c>tfMatrix != null</c>)
/// without cloning <c>xyz</c>, <c>Uv</c>, <c>Rgba</c>, or <c>Indices</c>.
/// </summary>
public sealed class OptimumWorkerMeshShadowCache
{
    private sealed class ShadowEntry
    {
        public readonly MeshData Shadow;
        public readonly int[] BaselineFlags;
        public readonly int BaselineFlagsCount;

        public ShadowEntry(MeshData shadow, int[] baselineFlags, int baselineFlagsCount)
        {
            Shadow = shadow;
            BaselineFlags = baselineFlags;
            BaselineFlagsCount = baselineFlagsCount;
        }
    }

    private readonly ConditionalWeakTable<MeshData, ShadowEntry> _entries = new();

    public MeshData PrepareShadow(MeshData source)
    {
        if (source.Flags == null)
        {
            return source;
        }

        if (!_entries.TryGetValue(source, out ShadowEntry? entry)
            || entry.BaselineFlags.Length < source.Flags.Length
            || entry.BaselineFlagsCount != source.FlagsCount)
        {
            entry = CreateEntry(source);
            _entries.AddOrUpdate(source, entry);
        }
        else
        {
            int copyCount = Math.Min(entry.BaselineFlags.Length, entry.Shadow.Flags!.Length);
            Array.Copy(entry.BaselineFlags, entry.Shadow.Flags, copyCount);
            entry.Shadow.SetVerticesCount(source.VerticesCount);
            entry.Shadow.SetIndicesCount(source.IndicesCount);
        }

        return entry.Shadow;
    }

    private static ShadowEntry CreateEntry(MeshData source)
    {
        int[] srcFlags = source.Flags!;
        int[] baseline = new int[srcFlags.Length];
        Array.Copy(srcFlags, baseline, srcFlags.Length);

        int[] shadowFlags = new int[srcFlags.Length];
        Array.Copy(baseline, shadowFlags, srcFlags.Length);

        MeshData shadow = new MeshData(false)
        {
            VerticesPerFace = source.VerticesPerFace,
            IndicesPerFace = source.IndicesPerFace,
            VerticesMax = source.VerticesMax,
            IndicesMax = source.IndicesMax,
            xyz = source.xyz,
            Uv = source.Uv,
            Rgba = source.Rgba,
            Normals = source.Normals,
            NormalsCount = source.NormalsCount,
            Flags = shadowFlags,
            Indices = source.Indices,
            XyzFaces = source.XyzFaces,
            XyzFacesCount = source.XyzFacesCount,
            TextureIndices = source.TextureIndices,
            TextureIndicesCount = source.TextureIndicesCount,
            TextureIds = source.TextureIds,
            ClimateColorMapIds = source.ClimateColorMapIds,
            SeasonColorMapIds = source.SeasonColorMapIds,
            ColorMapIdsCount = source.ColorMapIdsCount,
            RenderPassesAndExtraBits = source.RenderPassesAndExtraBits,
            RenderPassCount = source.RenderPassCount,
            CustomFloats = source.CustomFloats,
            CustomShorts = source.CustomShorts,
            CustomBytes = source.CustomBytes,
            CustomInts = source.CustomInts
        };
        shadow.SetVerticesCount(source.VerticesCount);
        shadow.SetIndicesCount(source.IndicesCount);

        return new ShadowEntry(shadow, baseline, source.FlagsCount);
    }
}

/// <summary>
/// Worker-partitioned thread-safe implementation for <see cref="MeshDataRecycler"/>.
/// Each producer/consumer thread rents and stores recycled <see cref="MeshData"/>
/// buffers inside its own local <see cref="WorkerBucket"/> (avoiding shared
/// SortedList races), while cross-thread <see cref="Recycle"/> calls route
/// buffers back to the originating worker via bounded MPSC queues.
/// </summary>
public static class OptimumMeshRecyclerBridge
{
    private const int MinimumSizeForRecycling = 4096;
    private const int SmallLimit = 368;
    private const int MediumLimit = 3072;
    private const int Four = 4;
    private const int MaxReturnQueuePerWorker = 192;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "game")]
    private static extern ref IClientWorldAccessor GetGame(MeshDataRecycler recycler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "disposed")]
    private static extern ref bool GetDisposed(MeshDataRecycler recycler);

    private static readonly ConditionalWeakTable<MeshDataRecycler, RecyclerHost> Hosts = new();
    private static readonly ConditionalWeakTable<MeshData, WorkerBucket> OriginBuckets = new();

    private sealed class RecyclerHost
    {
        public readonly IClientWorldAccessor? Game;
        public readonly Lock RegistryLock = new();
        public readonly List<WorkerBucket> AllBuckets = new();
        public readonly ThreadLocal<WorkerBucket?> ThreadBucket = new(trackAllValues: true);
        public volatile bool Disposed;

        public RecyclerHost(IClientWorldAccessor? game)
        {
            Game = game;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public WorkerBucket GetOrCreateCurrentBucket()
        {
            WorkerBucket? bucket = ThreadBucket.Value;
            if (bucket != null) return bucket;

            lock (RegistryLock)
            {
                bucket = ThreadBucket.Value;
                if (bucket != null) return bucket;
                bucket = new WorkerBucket(this);
                ThreadBucket.Value = bucket;
                AllBuckets.Add(bucket);
                return bucket;
            }
        }
    }

    private sealed class WorkerBucket
    {
        public readonly RecyclerHost Host;
        public readonly SortedList<float, MeshData> SmallSizes = new();
        public readonly SortedList<float, MeshData> MediumSizes = new();
        public readonly SortedList<float, MeshData> LargeSizes = new();
        public readonly ConcurrentQueue<MeshData> ReturnQueue = new();
        private long _pad0, _pad1, _pad2, _pad3, _pad4, _pad5, _pad6, _pad7;
        private int _returnQueueCount;
        private float[] _keysScratch = new float[256];

        public WorkerBucket(RecyclerHost host)
        {
            Host = host;
            _ = _pad0 + _pad1 + _pad2 + _pad3 + _pad4 + _pad5 + _pad6 + _pad7;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool EnqueueReturn(MeshData mesh)
        {
            if (Host.Disposed) return false;
            if (Interlocked.Increment(ref _returnQueueCount) > MaxReturnQueuePerWorker)
            {
                Interlocked.Decrement(ref _returnQueueCount);
                return false;
            }
            ReturnQueue.Enqueue(mesh);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MeshData GetOrCreateMesh(int minimumVertices, bool hostDisposed)
        {
            if (hostDisposed || Host.Disposed)
            {
                return new MeshData(minimumVertices);
            }

            DrainReturnQueue();

            int minimumCapacity = ((minimumVertices + Four - 1) / Four);
            MeshData? newMesh = minimumCapacity >= MediumLimit
                ? TryGet(LargeSizes, minimumCapacity)
                : (minimumCapacity >= SmallLimit
                    ? TryGet(MediumSizes, minimumCapacity)
                    : TryGet(SmallSizes, minimumCapacity));

            if (newMesh == null)
            {
                newMesh = new MeshData(minimumVertices);
            }
            newMesh.Recyclable = true;
            OriginBuckets.AddOrUpdate(newMesh, this);
            return newMesh;
        }

        public void DoRecycling()
        {
            if (Host.Disposed)
            {
                ClearLocal();
                return;
            }

            ControlSizeOfLists();

            long elapsedMs = Host.Game?.ElapsedMilliseconds ?? 0;
            while (ReturnQueue.TryDequeue(out MeshData? recycled))
            {
                Interlocked.Decrement(ref _returnQueueCount);
                if (recycled == null) continue;
                int entrySize = recycled.VerticesMax / Four;
                if (entrySize < MinimumSizeForRecycling / Four)
                {
                    recycled.DisposeBasicData();
                    continue;
                }
                recycled.RecyclingTime = elapsedMs;
                if (entrySize >= MediumLimit)
                {
                    TryAdd(LargeSizes, entrySize, recycled);
                }
                else if (entrySize >= SmallLimit)
                {
                    TryAdd(MediumSizes, entrySize, recycled);
                }
                else
                {
                    TryAdd(SmallSizes, entrySize, recycled);
                }
            }
        }

        public void ClearLocal()
        {
            while (ReturnQueue.TryDequeue(out MeshData? stale))
            {
                Interlocked.Decrement(ref _returnQueueCount);
                stale?.DisposeBasicData();
            }
            DisposeSortedList(SmallSizes);
            DisposeSortedList(MediumSizes);
            DisposeSortedList(LargeSizes);
        }

        private static void DisposeSortedList(SortedList<float, MeshData> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                list.GetValueAtIndex(i)?.DisposeBasicData();
            }
            list.Clear();
        }

        private void ControlSizeOfLists()
        {
            int bucketDivisor;
            lock (Host.RegistryLock)
            {
                bucketDivisor = Math.Max(1, Host.AllBuckets.Count);
            }
            RemoveOldest(SmallSizes, Math.Max(75000, 300000 / bucketDivisor));
            RemoveOldest(MediumSizes, Math.Max(225000, 900000 / bucketDivisor));
            RemoveOldest(LargeSizes, Math.Max(560000, 2240000 / bucketDivisor));
        }

        private void RemoveOldest(SortedList<float, MeshData> list, int maxSize)
        {
            if (list.Count == 0) return;

            long cutoffMs = (Host.Game?.ElapsedMilliseconds ?? 0) - MeshDataRecycler.TTL;
            while (list.Count > 0)
            {
                int totalsize = 0;
                int indexOldest = 0;
                long timeOldest = list.GetValueAtIndex(0).RecyclingTime;
                int count = list.Count;
                for (int i = 0; i < count; i++)
                {
                    totalsize += (int)list.GetKeyAtIndex(i);
                    long t = list.GetValueAtIndex(i).RecyclingTime;
                    if (t < timeOldest)
                    {
                        timeOldest = t;
                        indexOldest = i;
                    }
                }
                if (totalsize > maxSize || timeOldest < cutoffMs)
                {
                    list.GetValueAtIndex(indexOldest).DisposeBasicData();
                    list.RemoveAt(indexOldest);
                }
                else
                {
                    break;
                }
            }
        }

        private void DrainReturnQueue()
        {
            if (ReturnQueue.IsEmpty) return;
            DoRecycling();
        }

        private static void TryAdd(SortedList<float, MeshData> list, int intkey, MeshData entry)
        {
            const int maxPerBucket = 16;
            float key = intkey;
            float upperBound = intkey + 1;
            for (int attempt = 0; attempt < maxPerBucket && key < upperBound; attempt++)
            {
                if (!list.ContainsKey(key))
                {
                    list.Add(key, entry);
                    return;
                }
                float next = key + 0.05f;
                if (next == key)
                {
                    next = MathF.BitIncrement(key);
                }
                key = next;
            }

            entry.DisposeBasicData();
        }

        private MeshData? TryGet(SortedList<float, MeshData> list, int entrySize)
        {
            int count = list.Count;
            if (count == 0) return null;

            if (_keysScratch.Length < count)
            {
                _keysScratch = new float[Math.Max(count, _keysScratch.Length * 2)];
            }
            var keys = list.Keys;
            for (int i = 0; i < count; i++)
            {
                _keysScratch[i] = keys[i];
            }

            int idx = Array.BinarySearch(_keysScratch, 0, count, (float)entrySize);
            if (idx < 0)
            {
                idx = ~idx;
            }
            if (idx < count && _keysScratch[idx] <= entrySize * 1.15f + 4f)
            {
                MeshData result = list.GetValueAtIndex(idx);
                list.RemoveAt(idx);
                result.RecyclingTime = 0;
                return result;
            }
            return null;
        }
    }

    private static RecyclerHost GetHost(MeshDataRecycler recycler)
    {
        return Hosts.GetValue(recycler, static r => new RecyclerHost(GetGame(r)));
    }

    public static MeshData GetOrCreateMesh(MeshDataRecycler recycler, int minimumVertices)
    {
        bool disposed = GetDisposed(recycler);
        if (disposed)
        {
            return new MeshData(minimumVertices);
        }
        return GetHost(recycler).GetOrCreateCurrentBucket().GetOrCreateMesh(minimumVertices, disposed);
    }

    public static void DoRecycling(MeshDataRecycler recycler)
    {
        if (GetDisposed(recycler)) return;
        GetHost(recycler).GetOrCreateCurrentBucket().DoRecycling();
    }

    public static void Recycle(MeshDataRecycler recycler, MeshData? meshData)
    {
        if (meshData == null) return;
        if (GetDisposed(recycler))
        {
            meshData.DisposeBasicData();
            return;
        }
        RecyclerHost host = GetHost(recycler);
        if (host.Disposed)
        {
            meshData.DisposeBasicData();
            return;
        }
        if (OriginBuckets.TryGetValue(meshData, out WorkerBucket? origin) && origin.Host == host)
        {
            if (!origin.EnqueueReturn(meshData))
            {
                meshData.DisposeBasicData();
            }
            return;
        }
        WorkerBucket fallback = host.GetOrCreateCurrentBucket();
        if (!fallback.EnqueueReturn(meshData))
        {
            meshData.DisposeBasicData();
        }
    }

    public static void Dispose(MeshDataRecycler recycler)
    {
        GetDisposed(recycler) = true;
        if (!Hosts.TryGetValue(recycler, out RecyclerHost? host)) return;
        host.Disposed = true;
        lock (host.RegistryLock)
        {
            for (int i = 0; i < host.AllBuckets.Count; i++)
            {
                host.AllBuckets[i].ClearLocal();
            }
            host.AllBuckets.Clear();
        }
        host.ThreadBucket.Dispose();
    }
}

/// <summary>
/// Synchronizes <see cref="ObjectCacheUtil.TryGet{T}"/>, <see cref="ObjectCacheUtil.GetOrCreate{T}"/>,
/// and <see cref="ObjectCacheUtil.Delete"/> around <see cref="ICoreAPI.ObjectCache"/> so concurrent
/// chunk-meshing workers and main-thread renderers never corrupt the shared backing Dictionary.
/// </summary>
public static class OptimumObjectCacheBridge
{
    public static T? TryGet<T>(ICoreAPI api, string key)
    {
        var cache = api?.ObjectCache;
        if (cache == null) return default;
        lock (cache)
        {
            if (cache.TryGetValue(key, out object? obj))
            {
                return (T?)obj;
            }
            return default;
        }
    }

    public static T GetOrCreate<T>(ICoreAPI api, string key, CreateCachableObjectDelegate<T> onRequireCreate)
    {
        var cache = api?.ObjectCache;
        if (cache == null) return onRequireCreate();
        lock (cache)
        {
            if (cache.TryGetValue(key, out object? obj) && obj != null)
            {
                return (T)obj;
            }
        }
        T typedObj = onRequireCreate();
        lock (cache)
        {
            if (cache.TryGetValue(key, out object? existing) && existing != null)
            {
                return (T)existing;
            }
            cache[key] = typedObj!;
            return typedObj;
        }
    }

    public static bool Delete(ICoreAPI api, string key)
    {
        if (key == null) return false;
        var cache = api?.ObjectCache;
        if (cache == null) return false;
        lock (cache)
        {
            return cache.Remove(key);
        }
    }
}

/// <summary>
/// Lambda-free helper for lazily allocating per-key reference containers in
/// <see cref="ICoreAPI.ObjectCache"/> under synchronization without emitting
/// compiler-generated closure types at patch call sites.
/// </summary>
public static class OptimumCacheHelper
{
    private static class Factory<T> where T : class, new()
    {
        public static readonly CreateCachableObjectDelegate<T> Create = static () => new T();
    }

    public static T GetOrCreateDefault<T>(ICoreAPI api, string key) where T : class, new()
    {
        return OptimumObjectCacheBridge.GetOrCreate(api, key, Factory<T>.Create);
    }

    /// <summary>
    /// Looks up <paramref name="key"/> under <paramref name="cache"/> lock, invokes
    /// <paramref name="factory"/> outside the lock on cache miss, and stores the result
    /// under a double-checked lock write.
    /// </summary>
    public static TValue GetOrAdd<TKey, TValue, TState>(
        Dictionary<TKey, TValue> cache,
        TKey key,
        TState state,
        System.Func<TState, TValue> factory) where TKey : notnull
    {
        lock (cache)
        {
            if (cache.TryGetValue(key, out TValue? cached))
            {
                return cached;
            }
        }

        TValue created = factory(state);

        lock (cache)
        {
            if (cache.TryGetValue(key, out TValue? winner))
            {
                return winner;
            }

            cache[key] = created;
            return created;
        }
    }
}

/// <summary>
/// Per-call <see cref="ITexPositionSource"/> for crate mesh/label generation so
/// concurrent chunk workers never mutate shared <c>BlockCrate</c> fields.
/// </summary>
public sealed class OptimumCrateTexSource : ITexPositionSource
{
    private readonly ICoreClientAPI _capi;
    private readonly ITexPositionSource _inner;
    private readonly string? _curType;
    private readonly bool _useLabelPos;
    private readonly TextureAtlasPosition? _labelPos;

    public OptimumCrateTexSource(
        ICoreClientAPI capi,
        ITexPositionSource inner,
        string? curType,
        bool useLabelPos,
        TextureAtlasPosition? labelPos)
    {
        _capi = capi;
        _inner = inner;
        _curType = curType;
        _useLabelPos = useLabelPos;
        _labelPos = labelPos;
    }

    public Vintagestory.API.MathTools.Size2i AtlasSize => _inner.AtlasSize;

    public TextureAtlasPosition? this[string textureCode]
    {
        get
        {
            if (_useLabelPos) return _labelPos;
            TextureAtlasPosition? pos = _inner[_curType + "-" + textureCode];
            pos ??= _inner[textureCode];
            pos ??= _capi.BlockTextureAtlas.UnknownTexturePosition;
            return pos;
        }
    }
}

/// <summary>
/// Per-call <see cref="ITexPositionSource"/> for lantern mesh generation so
/// concurrent chunk workers never mutate shared <c>BlockLantern</c> fields.
/// </summary>
public sealed class OptimumLanternTexSource : ITexPositionSource
{
    private readonly ITexPositionSource _inner;
    private readonly ITexPositionSource _glassSource;
    private readonly string _curMat;
    private readonly string _curLining;

    public OptimumLanternTexSource(
        Vintagestory.API.MathTools.Size2i atlasSize,
        ITexPositionSource inner,
        ITexPositionSource glassSource,
        string curMat,
        string curLining)
    {
        AtlasSize = atlasSize;
        _inner = inner;
        _glassSource = glassSource;
        _curMat = curMat;
        _curLining = curLining;
    }

    public Vintagestory.API.MathTools.Size2i AtlasSize { get; }

    public TextureAtlasPosition this[string textureCode]
    {
        get
        {
            if (textureCode == "material") return _inner[_curMat];
            if (textureCode == "material-deco") return _inner["deco-" + _curMat];
            if (textureCode == "material-grid") return _inner["grid-" + _curMat];
            if (textureCode == "lining") return _inner[_curLining == "plain" ? _curMat : _curLining];
            if (textureCode == "glass") return _glassSource["material"];
            return _inner[textureCode];
        }
    }
}

/// <summary>
/// Per-call <see cref="ITexPositionSource"/> for inventory crop-prop icon rendering.
/// </summary>
public sealed class OptimumCropPropInvTexSource : ITexPositionSource
{
    private readonly ICoreClientAPI _capi;
    private readonly string _type;

    public OptimumCropPropInvTexSource(ICoreClientAPI capi, string type)
    {
        _capi = capi;
        _type = type;
    }

    public Vintagestory.API.MathTools.Size2i AtlasSize => _capi.BlockTextureAtlas.Size;

    public TextureAtlasPosition this[string textureCode]
    {
        get
        {
            _capi.BlockTextureAtlas.GetOrInsertTexture(new AssetLocation("block/meta/cropprop/" + _type), out _, out var texPos);
            return texPos;
        }
    }
}

/// <summary>
/// Per-call <see cref="ITexPositionSource"/> for placed crop-prop block tessellation
/// so concurrent workers never read or mutate a shared <c>nowTesselatingShape</c> field.
/// </summary>
public sealed class OptimumCropPropTexSource : ITexPositionSource
{
    private readonly ICoreClientAPI _capi;
    private readonly Dictionary<string, CompositeTexture>? _configTextures;
    private readonly Dictionary<string, AssetLocation>? _shapeTextures;

    public OptimumCropPropTexSource(
        ICoreClientAPI capi,
        Dictionary<string, CompositeTexture>? configTextures,
        Dictionary<string, AssetLocation>? shapeTextures)
    {
        _capi = capi;
        _configTextures = configTextures;
        _shapeTextures = shapeTextures;
    }

    public Vintagestory.API.MathTools.Size2i AtlasSize => _capi.BlockTextureAtlas.Size;

    public TextureAtlasPosition? this[string textureCode]
    {
        get
        {
            if (_configTextures != null && _configTextures.TryGetValue(textureCode, out CompositeTexture? ct))
            {
                _capi.BlockTextureAtlas.GetOrInsertTexture(ct, out _, out var texPosb);
                return texPosb;
            }

            if (_shapeTextures != null && _shapeTextures.TryGetValue(textureCode, out AssetLocation? shapeTex))
            {
                _capi.BlockTextureAtlas.GetOrInsertTexture(shapeTex, out _, out var texPos);
                return texPos;
            }

            return _capi.BlockTextureAtlas.UnknownTexturePosition;
        }
    }
}

/// <summary>
/// Per-call <see cref="ITexPositionSource"/> that overrides one texture code with a
/// resolved atlas position so callers never mutate shared <c>CompositeTexture.Baked</c>.
/// </summary>
public sealed class OptimumVariantOverrideTexSource : ITexPositionSource
{
    private readonly ITexPositionSource _inner;
    private readonly string _textureCode;
    private readonly TextureAtlasPosition? _overridePos;

    public OptimumVariantOverrideTexSource(
        ITexPositionSource inner,
        string textureCode,
        TextureAtlasPosition? overridePos)
    {
        _inner = inner;
        _textureCode = textureCode;
        _overridePos = overridePos;
    }

    public Vintagestory.API.MathTools.Size2i AtlasSize => _inner.AtlasSize;

    public TextureAtlasPosition? this[string textureCode] =>
        textureCode == _textureCode && _overridePos != null ? _overridePos : _inner[textureCode];
}

/// <summary>
/// Per-call <see cref="ITexPositionSource"/> for plant container content meshing
/// so global cache lookups do not serialize shape tessellation across workers.
/// </summary>
public sealed class OptimumPlantContentTexSource : ITexPositionSource
{
    private readonly ICoreClientAPI _capi;
    private readonly ItemStack _content;
    private readonly Dictionary<string, CompositeTexture>? _propTextures;
    private readonly Dictionary<string, AssetLocation>? _shapeTextures;
    private readonly ITexPositionSource _contentTexSource;

    public OptimumPlantContentTexSource(
        ICoreClientAPI capi,
        ItemStack content,
        Dictionary<string, CompositeTexture>? propTextures,
        Dictionary<string, AssetLocation>? shapeTextures,
        ITexPositionSource contentTexSource)
    {
        _capi = capi;
        _content = content;
        _propTextures = propTextures;
        _shapeTextures = shapeTextures;
        _contentTexSource = contentTexSource;
    }

    public Vintagestory.API.MathTools.Size2i AtlasSize => _capi.BlockTextureAtlas.Size;

    public TextureAtlasPosition? this[string textureCode]
    {
        get
        {
            AssetLocation? textureLoc = null;
            if (_propTextures != null && _propTextures.TryGetValue(textureCode, out CompositeTexture? compTex))
            {
                textureLoc = compTex.Base;
            }

            if (textureLoc == null && _shapeTextures != null)
            {
                _shapeTextures.TryGetValue(textureCode, out textureLoc);
            }

            if (textureLoc != null)
            {
                TextureAtlasPosition? texPos = _capi.BlockTextureAtlas[textureLoc];
                if (texPos == null)
                {
                    BitmapRef? bmp = _capi.Assets.TryGet(textureLoc.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"))?.ToBitmap(_capi);
                    if (bmp != null)
                    {
                        _capi.BlockTextureAtlas.GetOrInsertTexture(textureLoc, out _, out texPos, () => bmp);
                        bmp.Dispose();
                    }
                }

                return texPos;
            }

            if (_content.Class == EnumItemClass.Item)
            {
                textureLoc = _content.Item.Textures[textureCode].Base;
                BitmapRef? bmp = _capi.Assets.TryGet(textureLoc.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"))?.ToBitmap(_capi);
                if (bmp != null)
                {
                    _capi.BlockTextureAtlas.GetOrInsertTexture(textureLoc, out _, out TextureAtlasPosition texPos, () => bmp);
                    bmp.Dispose();
                    return texPos;
                }
            }

            return _contentTexSource[textureCode];
        }
    }
}
