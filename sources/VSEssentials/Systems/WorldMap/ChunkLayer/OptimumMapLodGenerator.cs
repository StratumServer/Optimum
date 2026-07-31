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
/// Priority queue for page loading: pages near the viewport center load
/// before distant pages. Uses squared distance from the viewport center
/// in page coordinates as the priority key.
/// </summary>
public sealed class OptimumMapPagePriorityQueue
{
    private readonly List<(long pageKey, int priority)> _heap = new();

    public int Count => _heap.Count;

    /// <summary>
    /// Enqueue a page with a distance-based priority (lower = higher priority).
    /// </summary>
    public void Enqueue(long pageKey, int pageX, int pageZ, int centerPageX, int centerPageZ)
    {
        int dx = pageX - centerPageX;
        int dz = pageZ - centerPageZ;
        int priority = dx * dx + dz * dz;
        _heap.Add((pageKey, priority));
        BubbleUp(_heap.Count - 1);
    }

    /// <summary>
    /// Dequeue the highest-priority (nearest) page.
    /// </summary>
    public long Dequeue()
    {
        if (_heap.Count == 0) return -1;

        long result = _heap[0].pageKey;
        int lastIdx = _heap.Count - 1;
        _heap[0] = _heap[lastIdx];
        _heap.RemoveAt(lastIdx);
        if (_heap.Count > 0) BubbleDown(0);
        return result;
    }

    public void Clear() => _heap.Clear();

    private void BubbleUp(int idx)
    {
        while (idx > 0)
        {
            int parent = (idx - 1) / 2;
            if (_heap[idx].priority < _heap[parent].priority)
            {
                (_heap[idx], _heap[parent]) = (_heap[parent], _heap[idx]);
                idx = parent;
            }
            else break;
        }
    }

    private void BubbleDown(int idx)
    {
        int count = _heap.Count;
        while (true)
        {
            int left = 2 * idx + 1;
            int right = 2 * idx + 2;
            int smallest = idx;

            if (left < count && _heap[left].priority < _heap[smallest].priority)
                smallest = left;
            if (right < count && _heap[right].priority < _heap[smallest].priority)
                smallest = right;

            if (smallest != idx)
            {
                (_heap[idx], _heap[smallest]) = (_heap[smallest], _heap[idx]);
                idx = smallest;
            }
            else break;
        }
    }
}
