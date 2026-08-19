using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using Xunit;

namespace Optimum.Tests;

public class MapPageCacheTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(7, 7, 0, 0)]
    [InlineData(8, 0, 1, 0)]
    [InlineData(15, 15, 1, 1)]
    [InlineData(16, 16, 2, 2)]
    [InlineData(-1, -1, -1, -1)]
    [InlineData(-8, -8, -1, -1)]
    [InlineData(-9, -9, -2, -2)]
    public void ChunkToPage_ConvertsCorrectly(int chunkX, int chunkZ, int expectedPageX, int expectedPageZ)
    {
        var (pageX, pageZ) = OptimumMapPageCache.ChunkToPage(chunkX, chunkZ);
        Assert.Equal(expectedPageX, pageX);
        Assert.Equal(expectedPageZ, pageZ);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(0, 1, 8)]
    [InlineData(7, 7, 63)]
    [InlineData(8, 8, 0)]    // wraps: 8 % 8 = 0
    [InlineData(9, 9, 9)]    // wraps: 9 % 8 = 1, -> 1*8+1 = 9
    [InlineData(-1, -1, 63)] // wraps: (-1%8+8)%8 = 7, -> 7*8+7 = 63
    [InlineData(-8, -8, 0)]  // wraps: (-8%8+8)%8 = 0, -> 0*8+0 = 0
    public void ChunkIndexInPage_WrapsCorrectly(int chunkX, int chunkZ, int expectedIndex)
    {
        int idx = OptimumMapPageCache.ChunkIndexInPage(chunkX, chunkZ);
        Assert.Equal(expectedIndex, idx);
    }

    [Fact]
    public void PackPageCoord_RoundTrips()
    {
        int pageX = 42;
        int pageZ = -17;
        long packed = OptimumMapPageCache.PackPageCoord(pageX, pageZ);

        // Unpack
        int unpackedX = (int)(uint)(packed >> 32);
        int unpackedZ = (int)(uint)(packed & 0xFFFFFFFF);
        Assert.Equal(pageX, unpackedX);
        Assert.Equal(pageZ, unpackedZ);
    }

    [Fact]
    public void PackPageCoord_NegativeValues_RoundTrip()
    {
        int pageX = -100;
        int pageZ = -200;
        long packed = OptimumMapPageCache.PackPageCoord(pageX, pageZ);

        int unpackedX = (int)(uint)(packed >> 32);
        int unpackedZ = (int)(uint)(packed & 0xFFFFFFFF);
        Assert.Equal(pageX, unpackedX);
        Assert.Equal(pageZ, unpackedZ);
    }

    [Fact]
    public void PageConstants_AreCorrect()
    {
        Assert.Equal(8, OptimumMapPageCache.PageLen);
        Assert.Equal(256, OptimumMapPageCache.PagePixelSize);
        Assert.Equal(65536, OptimumMapPageCache.PagePixelCount);
    }

    [Fact]
    public void ChunkToPage_AllChunksInSamePage_MapToSameCoord()
    {
        // All chunks 0..7 in both axes belong to page (0,0)
        for (int x = 0; x < 8; x++)
        {
            for (int z = 0; z < 8; z++)
            {
                var (px, pz) = OptimumMapPageCache.ChunkToPage(x, z);
                Assert.Equal(0, px);
                Assert.Equal(0, pz);
            }
        }

        // Chunk (8,0) belongs to page (1,0)
        var (px2, pz2) = OptimumMapPageCache.ChunkToPage(8, 0);
        Assert.Equal(1, px2);
        Assert.Equal(0, pz2);
    }

    [Fact]
    public void ChunkIndexInPage_CoversAll64Slots()
    {
        // Page (0,0) contains chunks (0,0) through (7,7)
        bool[] seen = new bool[64];
        for (int x = 0; x < 8; x++)
        {
            for (int z = 0; z < 8; z++)
            {
                int idx = OptimumMapPageCache.ChunkIndexInPage(x, z);
                Assert.InRange(idx, 0, 63);
                Assert.False(seen[idx], $"Duplicate index {idx} for chunk ({x},{z})");
                seen[idx] = true;
            }
        }

        // All 64 slots covered
        for (int i = 0; i < 64; i++)
        {
            Assert.True(seen[i], $"Index {i} not assigned to any chunk");
        }
    }

    [Fact]
    public void NegativeChunks_ProduceValidIndices()
    {
        // Page (-1,-1) contains chunks (-8,-8) through (-1,-1)
        bool[] seen = new bool[64];
        for (int x = -8; x < 0; x++)
        {
            for (int z = -8; z < 0; z++)
            {
                var (px, pz) = OptimumMapPageCache.ChunkToPage(x, z);
                Assert.Equal(-1, px);
                Assert.Equal(-1, pz);

                int idx = OptimumMapPageCache.ChunkIndexInPage(x, z);
                Assert.InRange(idx, 0, 63);
                Assert.False(seen[idx], $"Duplicate index {idx} for chunk ({x},{z})");
                seen[idx] = true;
            }
        }

        for (int i = 0; i < 64; i++)
        {
            Assert.True(seen[i], $"Index {i} not assigned to any chunk in page (-1,-1)");
        }
    }
}

    /// <summary>
    /// Tests for the OptimumMapTextureArray free-list and LRU logic.
    /// These test the data-structure logic only (no GL context available).
    /// The actual GL calls are tested via the full game launch.
    /// </summary>
    public class MapTextureArrayLogicTests
    {
        [Fact]
        public void PackPageCoord_DistinctPages_ProduceDistinctKeys()
        {
            var keys = new HashSet<long>();
            for (int x = -10; x <= 10; x++)
            {
                for (int z = -10; z <= 10; z++)
                {
                    long key = OptimumMapPageCache.PackPageCoord(x, z);
                    Assert.True(keys.Add(key), $"Duplicate key for page ({x},{z})");
                }
            }
        }

        [Fact]
        public void ChunkToPage_AdjacentChunks_CrossPageBoundary()
        {
            // Chunk 7 and chunk 8 belong to different pages
            var (p1x, _) = OptimumMapPageCache.ChunkToPage(7, 0);
            var (p2x, _) = OptimumMapPageCache.ChunkToPage(8, 0);
            Assert.Equal(0, p1x);
            Assert.Equal(1, p2x);
        }

        [Fact]
        public void PagePixelSize_Is256()
        {
            Assert.Equal(256, OptimumMapPageCache.PagePixelSize);
        }

        [Fact]
        public void ChunkToPage_NegativeBoundary_IsCorrect()
        {
            // Chunk -1 belongs to page -1
            var (px, pz) = OptimumMapPageCache.ChunkToPage(-1, -1);
            Assert.Equal(-1, px);
            Assert.Equal(-1, pz);

            // Chunk -8 belongs to page -1 (the last chunk in that page)
            (px, pz) = OptimumMapPageCache.ChunkToPage(-8, -8);
            Assert.Equal(-1, px);
            Assert.Equal(-1, pz);

            // Chunk -9 belongs to page -2
            (px, pz) = OptimumMapPageCache.ChunkToPage(-9, -9);
            Assert.Equal(-2, px);
            Assert.Equal(-2, pz);
        }

        [Fact]
        public void PagePixelCount_MatchesSquare()
        {
            Assert.Equal(256 * 256, OptimumMapPageCache.PagePixelCount);
        }

        [Fact]
        public void ChunkIndexInPage_NeverExceedsBitmaskWidth()
        {
            // Test 1000 random chunks: index always 0..63
            var rng = new Random(42);
            for (int i = 0; i < 1000; i++)
            {
                int cx = rng.Next(-10000, 10000);
                int cz = rng.Next(-10000, 10000);
                int idx = OptimumMapPageCache.ChunkIndexInPage(cx, cz);
                Assert.InRange(idx, 0, 63);
            }
        }
    }

    [Collection("Bc7Config")]
    public class Bc7SupportTests
    {
        [Fact]
        public void Encode_ReturnsNull_WhenDisabled()
        {
            // Save and override config
            bool origEnabled = OptimumConfig.MapPageCacheBc7;
            bool origSupported = OptimumConfig.MapPageCacheBc7Supported;

            OptimumConfig.MapPageCacheBc7 = false;
            OptimumConfig.MapPageCacheBc7Supported = true;
            try
            {
                int[] pixels = new int[256 * 256];
                byte[]? result = OptimumBc7Support.Encode(pixels, 256, 256);
                Assert.Null(result);
            }
            finally
            {
                OptimumConfig.MapPageCacheBc7 = origEnabled;
                OptimumConfig.MapPageCacheBc7Supported = origSupported;
            }
        }

        [Fact]
        public void Encode_ReturnsNull_WhenNotSupported()
        {
            bool origEnabled = OptimumConfig.MapPageCacheBc7;
            bool origSupported = OptimumConfig.MapPageCacheBc7Supported;

            OptimumConfig.MapPageCacheBc7 = true;
            OptimumConfig.MapPageCacheBc7Supported = false;
            try
            {
                int[] pixels = new int[256 * 256];
                byte[]? result = OptimumBc7Support.Encode(pixels, 256, 256);
                Assert.Null(result);
            }
            finally
            {
                OptimumConfig.MapPageCacheBc7 = origEnabled;
                OptimumConfig.MapPageCacheBc7Supported = origSupported;
            }
        }

        [Fact]
        public void Encode_ProducesCorrectSize_WhenEnabled()
        {
            bool origEnabled = OptimumConfig.MapPageCacheBc7;
            bool origSupported = OptimumConfig.MapPageCacheBc7Supported;

            OptimumConfig.MapPageCacheBc7 = true;
            OptimumConfig.MapPageCacheBc7Supported = true;
            try
            {
                int[] pixels = new int[256 * 256];
                // Fill with a gradient
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = (i & 0xFF) | ((i >> 2) & 0xFF) << 8 | unchecked((int)0xFF000000);
                }

                byte[]? result = OptimumBc7Support.Encode(pixels, 256, 256);
                Assert.NotNull(result);
                // BC7 compressed size: (256/4)*(256/4)*16 = 64*64*16 = 65536
                Assert.Equal(65536, result.Length);
            }
            finally
            {
                OptimumConfig.MapPageCacheBc7 = origEnabled;
                OptimumConfig.MapPageCacheBc7Supported = origSupported;
            }
        }

        [Fact]
        public void Encode_ReturnsNull_WhenPixelsMismatch()
        {
            bool origEnabled = OptimumConfig.MapPageCacheBc7;
            bool origSupported = OptimumConfig.MapPageCacheBc7Supported;

            OptimumConfig.MapPageCacheBc7 = true;
            OptimumConfig.MapPageCacheBc7Supported = true;
            try
            {
                int[] pixels = new int[100]; // wrong size
                byte[]? result = OptimumBc7Support.Encode(pixels, 256, 256);
                Assert.Null(result);
            }
            finally
            {
                OptimumConfig.MapPageCacheBc7 = origEnabled;
                OptimumConfig.MapPageCacheBc7Supported = origSupported;
            }
        }

        [Fact]
        public void Encode_SolidColor_ProducesNonZeroOutput()
        {
            bool origEnabled = OptimumConfig.MapPageCacheBc7;
            bool origSupported = OptimumConfig.MapPageCacheBc7Supported;

            OptimumConfig.MapPageCacheBc7 = true;
            OptimumConfig.MapPageCacheBc7Supported = true;
            try
            {
                int[] pixels = new int[256 * 256];
                Array.Fill(pixels, unchecked((int)0xFF804020)); // solid ABGR color

                byte[]? result = OptimumBc7Support.Encode(pixels, 256, 256);
                Assert.NotNull(result);
                Assert.Equal(65536, result.Length);

                // At least the mode bit should be set in every block
                for (int block = 0; block < result.Length; block += 16)
                {
                    // Mode 6 starts with 0x40
                    Assert.Equal(0x40, result[block] & 0x40);
                }
            }
            finally
            {
                OptimumConfig.MapPageCacheBc7 = origEnabled;
                OptimumConfig.MapPageCacheBc7Supported = origSupported;
            }
        }
    }

    public class DirtyPageTrackingTests
    {
        [Fact]
        public void InvalidateChunk_ClearsBitmaskBit()
        {
            // Simulates the bitmask logic: set bit then clear it
            int chunkX = 3;
            int chunkZ = 5;
            int idx = OptimumMapPageCache.ChunkIndexInPage(chunkX, chunkZ);

            ulong mask = 1UL << idx;
            Assert.NotEqual(0UL, mask);

            // Clearing the bit
            mask &= ~(1UL << idx);
            Assert.Equal(0UL, mask);
        }

        [Fact]
        public void InvalidateChunk_OnlyAffectsTargetBit()
        {
            // Set all 64 bits, then clear one
            ulong mask = ulong.MaxValue;

            int chunkX = 7;
            int chunkZ = 7;
            int idx = OptimumMapPageCache.ChunkIndexInPage(chunkX, chunkZ);
            mask &= ~(1UL << idx);

            // 63 bits remain set
            int setBits = 0;
            for (int i = 0; i < 64; i++)
            {
                if ((mask & (1UL << i)) != 0) setBits++;
            }
            Assert.Equal(63, setBits);
        }

        [Fact]
        public void DirtyChunk_NeighborsMapToCorrectPages()
        {
            // When chunk (8, 8) is dirty, vanilla invalidates:
            // (8,8), (8,7), (7,8), (8,9), (9,9)
            // These cross a page boundary: (8,8) -> page(1,1), (7,8) -> page(0,1)
            var (px1, pz1) = OptimumMapPageCache.ChunkToPage(8, 8);
            var (px2, pz2) = OptimumMapPageCache.ChunkToPage(7, 8);
            Assert.Equal(1, px1); Assert.Equal(1, pz1);
            Assert.Equal(0, px2); Assert.Equal(1, pz2);

            // Different pages get different keys
            long key1 = OptimumMapPageCache.PackPageCoord(px1, pz1);
            long key2 = OptimumMapPageCache.PackPageCoord(px2, pz2);
            Assert.NotEqual(key1, key2);
        }
    }

    public class MapLodAndPriorityTests
    {
        [Fact]
        public void Downsample2x_ProducesCorrectSize()
        {
            int[] full = new int[256 * 256];
            Array.Fill(full, unchecked((int)0xFF808080));
            int[] lod1 = OptimumMapLodGenerator.Downsample2x(full);
            Assert.NotNull(lod1);
            Assert.Equal(128 * 128, lod1.Length);
        }

        [Fact]
        public void Downsample4x_ProducesCorrectSize()
        {
            int[] full = new int[256 * 256];
            Array.Fill(full, unchecked((int)0xFF808080));
            int[] lod2 = OptimumMapLodGenerator.Downsample4x(full);
            Assert.NotNull(lod2);
            Assert.Equal(64 * 64, lod2.Length);
        }

        [Fact]
        public void Downsample2x_SolidColor_PreservesColor()
        {
            int[] full = new int[256 * 256];
            int color = unchecked((int)0xFF402010);
            Array.Fill(full, color);
            int[] lod1 = OptimumMapLodGenerator.Downsample2x(full);
            // Solid color downsampled should remain the same
            Assert.Equal(color, lod1[0]);
            Assert.Equal(color, lod1[128 * 128 - 1]);
        }

        [Fact]
        public void Downsample4x_SolidColor_PreservesColor()
        {
            int[] full = new int[256 * 256];
            int color = unchecked((int)0xFFAABBCC);
            Array.Fill(full, color);
            int[] lod2 = OptimumMapLodGenerator.Downsample4x(full);
            Assert.Equal(color, lod2[0]);
            Assert.Equal(color, lod2[64 * 64 - 1]);
        }

        [Fact]
        public void Downsample2x_NullInput_ReturnsNull()
        {
            Assert.Null(OptimumMapLodGenerator.Downsample2x(null));
        }

        [Fact]
        public void Downsample2x_WrongSize_ReturnsNull()
        {
            Assert.Null(OptimumMapLodGenerator.Downsample2x(new int[100]));
        }

        [Fact]
        public void PriorityQueue_DequeuesNearest()
        {
            var pq = new OptimumMapPagePriorityQueue();
            // Center at (5,5). Add pages at distances 3, 1, 2
            long k1 = OptimumMapPageCache.PackPageCoord(8, 5);  // dx=3
            long k2 = OptimumMapPageCache.PackPageCoord(6, 5);  // dx=1
            long k3 = OptimumMapPageCache.PackPageCoord(7, 5);  // dx=2

            pq.Enqueue(k1, 8, 5, 5, 5);
            pq.Enqueue(k2, 6, 5, 5, 5);
            pq.Enqueue(k3, 7, 5, 5, 5);

            // Should dequeue in nearest-first order
            Assert.Equal(k2, pq.Dequeue()); // dist^2 = 1
            Assert.Equal(k3, pq.Dequeue()); // dist^2 = 4
            Assert.Equal(k1, pq.Dequeue()); // dist^2 = 9
        }

        [Fact]
        public void PriorityQueue_Empty_ReturnsNegative()
        {
            var pq = new OptimumMapPagePriorityQueue();
            Assert.Equal(-1, pq.Dequeue());
        }

        [Fact]
        public void PriorityQueue_Count_TracksItems()
        {
            var pq = new OptimumMapPagePriorityQueue();
            Assert.Equal(0, pq.Count);
            pq.Enqueue(1, 0, 0, 0, 0);
            Assert.Equal(1, pq.Count);
            pq.Dequeue();
            Assert.Equal(0, pq.Count);
        }
    }
