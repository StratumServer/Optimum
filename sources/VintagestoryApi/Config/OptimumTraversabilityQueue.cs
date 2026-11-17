using System;
using System.Runtime.CompilerServices;

namespace Vintagestory.API.Config;

/// <summary>
/// Worker-owned nearest-first binary min-heap for chunk traversability regeneration.
///
/// The game maintains chunkPositionsForRegenTrav (HashSet) for deduplication and count checks under
/// chunkPositionsLock. Keeping this heap exclusively on the chunkvis thread lets the worker swap
/// newly queued positions in O(1) under lock, then prioritize by squared chunk distance to the
/// camera and reheap on camera movement outside all locks.
/// </summary>
public sealed class OptimumTraversabilityQueue
{
    /// <summary>Time budget per background tick so queue drains accelerate without starving peer systems.</summary>
    public const double BatchBudgetMs = 2.0;

    /// <summary>Minimum Manhattan chunk movement before re-prioritizing queued chunks around the camera.</summary>
    public const int ReheapMoveThresholdChunks = 2;

    /// <summary>Maximum entries removed from the shared deduplication set per lock acquisition.</summary>
    public const int PopBatchSize = 4;

    public struct Entry
    {
        public long DistSq;
        public int X;
        public int Y;
        public int Z;
        public int Dimension;
    }

    private Entry[] items = new Entry[256];
    private int count;
    private int lastCamX;
    private int lastCamY;
    private int lastCamZ;
    private bool hasCam;

    public int Count => count;

    public void Clear()
    {
        count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long DistanceSq(int cx, int cy, int cz, int camX, int camY, int camZ)
    {
        long dx = cx - camX;
        long dy = cy - camY;
        long dz = cz - camZ;
        return dx * dx + dy * dy + dz * dz;
    }

    public void Push(int cx, int cy, int cz, int dimension, int camX, int camY, int camZ)
    {
        if (!hasCam)
        {
            lastCamX = camX;
            lastCamY = camY;
            lastCamZ = camZ;
            hasCam = true;
        }

        EnsureCapacity(count + 1);
        int idx = count++;
        items[idx] = new Entry
        {
            DistSq = DistanceSq(cx, cy, cz, camX, camY, camZ),
            X = cx,
            Y = cy,
            Z = cz,
            Dimension = dimension,
        };
        SiftUp(idx);
    }

    public void BeginBulkReset(int expectedCount, int camX, int camY, int camZ)
    {
        count = 0;
        lastCamX = camX;
        lastCamY = camY;
        lastCamZ = camZ;
        hasCam = true;
        EnsureCapacity(expectedCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushUnordered(int cx, int cy, int cz, int dimension)
    {
        EnsureCapacity(count + 1);
        items[count++] = new Entry
        {
            DistSq = DistanceSq(cx, cy, cz, lastCamX, lastCamY, lastCamZ),
            X = cx,
            Y = cy,
            Z = cz,
            Dimension = dimension,
        };
    }

    public void FinishBulkReset()
    {
        for (int i = (count >> 1) - 1; i >= 0; i--)
        {
            SiftDown(i);
        }
    }

    public bool ReheapIfCameraMoved(int camX, int camY, int camZ)
    {
        if (count == 0)
        {
            lastCamX = camX;
            lastCamY = camY;
            lastCamZ = camZ;
            hasCam = true;
            return false;
        }

        int manhattan = Math.Abs(camX - lastCamX) + Math.Abs(camY - lastCamY) + Math.Abs(camZ - lastCamZ);
        if (hasCam && manhattan < ReheapMoveThresholdChunks)
        {
            return false;
        }

        lastCamX = camX;
        lastCamY = camY;
        lastCamZ = camZ;
        hasCam = true;

        for (int i = 0; i < count; i++)
        {
            ref Entry entry = ref items[i];
            entry.DistSq = DistanceSq(entry.X, entry.Y, entry.Z, camX, camY, camZ);
        }

        for (int i = (count >> 1) - 1; i >= 0; i--)
        {
            SiftDown(i);
        }

        return true;
    }

    public bool TryPop(out Entry entry)
    {
        if (count == 0)
        {
            entry = default;
            return false;
        }

        entry = items[0];
        int last = --count;
        if (last > 0)
        {
            items[0] = items[last];
            SiftDown(0);
        }
        return true;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= items.Length)
        {
            return;
        }

        int next = items.Length;
        while (next < needed)
        {
            next *= 2;
        }
        Array.Resize(ref items, next);
    }

    private void SiftUp(int idx)
    {
        Entry item = items[idx];
        while (idx > 0)
        {
            int parent = (idx - 1) >> 1;
            if (items[parent].DistSq <= item.DistSq)
            {
                break;
            }
            items[idx] = items[parent];
            idx = parent;
        }
        items[idx] = item;
    }

    private void SiftDown(int idx)
    {
        Entry item = items[idx];
        int half = count >> 1;
        while (idx < half)
        {
            int left = (idx << 1) + 1;
            int right = left + 1;
            int smallest = (right < count && items[right].DistSq < items[left].DistSq) ? right : left;
            if (items[smallest].DistSq >= item.DistSq)
            {
                break;
            }
            items[idx] = items[smallest];
            idx = smallest;
        }
        items[idx] = item;
    }
}
