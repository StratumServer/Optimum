using System;
using System.Collections.Generic;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Vintagestory.GameContent;

/// <summary>
/// Generates LOD tiers (2x and 4x downsampled) from full-res page pixels
/// and manages a priority queue for page loading (near-viewport first).
///
/// LOD 0 = full resolution (256x256)
/// LOD 1 = 2x downsampled (128x128, covers 2x2 = 4 LOD-0 pages)
/// LOD 2 = 4x downsampled (64x64, covers 4x4 = 16 LOD-0 pages)
///
/// For zoom-out views, the renderer samples LOD 1 or LOD 2 pages that cover
/// larger world areas with fewer texture array layers.
/// </summary>
public static class OptimumMapLodGenerator
{
    /// <summary>
    /// Downsample a full-res page (256x256) by 2x to produce a 128x128 image.
    /// Uses box-filter averaging (2x2 pixel blocks -> 1 pixel).
    /// </summary>
    public static int[] Downsample2x(int[] fullRes)
    {
        if (fullRes == null || fullRes.Length != 256 * 256) return null;

        int[] lod1 = new int[128 * 128];
        for (int y = 0; y < 128; y++)
        {
            for (int x = 0; x < 128; x++)
            {
                int srcX = x * 2;
                int srcY = y * 2;

                int p00 = fullRes[srcY * 256 + srcX];
                int p10 = fullRes[srcY * 256 + srcX + 1];
                int p01 = fullRes[(srcY + 1) * 256 + srcX];
                int p11 = fullRes[(srcY + 1) * 256 + srcX + 1];

                lod1[y * 128 + x] = AveragePixels(p00, p10, p01, p11);
            }
        }
        return lod1;
    }

    /// <summary>
    /// Downsample a full-res page (256x256) by 4x to produce a 64x64 image.
    /// Uses box-filter averaging (4x4 pixel blocks -> 1 pixel).
    /// </summary>
    public static int[] Downsample4x(int[] fullRes)
    {
        if (fullRes == null || fullRes.Length != 256 * 256) return null;

        int[] lod2 = new int[64 * 64];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int srcX = x * 4;
                int srcY = y * 4;

                // Average 4x4 block
                int sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                for (int dy = 0; dy < 4; dy++)
                {
                    for (int dx = 0; dx < 4; dx++)
                    {
                        int p = fullRes[(srcY + dy) * 256 + srcX + dx];
                        sumR += p & 0xFF;
                        sumG += (p >> 8) & 0xFF;
                        sumB += (p >> 16) & 0xFF;
                        sumA += (p >> 24) & 0xFF;
                    }
                }

                lod2[y * 64 + x] = (sumR / 16)
                    | ((sumG / 16) << 8)
                    | ((sumB / 16) << 16)
                    | ((sumA / 16) << 24);
            }
        }
        return lod2;
    }

    private static int AveragePixels(int p0, int p1, int p2, int p3)
    {
        int r = ((p0 & 0xFF) + (p1 & 0xFF) + (p2 & 0xFF) + (p3 & 0xFF)) / 4;
        int g = (((p0 >> 8) & 0xFF) + ((p1 >> 8) & 0xFF) + ((p2 >> 8) & 0xFF) + ((p3 >> 8) & 0xFF)) / 4;
        int b = (((p0 >> 16) & 0xFF) + ((p1 >> 16) & 0xFF) + ((p2 >> 16) & 0xFF) + ((p3 >> 16) & 0xFF)) / 4;
        int a = (((p0 >> 24) & 0xFF) + ((p1 >> 24) & 0xFF) + ((p2 >> 24) & 0xFF) + ((p3 >> 24) & 0xFF)) / 4;
        return r | (g << 8) | (b << 16) | (a << 24);
    }
}

/// <summary>
/// Thread-safe priority queue for page loading: pages near the viewport center
/// load before distant pages. Uses 64-bit squared distance from the viewport
/// center in page coordinates as the priority key and suppresses duplicate
/// in-flight page keys.
/// </summary>
public sealed class OptimumMapPagePriorityQueue
{
    private readonly object syncLock = new();
    private readonly List<(long pageKey, long priority)> heap = new();
    private readonly HashSet<long> queuedKeys = new();

    public int Count
    {
        get
        {
            lock (syncLock)
            {
                return heap.Count;
            }
        }
    }

    /// <summary>
    /// Enqueue a page with a distance-based priority (lower = higher priority).
    /// Duplicate page keys already present in the queue are ignored.
    /// </summary>
    public void Enqueue(long pageKey, int pageX, int pageZ, int centerPageX, int centerPageZ)
    {
        TryEnqueue(pageKey, pageX, pageZ, centerPageX, centerPageZ);
    }

    /// <summary>
    /// Enqueue a page if it is not already queued. Returns true if inserted.
    /// </summary>
    public bool TryEnqueue(long pageKey, int pageX, int pageZ, int centerPageX, int centerPageZ)
    {
        long dx = (long)pageX - centerPageX;
        long dz = (long)pageZ - centerPageZ;
        long priority = dx * dx + dz * dz;

        lock (syncLock)
        {
            if (!queuedKeys.Add(pageKey))
            {
                return false;
            }

            heap.Add((pageKey, priority));
            BubbleUp(heap.Count - 1);
            return true;
        }
    }

    /// <summary>
    /// Dequeue the highest-priority (nearest) page. Returns -1 when empty.
    /// Prefer <see cref="TryDequeue"/> because packed page (-1, -1) also equals -1L.
    /// </summary>
    public long Dequeue()
    {
        return TryDequeue(out long pageKey) ? pageKey : -1L;
    }

    /// <summary>
    /// Dequeue the highest-priority (nearest) page without sentinel collisions.
    /// </summary>
    public bool TryDequeue(out long pageKey)
    {
        lock (syncLock)
        {
            if (heap.Count == 0)
            {
                pageKey = 0;
                return false;
            }

            pageKey = heap[0].pageKey;
            queuedKeys.Remove(pageKey);
            int lastIdx = heap.Count - 1;
            heap[0] = heap[lastIdx];
            heap.RemoveAt(lastIdx);
            if (heap.Count > 0)
            {
                BubbleDown(0);
            }
            return true;
        }
    }

    public void Clear()
    {
        lock (syncLock)
        {
            heap.Clear();
            queuedKeys.Clear();
        }
    }

    private void BubbleUp(int idx)
    {
        while (idx > 0)
        {
            int parent = (idx - 1) / 2;
            if (heap[idx].priority < heap[parent].priority)
            {
                (heap[idx], heap[parent]) = (heap[parent], heap[idx]);
                idx = parent;
            }
            else break;
        }
    }

    private void BubbleDown(int idx)
    {
        int count = heap.Count;
        while (true)
        {
            int left = 2 * idx + 1;
            int right = 2 * idx + 2;
            int smallest = idx;

            if (left < count && heap[left].priority < heap[smallest].priority)
                smallest = left;
            if (right < count && heap[right].priority < heap[smallest].priority)
                smallest = right;

            if (smallest != idx)
            {
                (heap[idx], heap[smallest]) = (heap[smallest], heap[idx]);
                idx = smallest;
            }
            else break;
        }
    }
}
