using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Vintagestory.API.Config;

namespace Vintagestory.API.Client;

/// <summary>
/// Issue #73 / #612: Per-worker pool for the CustomMeshDataPart&lt;T&gt; buffers
/// allocated when chunk tesselation finalizes meshes via
/// ChunkTesselator.populateTesselatedChunkPart -> OptimumCloneChunkMesh.
///
/// Ownership and multi-worker safety:
/// - Each tessellation worker owns a private WorkerSlotPool via [ThreadStatic],
///   eliminating cross-worker locks on Rent/Clone.
/// - TesselatedChunkPart.AddModelAndStoreLocation calls Reclaim on the main
///   render thread. Pooled arrays track their originating WorkerSlotPool via a
///   ConditionalWeakTable so Reclaim enqueues returns ONLY into the originating
///   worker's bounded MPSC queue (never mutating another thread's free lists).
/// - Return queues are drained solely by the owning worker on Rent or TestDrain.
/// - InterleaveSizes and InterleaveOffsets avoid allocating new int[] arrays when
///   a recycled part already carries matching metadata.
/// </summary>
public static class OptimumMeshPartPool
{
    private const int MaxPerTypePerWorker = 128;

    private static readonly Lock RegistryLock = new();
    private static readonly List<WorkerSlotPool> AllSlots = new();
    private static readonly ConditionalWeakTable<Array, WorkerSlotPool> ArrayOrigins = new();

    [ThreadStatic]
    private static WorkerSlotPool? t_slot;

    private sealed class WorkerSlotPool
    {
        private long _pad0, _pad1, _pad2, _pad3, _pad4, _pad5, _pad6, _pad7;
        public readonly List<CustomMeshDataPartInt> FreeInt = new(32);
        public readonly List<CustomMeshDataPartShort> FreeShort = new(32);
        public readonly List<CustomMeshDataPartFloat> FreeFloat = new(32);
        public readonly List<CustomMeshDataPartByte> FreeByte = new(32);

        public readonly ConcurrentQueue<CustomMeshDataPartInt> ReclaimInt = new();
        public readonly ConcurrentQueue<CustomMeshDataPartShort> ReclaimShort = new();
        public readonly ConcurrentQueue<CustomMeshDataPartFloat> ReclaimFloat = new();
        public readonly ConcurrentQueue<CustomMeshDataPartByte> ReclaimByte = new();

        private int _reclaimIntCount;
        private int _reclaimShortCount;
        private int _reclaimFloatCount;
        private int _reclaimByteCount;

        public WorkerSlotPool()
        {
            _ = _pad0 + _pad1 + _pad2 + _pad3 + _pad4 + _pad5 + _pad6 + _pad7;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DrainReturns()
        {
            while (ReclaimInt.TryDequeue(out var p))
            {
                Interlocked.Decrement(ref _reclaimIntCount);
                if (FreeInt.Count < MaxPerTypePerWorker) FreeInt.Add(p);
            }
            while (ReclaimShort.TryDequeue(out var p))
            {
                Interlocked.Decrement(ref _reclaimShortCount);
                if (FreeShort.Count < MaxPerTypePerWorker) FreeShort.Add(p);
            }
            while (ReclaimFloat.TryDequeue(out var p))
            {
                Interlocked.Decrement(ref _reclaimFloatCount);
                if (FreeFloat.Count < MaxPerTypePerWorker) FreeFloat.Add(p);
            }
            while (ReclaimByte.TryDequeue(out var p))
            {
                Interlocked.Decrement(ref _reclaimByteCount);
                if (FreeByte.Count < MaxPerTypePerWorker) FreeByte.Add(p);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnqueueInt(CustomMeshDataPartInt part)
        {
            if (FreeInt.Count + _reclaimIntCount >= MaxPerTypePerWorker) return;
            if (Interlocked.Increment(ref _reclaimIntCount) <= MaxPerTypePerWorker)
            {
                ReclaimInt.Enqueue(part);
            }
            else
            {
                Interlocked.Decrement(ref _reclaimIntCount);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnqueueShort(CustomMeshDataPartShort part)
        {
            if (FreeShort.Count + _reclaimShortCount >= MaxPerTypePerWorker) return;
            if (Interlocked.Increment(ref _reclaimShortCount) <= MaxPerTypePerWorker)
            {
                ReclaimShort.Enqueue(part);
            }
            else
            {
                Interlocked.Decrement(ref _reclaimShortCount);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnqueueFloat(CustomMeshDataPartFloat part)
        {
            if (FreeFloat.Count + _reclaimFloatCount >= MaxPerTypePerWorker) return;
            if (Interlocked.Increment(ref _reclaimFloatCount) <= MaxPerTypePerWorker)
            {
                ReclaimFloat.Enqueue(part);
            }
            else
            {
                Interlocked.Decrement(ref _reclaimFloatCount);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnqueueByte(CustomMeshDataPartByte part)
        {
            if (FreeByte.Count + _reclaimByteCount >= MaxPerTypePerWorker) return;
            if (Interlocked.Increment(ref _reclaimByteCount) <= MaxPerTypePerWorker)
            {
                ReclaimByte.Enqueue(part);
            }
            else
            {
                Interlocked.Decrement(ref _reclaimByteCount);
            }
        }

        public void ClearAll()
        {
            while (ReclaimInt.TryDequeue(out _)) Interlocked.Decrement(ref _reclaimIntCount);
            while (ReclaimShort.TryDequeue(out _)) Interlocked.Decrement(ref _reclaimShortCount);
            while (ReclaimFloat.TryDequeue(out _)) Interlocked.Decrement(ref _reclaimFloatCount);
            while (ReclaimByte.TryDequeue(out _)) Interlocked.Decrement(ref _reclaimByteCount);
            FreeInt.Clear();
            FreeShort.Clear();
            FreeFloat.Clear();
            FreeByte.Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static WorkerSlotPool GetCurrentSlot()
    {
        WorkerSlotPool? slot = t_slot;
        if (slot != null) return slot;
        lock (RegistryLock)
        {
            slot = t_slot;
            if (slot != null) return slot;
            slot = new WorkerSlotPool();
            t_slot = slot;
            AllSlots.Add(slot);
            return slot;
        }
    }

    /// <summary>
    /// Clears all per-worker pooled buffers and return queues. Intended for
    /// tests and session teardown.
    /// </summary>
    public static void Clear()
    {
        lock (RegistryLock)
        {
            for (int i = 0; i < AllSlots.Count; i++)
            {
                AllSlots[i].ClearAll();
            }
        }
        ArrayOrigins.Clear();
    }

    /// <summary>
    /// Clones a chunk mesh using the calling worker's MeshData.Recycler and
    /// CustomMeshDataPart pool, preserving exact field parity with
    /// MeshData.CloneUsingRecycler().
    /// </summary>
    public static MeshData? CloneChunkMesh(MeshData? src)
    {
        if (src == null) return null;

        int recyclableSizeInBytes = src.VerticesCount * 28;
        if (recyclableSizeInBytes < MeshDataRecycler.MinimumSizeForRecycling ||
            src.Uv == null || src.Rgba == null || src.Flags == null ||
            src.VerticesPerFace != 4 ||
            src.IndicesPerFace != 6)
        {
            return null;
        }

        int requiredSize = Math.Max(src.VerticesCount, ((src.IndicesCount + 5) / 6) * 4);
        if (requiredSize > src.VerticesCount * 1.05f || requiredSize * 6 / 4 > src.IndicesCount * 1.2f)
        {
            return null;
        }

        WorkerSlotPool slot = GetCurrentSlot();
        slot.DrainReturns();

        MeshData dst = MeshData.Recycler.GetOrCreateMesh(requiredSize);

        dst.SetVerticesCount(src.VerticesCount);
        dst.SetIndicesCount(src.IndicesCount);
        Array.Copy(src.xyz, dst.xyz, src.XyzCount);
        Array.Copy(src.Uv, dst.Uv, src.UvCount);
        Array.Copy(src.Rgba, dst.Rgba, src.RgbaCount);
        Array.Copy(src.Flags, dst.Flags, src.FlagsCount);
        Array.Copy(src.Indices, dst.Indices, src.IndicesCount);

        if (src.Normals != null)
        {
            dst.Normals = CopyIntArray(src.Normals, src.NormalsCount);
            dst.NormalsCount = src.NormalsCount;
        }
        if (src.XyzFaces != null)
        {
            dst.XyzFaces = CopyByteArray(src.XyzFaces, src.XyzFacesCount);
            dst.XyzFacesCount = src.XyzFacesCount;
        }
        if (src.TextureIndices != null)
        {
            dst.TextureIndices = CopyByteArray(src.TextureIndices, src.TextureIndicesCount);
            dst.TextureIndicesCount = src.TextureIndicesCount;
            dst.TextureIds = src.TextureIds != null ? (int[])src.TextureIds.Clone() : Array.Empty<int>();
        }
        if (src.ClimateColorMapIds != null)
        {
            dst.ClimateColorMapIds = CopyByteArray(src.ClimateColorMapIds, src.ColorMapIdsCount);
            dst.ColorMapIdsCount = src.ColorMapIdsCount;
        }
        if (src.SeasonColorMapIds != null)
        {
            dst.SeasonColorMapIds = CopyByteArray(src.SeasonColorMapIds, src.ColorMapIdsCount);
            dst.ColorMapIdsCount = src.ColorMapIdsCount;
        }
        if (src.RenderPassesAndExtraBits != null)
        {
            dst.RenderPassesAndExtraBits = CopyShortArray(src.RenderPassesAndExtraBits, src.RenderPassCount);
            dst.RenderPassCount = src.RenderPassCount;
        }

        if (src.CustomFloats != null)
        {
            dst.CustomFloats = RentFloat(slot, src.CustomFloats);
        }
        if (src.CustomShorts != null)
        {
            dst.CustomShorts = RentShort(slot, src.CustomShorts);
        }
        if (src.CustomBytes != null)
        {
            dst.CustomBytes = RentByte(slot, src.CustomBytes);
        }
        if (src.CustomInts != null)
        {
            dst.CustomInts = RentInt(slot, src.CustomInts);
        }

        return dst;
    }

    /// <summary>
    /// Detaches and returns CustomMeshDataPart buffers from an uploaded chunk mesh
    /// to the originating worker's bounded MPSC queue before MeshData.Dispose().
    /// </summary>
    public static void Reclaim(MeshData? mesh)
    {
        if (mesh == null) return;

        if (mesh.CustomInts != null)
        {
            var p = mesh.CustomInts;
            mesh.CustomInts = null;
            if (IsPoolablePart(p))
            {
                EnqueueToOrigin(p, p.Values, static (slot, part) => slot.EnqueueInt(part));
            }
        }
        if (mesh.CustomShorts != null)
        {
            var p = mesh.CustomShorts;
            mesh.CustomShorts = null;
            if (IsPoolablePart(p))
            {
                EnqueueToOrigin(p, p.Values, static (slot, part) => slot.EnqueueShort(part));
            }
        }
        if (mesh.CustomFloats != null)
        {
            var p = mesh.CustomFloats;
            mesh.CustomFloats = null;
            if (IsPoolablePart(p))
            {
                EnqueueToOrigin(p, p.Values, static (slot, part) => slot.EnqueueFloat(part));
            }
        }
        if (mesh.CustomBytes != null)
        {
            var p = mesh.CustomBytes;
            mesh.CustomBytes = null;
            if (IsPoolablePart(p))
            {
                EnqueueToOrigin(p, p.Values, static (slot, part) => slot.EnqueueByte(part));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnqueueToOrigin<TPart>(TPart part, Array? values, Action<WorkerSlotPool, TPart> enqueue)
    {
        if (values != null && ArrayOrigins.TryGetValue(values, out WorkerSlotPool? origin))
        {
            enqueue(origin, part);
            return;
        }
        enqueue(GetCurrentSlot(), part);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPoolablePart<T>(CustomMeshDataPart<T> part)
    {
        return part.Values != null && part.AllocationSize == part.Count;
    }

    private static CustomMeshDataPartInt RentInt(WorkerSlotPool slot, CustomMeshDataPartInt src)
    {
        if (src.AllocationSize != src.Count)
        {
            OptimumDiagnostics.MeshPartPool.Skip();
            return src.Clone();
        }
        int needed = src.Count;
        var list = slot.FreeInt;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var candidate = list[i];
            if (candidate.Values != null && candidate.Values.Length >= needed)
            {
                int last = list.Count - 1;
                list[i] = list[last];
                list.RemoveAt(last);
                CopyIntInto(src, candidate);
                ArrayOrigins.AddOrUpdate(candidate.Values, slot);
                OptimumDiagnostics.MeshPartPool.Hit();
                return candidate;
            }
        }

        OptimumDiagnostics.MeshPartPool.Skip();
        var fresh = new CustomMeshDataPartInt(src.BufferSize);
        CopyIntInto(src, fresh);
        if (fresh.Values != null) ArrayOrigins.AddOrUpdate(fresh.Values, slot);
        return fresh;
    }

    private static CustomMeshDataPartShort RentShort(WorkerSlotPool slot, CustomMeshDataPartShort src)
    {
        if (src.AllocationSize != src.Count)
        {
            OptimumDiagnostics.MeshPartPool.Skip();
            return src.Clone();
        }
        int needed = src.Count;
        var list = slot.FreeShort;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var candidate = list[i];
            if (candidate.Values != null && candidate.Values.Length >= needed)
            {
                int last = list.Count - 1;
                list[i] = list[last];
                list.RemoveAt(last);
                CopyShortInto(src, candidate);
                ArrayOrigins.AddOrUpdate(candidate.Values, slot);
                OptimumDiagnostics.MeshPartPool.Hit();
                return candidate;
            }
        }

        OptimumDiagnostics.MeshPartPool.Skip();
        var fresh = new CustomMeshDataPartShort(src.BufferSize);
        CopyShortInto(src, fresh);
        if (fresh.Values != null) ArrayOrigins.AddOrUpdate(fresh.Values, slot);
        return fresh;
    }

    private static CustomMeshDataPartFloat RentFloat(WorkerSlotPool slot, CustomMeshDataPartFloat src)
    {
        if (src.AllocationSize != src.Count)
        {
            OptimumDiagnostics.MeshPartPool.Skip();
            return src.Clone();
        }
        int needed = src.Count;
        var list = slot.FreeFloat;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var candidate = list[i];
            if (candidate.Values != null && candidate.Values.Length >= needed)
            {
                int last = list.Count - 1;
                list[i] = list[last];
                list.RemoveAt(last);
                CopyFloatInto(src, candidate);
                ArrayOrigins.AddOrUpdate(candidate.Values, slot);
                OptimumDiagnostics.MeshPartPool.Hit();
                return candidate;
            }
        }

        OptimumDiagnostics.MeshPartPool.Skip();
        var fresh = new CustomMeshDataPartFloat(src.BufferSize);
        CopyFloatInto(src, fresh);
        if (fresh.Values != null) ArrayOrigins.AddOrUpdate(fresh.Values, slot);
        return fresh;
    }

    private static CustomMeshDataPartByte RentByte(WorkerSlotPool slot, CustomMeshDataPartByte src)
    {
        if (src.AllocationSize != src.Count)
        {
            OptimumDiagnostics.MeshPartPool.Skip();
            return src.Clone();
        }
        int needed = src.Count;
        var list = slot.FreeByte;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var candidate = list[i];
            if (candidate.Values != null && candidate.Values.Length >= needed)
            {
                int last = list.Count - 1;
                list[i] = list[last];
                list.RemoveAt(last);
                CopyByteInto(src, candidate);
                ArrayOrigins.AddOrUpdate(candidate.Values, slot);
                OptimumDiagnostics.MeshPartPool.Hit();
                return candidate;
            }
        }

        OptimumDiagnostics.MeshPartPool.Skip();
        var fresh = new CustomMeshDataPartByte(src.BufferSize);
        CopyByteInto(src, fresh);
        if (fresh.Values != null) ArrayOrigins.AddOrUpdate(fresh.Values, slot);
        return fresh;
    }

    private static void CopyIntInto(CustomMeshDataPartInt src, CustomMeshDataPartInt dst)
    {
        CopyCommonMetadata(src, dst);
        dst.Conversion = src.Conversion;
        if (src.Count > 0 && src.Values != null)
        {
            Array.Copy(src.Values, 0, dst.Values, 0, src.Count);
        }
    }

    private static void CopyShortInto(CustomMeshDataPartShort src, CustomMeshDataPartShort dst)
    {
        CopyCommonMetadata(src, dst);
        dst.Conversion = src.Conversion;
        if (src.Count > 0 && src.Values != null)
        {
            Array.Copy(src.Values, 0, dst.Values, 0, src.Count);
        }
    }

    private static void CopyFloatInto(CustomMeshDataPartFloat src, CustomMeshDataPartFloat dst)
    {
        CopyCommonMetadata(src, dst);
        if (src.Count > 0 && src.Values != null)
        {
            Array.Copy(src.Values, 0, dst.Values, 0, src.Count);
        }
    }

    private static void CopyByteInto(CustomMeshDataPartByte src, CustomMeshDataPartByte dst)
    {
        CopyCommonMetadata(src, dst);
        dst.Conversion = src.Conversion;
        if (src.Count > 0 && src.Values != null)
        {
            Array.Copy(src.Values, 0, dst.Values, 0, src.Count);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyCommonMetadata<T>(CustomMeshDataPart<T> src, CustomMeshDataPart<T> dst)
    {
        dst.Count = src.Count;
        dst.InterleaveStride = src.InterleaveStride;
        dst.Instanced = src.Instanced;
        dst.StaticDraw = src.StaticDraw;
        dst.BaseOffset = src.BaseOffset;
        dst.InterleaveSizes = CloneIntArrayIfChanged(dst.InterleaveSizes, src.InterleaveSizes);
        dst.InterleaveOffsets = CloneIntArrayIfChanged(dst.InterleaveOffsets, src.InterleaveOffsets);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int[]? CloneIntArrayIfChanged(int[]? dst, int[]? src)
    {
        if (src == null) return null;
        if (dst != null && dst.Length == src.Length)
        {
            if (src.AsSpan().SequenceEqual(dst.AsSpan())) return dst;
            Array.Copy(src, dst, src.Length);
            return dst;
        }
        return (int[])src.Clone();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int[] CopyIntArray(int[] src, int count)
    {
        var copy = new int[count];
        if (count > 0) Array.Copy(src, 0, copy, 0, count);
        return copy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short[] CopyShortArray(short[] src, int count)
    {
        var copy = new short[count];
        if (count > 0) Array.Copy(src, 0, copy, 0, count);
        return copy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] CopyByteArray(byte[] src, int count)
    {
        var copy = new byte[count];
        if (count > 0) Array.Copy(src, 0, copy, 0, count);
        return copy;
    }

    // ---- Test seams (preserve existing single-thread and new multi-worker test contracts) ----

    public static CustomMeshDataPartInt TestClonePartInt(CustomMeshDataPartInt src)
    {
        WorkerSlotPool slot = GetCurrentSlot();
        slot.DrainReturns();
        return RentInt(slot, src);
    }

    public static void TestReclaimInt(CustomMeshDataPartInt? part)
    {
        if (part != null && IsPoolablePart(part))
        {
            EnqueueToOrigin(part, part.Values, static (slot, p) => slot.EnqueueInt(p));
        }
    }

    public static void TestDrain() => GetCurrentSlot().DrainReturns();

    public static int TestFreeIntCount => GetCurrentSlot().FreeInt.Count;

    public static int TestRegisteredWorkerSlotCount
    {
        get { lock (RegistryLock) { return AllSlots.Count; } }
    }
}
