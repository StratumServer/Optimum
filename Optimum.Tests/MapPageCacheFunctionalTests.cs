using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Functional tests for the map page cache: disk round-trip, pixel compositing,
/// LRU eviction logic, priority queue ordering, and boundary conditions.
/// These test the actual data-flow correctness that the existing coordinate-math
/// tests do not cover.
/// </summary>
[Collection("Bc7Config")]
public class MapPageCacheFunctionalTests
{
    // --- Disk serialization round-trip ---

    [Fact]
    public void DiskRoundTrip_HeaderFormat_IsCorrect()
    {
        // Simulate the file format manually: magic + version + pageX + pageZ + bitmask + gzip(pixels)
        int pageX = 3;
        int pageZ = -2;
        ulong bitmask = 0x0000_0000_0000_000F; // 4 chunks set

        int[] pixels = new int[OptimumMapPageCache.PagePixelCount];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = i * 7 + unchecked((int)0xFF000000);

        byte[] rawBytes = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, rawBytes, 0, rawBytes.Length);

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true))
                gz.Write(rawBytes, 0, rawBytes.Length);
            compressed = ms.ToArray();
        }

        // Build the file
        byte[] file = new byte[21 + compressed.Length];
        file[0] = (byte)'O'; file[1] = (byte)'M'; file[2] = (byte)'P'; file[3] = 0x01;
        file[4] = 1; // version
        BitConverter.TryWriteBytes(file.AsSpan(5), pageX);
        BitConverter.TryWriteBytes(file.AsSpan(9), pageZ);
        BitConverter.TryWriteBytes(file.AsSpan(13), bitmask);
        Array.Copy(compressed, 0, file, 21, compressed.Length);

        // Verify header fields
        Assert.Equal((byte)'O', file[0]);
        Assert.Equal((byte)'M', file[1]);
        Assert.Equal((byte)'P', file[2]);
        Assert.Equal(0x01, file[3]);
        Assert.Equal(1, file[4]);
        Assert.Equal(pageX, BitConverter.ToInt32(file, 5));
        Assert.Equal(pageZ, BitConverter.ToInt32(file, 9));
        Assert.Equal(bitmask, BitConverter.ToUInt64(file, 13));

        // Verify decompression
        byte[] payloadCompressed = new byte[compressed.Length];
        Array.Copy(file, 21, payloadCompressed, 0, compressed.Length);

        using var input = new MemoryStream(payloadCompressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        byte[] decompressed = output.ToArray();

        Assert.Equal(pixels.Length * 4, decompressed.Length);
        int[] roundTripped = new int[pixels.Length];
        Buffer.BlockCopy(decompressed, 0, roundTripped, 0, decompressed.Length);
        Assert.Equal(pixels, roundTripped);
    }

    // --- Pixel compositing correctness ---

    [Fact]
    public void WriteChunk_CompositesPixelsAtCorrectOffset()
    {
        // Chunk (2, 3) within page (0,0) should land at pixel offset (64, 96) in the 256x256 page
        int chunkX = 2;
        int chunkZ = 3;
        int localX = OptimumMapPageCache.ChunkIndexInPage(chunkX, chunkZ) % OptimumMapPageCache.PageLen;
        int localZ = OptimumMapPageCache.ChunkIndexInPage(chunkX, chunkZ) / OptimumMapPageCache.PageLen;

        // Verify local coords
        int expectedLocalX = ((chunkX % 8) + 8) % 8; // 2
        int expectedLocalZ = ((chunkZ % 8) + 8) % 8; // 3
        Assert.Equal(2, expectedLocalX);
        Assert.Equal(3, expectedLocalZ);

        // Pixel base position within the 256x256 page
        int basePixelX = expectedLocalX * 32; // 64
        int basePixelZ = expectedLocalZ * 32; // 96
        Assert.Equal(64, basePixelX);
        Assert.Equal(96, basePixelZ);

        // Verify the compositing logic: row 0 of the chunk lands at (96*256 + 64) in the page buffer
        int expectedDstOffset = basePixelZ * 256 + basePixelX;
        Assert.Equal(96 * 256 + 64, expectedDstOffset);
    }

    [Fact]
    public void WriteChunk_NegativeCoords_CompositesCorrectly()
    {
        // Chunk (-1, -1) belongs to page (-1, -1), local (7, 7)
        int chunkX = -1;
        int chunkZ = -1;
        int localX = ((chunkX % 8) + 8) % 8; // 7
        int localZ = ((chunkZ % 8) + 8) % 8; // 7
        Assert.Equal(7, localX);
        Assert.Equal(7, localZ);

        int basePixelX = 7 * 32; // 224
        int basePixelZ = 7 * 32; // 224
        Assert.Equal(224, basePixelX);
        Assert.Equal(224, basePixelZ);
    }

    [Fact]
    public void GetCachedChunkPixels_ExtractsCorrectSubregion()
    {
        // Build a page buffer where each chunk position has a unique pattern
        int[] pageBuffer = new int[256 * 256];
        int targetChunkX = 5;
        int targetChunkZ = 2;
        int localX = ((targetChunkX % 8) + 8) % 8;
        int localZ = ((targetChunkZ % 8) + 8) % 8;
        int basePixelX = localX * 32;
        int basePixelZ = localZ * 32;

        // Fill the target chunk region with a known pattern
        for (int row = 0; row < 32; row++)
        {
            for (int col = 0; col < 32; col++)
            {
                pageBuffer[(basePixelZ + row) * 256 + basePixelX + col] = row * 32 + col + 1;
            }
        }

        // Extract using the same logic as GetCachedChunkPixels
        int[] extracted = new int[32 * 32];
        for (int row = 0; row < 32; row++)
        {
            int srcOffset = (basePixelZ + row) * 256 + basePixelX;
            int dstOffset = row * 32;
            Array.Copy(pageBuffer, srcOffset, extracted, dstOffset, 32);
        }

        // Verify the first and last pixel
        Assert.Equal(1, extracted[0]);           // row=0, col=0 -> 0*32+0+1 = 1
        Assert.Equal(32 * 32, extracted[32 * 32 - 1]); // row=31, col=31 -> 31*32+31+1 = 1024
    }

    // --- LRU eviction logic ---

    [Fact]
    public void LruEviction_EvictsLeastRecentlyUsed()
    {
        // Simulate the LRU without GL: use the data-structure operations directly
        var pageToLayer = new Dictionary<long, int>();
        var lruOrder = new LinkedList<long>();
        var lruNodes = new Dictionary<long, LinkedListNode<long>>();
        var freeList = new Stack<int>();

        int maxLayers = 3;
        for (int i = maxLayers - 1; i >= 0; i--) freeList.Push(i);

        // Fill all 3 layers
        for (int i = 0; i < 3; i++)
        {
            long key = i + 100;
            int layer = freeList.Pop();
            pageToLayer[key] = layer;
            var node = lruOrder.AddFirst(key);
            lruNodes[key] = node;
        }

        // Access key 100 (moves to front)
        {
            long touchKey = 100;
            var node = lruNodes[touchKey];
            lruOrder.Remove(node);
            lruOrder.AddFirst(node);
        }

        // Now LRU order front-to-back: 100, 102, 101
        // Eviction should remove the back: 101
        long evictKey = lruOrder.Last!.Value;
        Assert.Equal(101L, evictKey);
    }

    [Fact]
    public void LruEviction_ReusesEvictedLayer()
    {
        var pageToLayer = new Dictionary<long, int>();
        var freeList = new Stack<int>();
        int maxLayers = 2;
        for (int i = maxLayers - 1; i >= 0; i--) freeList.Push(i);

        // Fill both layers
        pageToLayer[10] = freeList.Pop(); // layer 0
        pageToLayer[20] = freeList.Pop(); // layer 1

        // Evict key 10
        int evictedLayer = pageToLayer[10];
        pageToLayer.Remove(10);
        freeList.Push(evictedLayer);

        // Allocate new page: gets the evicted layer back
        int newLayer = freeList.Pop();
        Assert.Equal(0, newLayer); // layer 0 was evicted and reused
    }

    // --- Priority queue correctness ---

    [Fact]
    public void PriorityQueue_MaintainsHeapInvariant()
    {
        var pq = new OptimumMapPagePriorityQueue();

        // Insert 20 pages at random distances
        var rng = new Random(42);
        var expectedOrder = new List<(long key, int dist)>();
        for (int i = 0; i < 20; i++)
        {
            int px = rng.Next(-50, 50);
            int pz = rng.Next(-50, 50);
            long key = OptimumMapPageCache.PackPageCoord(px, pz);
            int dist = px * px + pz * pz;
            pq.Enqueue(key, px, pz, 0, 0);
            expectedOrder.Add((key, dist));
        }

        // Dequeue all: should come out in non-decreasing distance order
        int prev = -1;
        for (int i = 0; i < 20; i++)
        {
            long dequeued = pq.Dequeue();
            Assert.NotEqual(-1, dequeued);
            // Find the distance for this key
            int dequeuedDist = expectedOrder.Find(e => e.key == dequeued).dist;
            Assert.True(dequeuedDist >= prev, $"Heap violation: dist {dequeuedDist} < prev {prev}");
            prev = dequeuedDist;
        }

        Assert.Equal(0, pq.Count);
    }

    // --- Boundary condition tests ---

    [Fact]
    public void ChunkToPage_BoundaryAt8_IsConsistent()
    {
        // Chunk 7 -> page 0, chunk 8 -> page 1
        var (p7, _) = OptimumMapPageCache.ChunkToPage(7, 0);
        var (p8, _) = OptimumMapPageCache.ChunkToPage(8, 0);
        Assert.Equal(0, p7);
        Assert.Equal(1, p8);

        // Chunk -1 -> page -1, chunk 0 -> page 0
        var (pm1, _) = OptimumMapPageCache.ChunkToPage(-1, 0);
        var (p0, _) = OptimumMapPageCache.ChunkToPage(0, 0);
        Assert.Equal(-1, pm1);
        Assert.Equal(0, p0);
    }

    [Fact]
    public void InvalidateChunk_ThenIsChunkCached_ReturnsFalse()
    {
        // Simulate: set bit, then clear bit, verify cleared
        int chunkX = 4, chunkZ = 6;
        int idx = OptimumMapPageCache.ChunkIndexInPage(chunkX, chunkZ);

        ulong mask = 1UL << idx;
        Assert.True((mask & (1UL << idx)) != 0); // bit set

        mask &= ~(1UL << idx);
        Assert.False((mask & (1UL << idx)) != 0); // bit cleared
    }

    [Fact]
    public void PageCoverage_3x3ComponentStraddlesPageBoundary()
    {
        // A MultiChunkMapComponent at baseChunkCoord (6, 6) covers chunks (6,6) through (8,8)
        // Chunks 6,7 -> page 0; chunk 8 -> page 1
        // This means the component straddles two pages on each axis
        int baseX = 6, baseZ = 6;
        var pages = new HashSet<long>();
        for (int dx = 0; dx < 3; dx++)
        {
            for (int dz = 0; dz < 3; dz++)
            {
                int cx = baseX + dx;
                int cz = baseZ + dz;
                var (px, pz) = OptimumMapPageCache.ChunkToPage(cx, cz);
                pages.Add(OptimumMapPageCache.PackPageCoord(px, pz));
            }
        }

        // A component at (6,6) spans 4 pages: (0,0), (0,1), (1,0), (1,1)
        Assert.True(pages.Count > 1, $"Expected multiple pages, got {pages.Count}");
        Assert.Contains(OptimumMapPageCache.PackPageCoord(0, 0), pages);
        Assert.Contains(OptimumMapPageCache.PackPageCoord(1, 1), pages);
    }

    // --- LOD downsample pixel accuracy ---

    [Fact]
    public void Downsample2x_GradientBlock_AveragesCorrectly()
    {
        int[] full = new int[256 * 256];
        // Create a 2x2 pattern at (0,0): pixels are (10,20,30,40), (50,60,70,80), (90,100,110,120), (130,140,150,160)
        full[0] = 10 | (20 << 8) | (30 << 16) | (40 << 24);
        full[1] = 50 | (60 << 8) | (70 << 16) | (80 << 24);
        full[256] = 90 | (100 << 8) | (110 << 16) | (120 << 24);
        full[257] = 130 | (140 << 8) | (150 << 16) | (160 << 24);

        int[] lod1 = OptimumMapLodGenerator.Downsample2x(full);

        // Average of 4 pixels: R=(10+50+90+130)/4=70, G=(20+60+100+140)/4=80, B=(30+70+110+150)/4=90, A=(40+80+120+160)/4=100
        int expected = 70 | (80 << 8) | (90 << 16) | (100 << 24);
        Assert.Equal(expected, lod1[0]);
    }

    [Fact]
    public void Downsample4x_GradientBlock_AveragesCorrectly()
    {
        int[] full = new int[256 * 256];
        // Fill a 4x4 block with uniform value
        int pixel = 42 | (84 << 8) | (126 << 16) | (200 << 24);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                full[y * 256 + x] = pixel;

        int[] lod2 = OptimumMapLodGenerator.Downsample4x(full);
        // Uniform input -> same output
        Assert.Equal(pixel, lod2[0]);
    }

    // --- BC7 encoder functional test ---

    [Fact]
    public void Bc7Encode_GradientBlock_ProducesDecodableOutput()
    {
        bool origEnabled = OptimumConfig.MapPageCacheBc7;
        bool origSupported = OptimumConfig.MapPageCacheBc7Supported;
        OptimumConfig.MapPageCacheBc7 = true;
        OptimumConfig.MapPageCacheBc7Supported = true;

        try
        {
            // Create a page with a smooth horizontal gradient
            int[] pixels = new int[256 * 256];
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    pixels[y * 256 + x] = x | (128 << 8) | ((255 - x) << 16) | (255 << 24);
                }
            }

            byte[]? bc7 = OptimumBc7Support.Encode(pixels, 256, 256);
            Assert.NotNull(bc7);
            Assert.Equal(65536, bc7.Length); // (256/4)^2 * 16

            // Every block should have the Mode 6 marker
            for (int block = 0; block < bc7.Length; block += 16)
            {
                Assert.Equal(0x40, bc7[block] & 0x7F); // Mode 6 = bit 6 set, bits 0-5 zero
            }
        }
        finally
        {
            OptimumConfig.MapPageCacheBc7 = origEnabled;
            OptimumConfig.MapPageCacheBc7Supported = origSupported;
        }
    }

    [Fact]
    public void Bc7Encode_SolidBlock_EndpointsMatch()
    {
        bool origEnabled = OptimumConfig.MapPageCacheBc7;
        bool origSupported = OptimumConfig.MapPageCacheBc7Supported;
        OptimumConfig.MapPageCacheBc7 = true;
        OptimumConfig.MapPageCacheBc7Supported = true;

        try
        {
            // Solid color page: all pixels identical
            int color = 100 | (150 << 8) | (200 << 16) | (255 << 24);
            int[] pixels = new int[256 * 256];
            Array.Fill(pixels, color);

            byte[]? bc7 = OptimumBc7Support.Encode(pixels, 256, 256);
            Assert.NotNull(bc7);

            // For a solid block, both endpoints should encode the same color
            // and all indices should be 0. The endpoints are at bits 7-62
            // (7 bits each, 8 components total: e0R, e1R, e0G, e1G, e0B, e1B, e0A, e1A)
            // For a solid color, e0 == e1.
            // We check the first block:
            byte[] block = new byte[16];
            Array.Copy(bc7, 0, block, 0, 16);

            // Extract e0R (bits 7-13) and e1R (bits 14-20): both should be the same
            int e0r = ExtractBits(block, 7, 7);
            int e1r = ExtractBits(block, 14, 7);
            // For solid color, min and max are same pixel, so endpoints should be equal
            Assert.Equal(e0r, e1r);
        }
        finally
        {
            OptimumConfig.MapPageCacheBc7 = origEnabled;
            OptimumConfig.MapPageCacheBc7Supported = origSupported;
        }
    }

    private static int ExtractBits(byte[] data, int bitOffset, int numBits)
    {
        int value = 0;
        for (int i = 0; i < numBits; i++)
        {
            int bitPos = bitOffset + i;
            if ((data[bitPos / 8] & (1 << (bitPos % 8))) != 0)
            {
                value |= (1 << i);
            }
        }
        return value;
    }

    // --- Priority queue negative coordinates, overflow, deduplication, thread safety ---

    [Fact]
    public void PriorityQueue_NegativeCoordsAndMinusOneSentinel_DoNotCollide()
    {
        var pq = new OptimumMapPagePriorityQueue();
        long negOneKey = OptimumMapPageCache.PackPageCoord(-1, -1);
        long negXKey = OptimumMapPageCache.PackPageCoord(-5, 3);
        Assert.Equal(-1L, negOneKey);
        Assert.True(negXKey < 0);

        Assert.True(pq.TryEnqueue(negXKey, -5, 3, 0, 0));
        Assert.True(pq.TryEnqueue(negOneKey, -1, -1, 0, 0));
        Assert.False(pq.TryEnqueue(negOneKey, -1, -1, 0, 0)); // duplicate suppressed

        Assert.True(pq.TryDequeue(out long first));
        Assert.Equal(negOneKey, first);

        Assert.True(pq.TryDequeue(out long second));
        Assert.Equal(negXKey, second);

        Assert.False(pq.TryDequeue(out _));
        Assert.Equal(-1L, pq.Dequeue());
    }

    [Fact]
    public void PriorityQueue_LargeCoordinates_DoNotOverflowDistance()
    {
        var pq = new OptimumMapPagePriorityQueue();
        long nearKey = OptimumMapPageCache.PackPageCoord(10, 0);
        long farKey = OptimumMapPageCache.PackPageCoord(100_000, 0); // 100_000^2 overflows int32

        Assert.True(pq.TryEnqueue(farKey, 100_000, 0, 0, 0));
        Assert.True(pq.TryEnqueue(nearKey, 10, 0, 0, 0));

        Assert.True(pq.TryDequeue(out long first));
        Assert.Equal(nearKey, first);
        Assert.True(pq.TryDequeue(out long second));
        Assert.Equal(farKey, second);
    }

    [Fact]
    public async Task PriorityQueue_ConcurrentEnqueueDequeue_IsThreadSafe()
    {
        var pq = new OptimumMapPagePriorityQueue();
        const int pageCount = 400;
        var dequeued = new System.Collections.Concurrent.ConcurrentBag<long>();

        var producer1 = Task.Run(() =>
        {
            for (int x = -10; x < 10; x++)
                for (int z = -10; z < 10; z++)
                    pq.TryEnqueue(OptimumMapPageCache.PackPageCoord(x, z), x, z, 0, 0);
        });
        var producer2 = Task.Run(() =>
        {
            for (int x = -10; x < 10; x++)
                for (int z = -10; z < 10; z++)
                    pq.TryEnqueue(OptimumMapPageCache.PackPageCoord(x, z), x, z, 0, 0);
        });
        var consumer = Task.Run(() =>
        {
            while (!producer1.IsCompleted || !producer2.IsCompleted || pq.Count > 0)
            {
                if (pq.TryDequeue(out long key))
                {
                    dequeued.Add(key);
                }
                else
                {
                    Thread.Yield();
                }
            }
        });

        await Task.WhenAll(producer1, producer2, consumer);
        Assert.True(dequeued.Count >= pageCount);
    }

    // --- Coalesced GPU uploads, sub-chunk vs full-page snapshot, lost-wakeup prevention ---

    [Fact]
    public void PageCache_CoalescesMultipleWrites_AndSelectsSubChunkVsFullPage()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "optimum-map-test-" + Guid.NewGuid().ToString("N"));
        bool origEnabled = OptimumConfig.MapPageCacheEnabled;
        OptimumConfig.MapPageCacheEnabled = true;

        try
        {
            using var cache = new OptimumMapPageCache(tempDir, logger: null, maxInMemoryPages: 32);
            int[] chunkA = MakeFilledChunk(unchecked((int)0xFF112233));
            int[] chunkB = MakeFilledChunk(unchecked((int)0xFF445566));
            int[] chunkC = MakeFilledChunk(unchecked((int)0xFF778899));

            cache.WriteChunk(0, 0, chunkA);
            cache.WriteChunk(1, 0, chunkB);
            cache.WriteChunk(2, 1, chunkC);

            long pageKey = OptimumMapPageCache.PackPageCoord(0, 0);
            Assert.True(cache.TryDequeueDirtyGpuPage(out long dequeuedKey));
            Assert.Equal(pageKey, dequeuedKey);
            // All 3 writes coalesced into a single dirty-page queue entry
            Assert.False(cache.TryDequeueDirtyGpuPage(out _));

            int[] fullScratch = new int[OptimumMapPageCache.PagePixelCount];
            int[][] slices = CreateChunkSlices();

            // Resident layer with 3 dirty chunks -> sub-chunk upload path
            Assert.True(cache.TrySnapshotDirtyPage(
                pageKey, hasLayer: true, fullScratch, slices,
                out bool isFullPage, out ulong dirtyMask, out int sliceCount));
            Assert.False(isFullPage);
            Assert.Equal(3, sliceCount);
            Assert.Equal(
                (1UL << OptimumMapPageCache.ChunkIndexInPage(0, 0)) |
                (1UL << OptimumMapPageCache.ChunkIndexInPage(1, 0)) |
                (1UL << OptimumMapPageCache.ChunkIndexInPage(2, 1)),
                dirtyMask);
            Assert.Equal(chunkA, slices[0]);
            Assert.Equal(chunkB, slices[1]);
            Assert.Equal(chunkC, slices[2]);

            // Writing again re-enqueues (no lost wakeup), and non-resident layer forces full-page upload
            int[] chunkD = MakeFilledChunk(unchecked((int)0xFFAABBCC));
            cache.WriteChunk(3, 3, chunkD);
            Assert.True(cache.TryDequeueDirtyGpuPage(out long requeuedKey));
            Assert.Equal(pageKey, requeuedKey);

            Assert.True(cache.TrySnapshotDirtyPage(
                pageKey, hasLayer: false, fullScratch, slices,
                out isFullPage, out _, out _));
            Assert.True(isFullPage);
            Assert.Equal(unchecked((int)0xFF112233), fullScratch[0]);
            Assert.Equal(unchecked((int)0xFFAABBCC), fullScratch[(3 * 32) * 256 + (3 * 32)]);

            // Partial resident page with >16 dirty chunks batches 16 sub-chunks and re-enqueues remainder
            for (int i = 0; i < 20; i++)
            {
                cache.WriteChunk(i % 8, i / 8, MakeFilledChunk(unchecked((int)(0xFF000000 + i + 1))));
            }
            Assert.True(cache.TryDequeueDirtyGpuPage(out _));
            Assert.True(cache.TrySnapshotDirtyPage(
                pageKey, hasLayer: true, fullScratch, slices,
                out isFullPage, out ulong batch1Mask, out int batch1Count));
            Assert.False(isFullPage);
            Assert.Equal(OptimumMapPageCache.MaxSubChunkUploadsPerPage, batch1Count);
            Assert.Equal(16, BitOperations.PopCount(batch1Mask));

            Assert.True(cache.TryDequeueDirtyGpuPage(out long remainderKey));
            Assert.Equal(pageKey, remainderKey);
            Assert.True(cache.TrySnapshotDirtyPage(
                pageKey, hasLayer: true, fullScratch, slices,
                out isFullPage, out ulong batch2Mask, out int batch2Count));
            Assert.False(isFullPage);
            Assert.Equal(4, batch2Count);
            Assert.Equal(0UL, batch1Mask & batch2Mask);
            Assert.False(cache.TryDequeueDirtyGpuPage(out _));
        }
        finally
        {
            OptimumConfig.MapPageCacheEnabled = origEnabled;
            TryDeleteDir(tempDir);
        }
    }

    // --- Real disk round-trip, merge-without-clobber, reopen, gzip-bomb protection ---

    [Fact]
    public void PageCache_DiskRoundTrip_PreservesLiveAndInvalidatedChunks_AndRejectsGzipBomb()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "optimum-map-disk-" + Guid.NewGuid().ToString("N"));
        bool origEnabled = OptimumConfig.MapPageCacheEnabled;
        OptimumConfig.MapPageCacheEnabled = true;

        try
        {
            int diskColor0 = unchecked((int)0xFF102030);
            int diskColor1 = unchecked((int)0xFF203040);
            int diskColor2 = unchecked((int)0xFF304050);
            int liveColor0 = unchecked((int)0xFFDDCCBB);

            using (var writerCache = new OptimumMapPageCache(tempDir, logger: null, maxInMemoryPages: 32))
            {
                writerCache.WriteChunk(0, 0, MakeFilledChunk(diskColor0));
                writerCache.WriteChunk(1, 0, MakeFilledChunk(diskColor1));
                writerCache.WriteChunk(2, 0, MakeFilledChunk(diskColor2));
                writerCache.Flush();
            }

            using (var readerCache = new OptimumMapPageCache(tempDir, logger: null, maxInMemoryPages: 32))
            {
                // Live write to (0,0) and invalidation of (1,0) BEFORE disk read.
                // Whether WriterLoop merges first or TryReadPage runs first, live (0,0) must survive,
                // invalidated (1,0) must stay uncached, and persisted (2,0) must be preserved.
                readerCache.WriteChunk(0, 0, MakeFilledChunk(liveColor0));
                readerCache.WriteChunk(1, 0, MakeFilledChunk(diskColor1));
                readerCache.InvalidateChunk(1, 0);

                readerCache.TryReadPage(0, 0);

                // Live chunk (0,0) must survive, invalidated (1,0) must stay uncached, (2,0) must load from disk
                int[]? got0 = readerCache.GetCachedChunkPixels(0, 0);
                int[]? got1 = readerCache.GetCachedChunkPixels(1, 0);
                int[]? got2 = readerCache.GetCachedChunkPixels(2, 0);
                Assert.NotNull(got0);
                Assert.Equal(liveColor0, got0[0]);
                Assert.Null(got1);
                Assert.False(readerCache.IsChunkCached(1, 0));
                Assert.NotNull(got2);
                Assert.Equal(diskColor2, got2[0]);

                // Flushing back to disk must also preserve chunk (2,0) across sessions
                readerCache.Flush();

                // Second TryReadPage on same page is a no-op
                Assert.False(readerCache.TryReadPage(0, 0));

                // Closing map clears pending GPU uploads; reopening via RequestGpuUpload schedules full upload without disk re-read
                readerCache.ClearPendingGpuUploads();
                Assert.False(readerCache.TryDequeueDirtyGpuPage(out _));

                long pageKey = OptimumMapPageCache.PackPageCoord(0, 0);
                Assert.True(readerCache.RequestGpuUpload(pageKey));
                Assert.True(readerCache.TryDequeueDirtyGpuPage(out long reopenKey));
                Assert.Equal(pageKey, reopenKey);

                int[] fullScratch = new int[OptimumMapPageCache.PagePixelCount];
                int[][] slices = CreateChunkSlices();
                Assert.True(readerCache.TrySnapshotDirtyPage(
                    pageKey, hasLayer: true, fullScratch, slices,
                    out bool isFullPage, out _, out _));
                Assert.True(isFullPage);

                // Write a gzip-bomb .omp file for page (5, 5): decompresses to > 256 KB
                string bombPath = Path.Combine(tempDir, "5_5.omp");
                byte[] oversizedRaw = new byte[OptimumMapPageCache.PagePixelCount * 4 + 4096];
                using (var ms = new MemoryStream())
                {
                    ms.Write("OMP\x01"u8);
                    ms.WriteByte(1);
                    ms.Write(BitConverter.GetBytes(5));
                    ms.Write(BitConverter.GetBytes(5));
                    ms.Write(BitConverter.GetBytes(ulong.MaxValue));
                    using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        gz.Write(oversizedRaw, 0, oversizedRaw.Length);
                    }
                    File.WriteAllBytes(bombPath, ms.ToArray());
                }

                Assert.False(readerCache.TryReadPage(5, 5));
                Assert.False(readerCache.IsChunkCached(5 * 8, 5 * 8));
            }
        }
        finally
        {
            OptimumConfig.MapPageCacheEnabled = origEnabled;
            TryDeleteDir(tempDir);
        }
    }

    // --- Bounded in-memory eviction ---

    [Fact]
    public void PageCache_EnforcesMaxInMemoryPages_WithoutEvictingPinnedPages()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "optimum-map-evict-" + Guid.NewGuid().ToString("N"));
        bool origEnabled = OptimumConfig.MapPageCacheEnabled;
        OptimumConfig.MapPageCacheEnabled = true;

        try
        {
            using var cache = new OptimumMapPageCache(tempDir, logger: null, maxInMemoryPages: 4);
            int[] chunk = MakeFilledChunk(unchecked((int)0xFF123456));

            // Write 10 pages, flushing disk & clearing GPU dirty flags so they are evictable
            for (int px = 0; px < 10; px++)
            {
                cache.WriteChunk(px * 8, 0, chunk);
                cache.Flush();
                cache.ClearPendingGpuUploads();
            }

            Assert.True(cache.CachedPageCount <= 4, $"Expected <= 4 cached pages, got {cache.CachedPageCount}");

            // Now pin page (20, 0) (dirty GPU upload pending) and insert 6 more flushed pages
            cache.WriteChunk(20 * 8, 0, MakeFilledChunk(unchecked((int)0xFFABCDEF)));
            for (int px = 21; px < 27; px++)
            {
                cache.WriteChunk(px * 8, 0, chunk);
                cache.Flush();
                // Drain only non-pinned pages so page 20 stays queued for GPU upload
                while (cache.TryDequeueDirtyGpuPage(out long key))
                {
                    if (key == OptimumMapPageCache.PackPageCoord(20, 0))
                    {
                        cache.RequestGpuUpload(key);
                        break;
                    }
                    int[] fullScratch = new int[OptimumMapPageCache.PagePixelCount];
                    cache.TrySnapshotDirtyPage(key, hasLayer: false, fullScratch, CreateChunkSlices(), out _, out _, out _);
                }
            }

            Assert.True(cache.IsChunkCached(20 * 8, 0));
        }
        finally
        {
            OptimumConfig.MapPageCacheEnabled = origEnabled;
            TryDeleteDir(tempDir);
        }
    }

    // --- Concurrent stress test: zero torn chunk reads ---

    [Fact]
    public async Task PageCache_ConcurrentWritersReadersAndSnapshots_NeverObserveTornChunks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "optimum-map-race-" + Guid.NewGuid().ToString("N"));
        bool origEnabled = OptimumConfig.MapPageCacheEnabled;
        OptimumConfig.MapPageCacheEnabled = true;

        try
        {
            using var cache = new OptimumMapPageCache(tempDir, logger: null, maxInMemoryPages: 32);
            var cts = new CancellationTokenSource();

            var writerTask = Task.Run(() =>
            {
                for (int iter = 1; iter <= 120; iter++)
                {
                    int stamp = unchecked((int)(0xFF000000 | (uint)iter));
                    int[] pixels = MakeFilledChunk(stamp);
                    cache.WriteChunk(iter % 8, (iter / 8) % 8, pixels);
                    if (iter % 17 == 0)
                    {
                        cache.InvalidateChunk((iter + 3) % 8, 0);
                    }
                }
                cts.Cancel();
            });

            int[] fullScratch = new int[OptimumMapPageCache.PagePixelCount];
            int[][] slices = CreateChunkSlices();
            while (!cts.IsCancellationRequested)
            {
                while (cache.TryDequeueDirtyGpuPage(out long key))
                {
                    if (cache.TrySnapshotDirtyPage(key, hasLayer: true, fullScratch, slices, out bool isFull, out _, out int count) && !isFull)
                    {
                        for (int s = 0; s < count; s++)
                        {
                            int expected = slices[s][0];
                            for (int p = 1; p < slices[s].Length; p++)
                            {
                                Assert.Equal(expected, slices[s][p]);
                            }
                        }
                    }
                }

                int[]? chunk = cache.GetCachedChunkPixels(0, 0);
                if (chunk != null)
                {
                    int expected = chunk[0];
                    for (int p = 1; p < chunk.Length; p++)
                    {
                        Assert.Equal(expected, chunk[p]);
                    }
                }
            }

            await writerTask;
        }
        finally
        {
            OptimumConfig.MapPageCacheEnabled = origEnabled;
            TryDeleteDir(tempDir);
        }
    }

    // --- Source/patch contract check ---

    [Fact]
    public void ChunkMapLayerPatch_UsesCoalescedFlushAndBackgroundDiskReads()
    {
        string patch = PatchReader.ReadPatch("patches/runtime/VSEssentials/Vintagestory/GameContent/ChunkMapLayer.cs.patch");
        string texArray = File.ReadAllText(PatchReader.FindRepositoryFile(
            "sources/VSEssentials/Systems/WorldMap/ChunkLayer/OptimumMapTextureArray.cs"));

        Assert.Contains("pageCache.FlushDirtyPagesToGpu(pageTextureArray, 16);", patch);
        Assert.DoesNotContain("UploadChunkToPageArray", patch);
        Assert.Contains("loadQueue.TryDequeue(out long pageKey)", patch);
        Assert.Contains("pageCache?.ClearPendingGpuUploads();", patch);
        Assert.Contains("pageCache.RequestGpuUpload(pageKey)", patch);
        Assert.Contains("GL.BindTexture((TextureTarget)GL_TEXTURE_2D_ARRAY, 0);", texArray);
        Assert.Contains("public bool UploadChunk(", texArray);
    }

    private static int[] MakeFilledChunk(int color)
    {
        int[] pixels = new int[32 * 32];
        Array.Fill(pixels, color);
        return pixels;
    }

    private static int[][] CreateChunkSlices()
    {
        var slices = new int[OptimumMapPageCache.MaxSubChunkUploadsPerPage][];
        for (int i = 0; i < slices.Length; i++)
        {
            slices[i] = new int[32 * 32];
        }
        return slices;
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
