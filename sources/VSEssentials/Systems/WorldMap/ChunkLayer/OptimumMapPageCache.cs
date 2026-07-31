using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Vintagestory.GameContent;

/// <summary>
/// Page-based disk cache for the world map terrain layer. Groups map chunks
/// into 8x8 pages (256x256 pixels) and persists them as zstd-compressed
/// RGBA on disk. Background threads handle I/O so map opens return cached
/// pages in sub-100ms instead of re-generating from MapDB.
///
/// The cache directory lives at {DataPath}/ModData/optimum-map/{world-id}/.
/// Each page file: {pageX}_{pageZ}.omp (Optimum Map Page).
/// </summary>
public sealed class OptimumMapPageCache : IDisposable
{
    /// <summary>
    /// Chunks per page edge. Page = PageLen x PageLen chunks = (PageLen*32) x (PageLen*32) pixels.
    /// </summary>
    public const int PageLen = 8;

    /// <summary>Pixels per page edge.</summary>
    public const int PagePixelSize = PageLen * GlobalConstants.ChunkSize; // 256

    /// <summary>Total pixel count per page (256*256 = 65536).</summary>
    public const int PagePixelCount = PagePixelSize * PagePixelSize;

    /// <summary>
    /// File header: magic (4 bytes) + version (1) + pageX (4) + pageZ (4) + bitmask (8) = 21 bytes.
    /// </summary>
    private const int HeaderSize = 21;
    private static readonly byte[] Magic = "OMP\x01"u8.ToArray();

    private readonly string _cacheDir;
    private readonly ICoreClientAPI _capi;
    private readonly ConcurrentQueue<PageWriteRequest> _writeQueue = new();
    private readonly ConcurrentQueue<PageReadResult> _readResults = new();
    private readonly Thread _writerThread;
    private volatile bool _disposed;

    /// <summary>
    /// In-memory tracking of which chunks within each page have valid cached pixels.
    /// Key = page coordinate (pageX, pageZ). Value = 64-bit bitmask (bit i = chunk at row*PageLen+col).
    /// </summary>
    private readonly ConcurrentDictionary<long, ulong> _pageBitmasks = new();

    /// <summary>
    /// Per-page pixel buffer. Written on background thread, read on main thread for upload.
    /// Key = packed page coord. Value = RGBA int[65536].
    /// </summary>
    private readonly ConcurrentDictionary<long, int[]> _pagePixels = new();

    /// <summary>
    /// Per-page lock objects. Protects pixel buffer writes from concurrent
    /// compositing when two chunks in the same page generate at the same time.
    /// </summary>
    private readonly ConcurrentDictionary<long, object> _pageLocks = new();

    public OptimumMapPageCache(ICoreClientAPI capi, string worldId)
    {
        _capi = capi;

        // Sanitize worldId: strip path separators and special characters
        // to prevent directory traversal (worldId comes from the savegame).
        string safeId = SanitizePathComponent(worldId);
        _cacheDir = Path.Combine(GamePaths.DataPath, "ModData", "optimum-map", safeId);
        Directory.CreateDirectory(_cacheDir);

        // Validate cache version: if the game version changed since the cache
        // was written, terrain generation parameters might differ (Anego changes
        // noise config between major updates). Purge stale pages on mismatch.
        ValidateCacheVersion();

        _writerThread = new Thread(WriterLoop)
        {
            Name = "Optimum-MapPageWriter",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _writerThread.Start();
    }

    private void ValidateCacheVersion()
    {
        string versionFile = Path.Combine(_cacheDir, ".version");
        string currentVersion = GameVersion.ShortGameVersion;

        try
        {
            if (File.Exists(versionFile))
            {
                string cached = File.ReadAllText(versionFile).Trim();
                if (cached == currentVersion) return;

                // Version mismatch: purge all .omp files (terrain noise params
                // can change between VS major versions, producing wrong heights)
                _capi.Logger.Notification("[Optimum] Map cache version mismatch ({0} vs {1}), purging stale pages.", cached, currentVersion);
                foreach (string file in Directory.EnumerateFiles(_cacheDir, "*.omp"))
                {
                    try { File.Delete(file); } catch { }
                }
            }

            File.WriteAllText(versionFile, currentVersion);
        }
        catch (Exception)
        {
            // Disk error: proceed without version check
        }
    }

    /// <summary>
    /// Convert a chunk coordinate to its containing page coordinate.
    /// </summary>
    public static (int pageX, int pageZ) ChunkToPage(int chunkX, int chunkZ)
    {
        // Use floor division for negative coordinates
        int pageX = chunkX >= 0 ? chunkX / PageLen : (chunkX - PageLen + 1) / PageLen;
        int pageZ = chunkZ >= 0 ? chunkZ / PageLen : (chunkZ - PageLen + 1) / PageLen;
        return (pageX, pageZ);
    }

    /// <summary>
    /// Index of a chunk within its page (0..63).
    /// </summary>
    public static int ChunkIndexInPage(int chunkX, int chunkZ)
    {
        int localX = ((chunkX % PageLen) + PageLen) % PageLen;
        int localZ = ((chunkZ % PageLen) + PageLen) % PageLen;
        return localZ * PageLen + localX;
    }

    /// <summary>
    /// Pack a page coordinate pair into a single long for dictionary keys.
    /// </summary>
    public static long PackPageCoord(int pageX, int pageZ)
    {
        return ((long)(uint)pageX << 32) | (uint)pageZ;
    }

    /// <summary>
    /// Submit chunk pixels to the page cache. Called from the generation thread
    /// after GenerateChunkImage produces an int[1024] ARGB array.
    /// </summary>
    public void WriteChunk(int chunkX, int chunkZ, int[] chunkPixels)
    {
        if (!OptimumConfig.MapPageCacheEnabled || _disposed) return;
        if (chunkPixels == null || chunkPixels.Length != GlobalConstants.ChunkSize * GlobalConstants.ChunkSize) return;

        var (pageX, pageZ) = ChunkToPage(chunkX, chunkZ);
        long key = PackPageCoord(pageX, pageZ);
        int idx = ChunkIndexInPage(chunkX, chunkZ);

        // Get or create the page pixel buffer
        int[] pageBuffer = _pagePixels.GetOrAdd(key, _ => new int[PagePixelCount]);
        object pageLock = _pageLocks.GetOrAdd(key, _ => new object());

        // Composite the 32x32 chunk into the correct position within the 256x256 page.
        // Lock per-page: two chunks in the same page can generate concurrently and
        // their Array.Copy calls would interleave without this.
        int localX = ((chunkX % PageLen) + PageLen) % PageLen;
        int localZ = ((chunkZ % PageLen) + PageLen) % PageLen;
        int basePixelX = localX * GlobalConstants.ChunkSize;
        int basePixelZ = localZ * GlobalConstants.ChunkSize;

        lock (pageLock)
        {
            for (int row = 0; row < GlobalConstants.ChunkSize; row++)
            {
                int srcOffset = row * GlobalConstants.ChunkSize;
                int dstOffset = (basePixelZ + row) * PagePixelSize + basePixelX;
                Array.Copy(chunkPixels, srcOffset, pageBuffer, dstOffset, GlobalConstants.ChunkSize);
            }
        }

        // Update bitmask
        ulong oldMask, newMask;
        do
        {
            oldMask = _pageBitmasks.GetOrAdd(key, 0UL);
            newMask = oldMask | (1UL << idx);
        } while (!_pageBitmasks.TryUpdate(key, newMask, oldMask) &&
                 _pageBitmasks.GetOrAdd(key, 0UL) != newMask);

        // Enqueue disk write
        _writeQueue.Enqueue(new PageWriteRequest(pageX, pageZ, key));
    }

    /// <summary>
    /// Try to read a full page from disk cache. Returns true if the page exists
    /// and the read was enqueued; the result arrives via TryGetReadResult.
    /// Call from the generation thread to skip regeneration for cached chunks.
    /// </summary>
    public bool TryReadPage(int pageX, int pageZ)
    {
        if (!OptimumConfig.MapPageCacheEnabled || _disposed) return false;

        string path = GetPagePath(pageX, pageZ);
        if (!File.Exists(path)) return false;

        // Read synchronously on the generation thread (background already)
        try
        {
            // Sanity check: a valid page file is header + gzip(256KB RGBA).
            // GZip at Fastest compresses 256KB of pixel data to ~60-100KB typical.
            // Cap at 1MB to reject corrupted files that claim excessive size.
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > 1024 * 1024) return false;

            byte[] fileBytes = File.ReadAllBytes(path);
            if (fileBytes.Length < HeaderSize) return false;

            // Validate magic
            if (fileBytes[0] != Magic[0] || fileBytes[1] != Magic[1] ||
                fileBytes[2] != Magic[2] || fileBytes[3] != Magic[3])
            {
                return false;
            }

            byte version = fileBytes[4];
            if (version != 1) return false;

            int storedPageX = BitConverter.ToInt32(fileBytes, 5);
            int storedPageZ = BitConverter.ToInt32(fileBytes, 9);
            ulong bitmask = BitConverter.ToUInt64(fileBytes, 13);

            if (storedPageX != pageX || storedPageZ != pageZ) return false;

            // Decompress the RGBA payload
            int compressedLen = fileBytes.Length - HeaderSize;
            byte[] compressed = new byte[compressedLen];
            Array.Copy(fileBytes, HeaderSize, compressed, 0, compressedLen);

            byte[] decompressed = DecompressGzip(compressed);
            if (decompressed == null || decompressed.Length != PagePixelCount * 4) return false;

            // Convert byte[] RGBA to int[] ARGB (VS uses ARGB internally)
            int[] pixels = new int[PagePixelCount];
            Buffer.BlockCopy(decompressed, 0, pixels, 0, decompressed.Length);

            long key = PackPageCoord(pageX, pageZ);
            _pagePixels[key] = pixels;
            _pageBitmasks[key] = bitmask;

            _readResults.Enqueue(new PageReadResult(pageX, pageZ, pixels, bitmask));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Dequeue a completed page read. Called from the main thread during OnTick.
    /// </summary>
    public bool TryGetReadResult(out PageReadResult result)
    {
        return _readResults.TryDequeue(out result);
    }

    /// <summary>
    /// Get the full page pixel buffer by packed page key. Returns true if the
    /// page exists in memory.
    /// </summary>
    public bool TryGetPagePixels(long pageKey, out int[] pixels)
    {
        return _pagePixels.TryGetValue(pageKey, out pixels);
    }

    /// <summary>
    /// Check if a specific chunk is cached in memory (in the page pixel buffer).
    /// </summary>
    public bool IsChunkCached(int chunkX, int chunkZ)
    {
        var (pageX, pageZ) = ChunkToPage(chunkX, chunkZ);
        long key = PackPageCoord(pageX, pageZ);
        int idx = ChunkIndexInPage(chunkX, chunkZ);

        if (_pageBitmasks.TryGetValue(key, out ulong mask))
        {
            return (mask & (1UL << idx)) != 0;
        }
        return false;
    }

    /// <summary>
    /// Get the cached pixels for a single chunk from the in-memory page buffer.
    /// Returns null if not cached. The returned array is a COPY (caller-owned).
    /// For the hot path (OnViewChangedClient serves from cache), callers should
    /// prefer TryGetPagePixels + manual sub-region copy with a reusable buffer.
    /// </summary>
    public int[]? GetCachedChunkPixels(int chunkX, int chunkZ)
    {
        if (!IsChunkCached(chunkX, chunkZ)) return null;

        var (pageX, pageZ) = ChunkToPage(chunkX, chunkZ);
        long key = PackPageCoord(pageX, pageZ);

        if (!_pagePixels.TryGetValue(key, out int[]? pageBuffer)) return null;

        int localX = ((chunkX % PageLen) + PageLen) % PageLen;
        int localZ = ((chunkZ % PageLen) + PageLen) % PageLen;
        int basePixelX = localX * GlobalConstants.ChunkSize;
        int basePixelZ = localZ * GlobalConstants.ChunkSize;

        int[] chunkPixels = _chunkPixelBuffer ??= new int[GlobalConstants.ChunkSize * GlobalConstants.ChunkSize];
        for (int row = 0; row < GlobalConstants.ChunkSize; row++)
        {
            int srcOffset = (basePixelZ + row) * PagePixelSize + basePixelX;
            int dstOffset = row * GlobalConstants.ChunkSize;
            Array.Copy(pageBuffer, srcOffset, chunkPixels, dstOffset, GlobalConstants.ChunkSize);
        }

        // Return a copy since the caller (loadFromChunkPixels) stores the reference
        int[] result = new int[chunkPixels.Length];
        Array.Copy(chunkPixels, result, chunkPixels.Length);
        return result;
    }

    [ThreadStatic]
    private static int[]? _chunkPixelBuffer;

    /// <summary>
    /// Fill uncached chunks in the given page with pregen (biome-estimated)
    /// pixels from the terrain sampler. Only writes chunks whose bitmask bit
    /// is unset. Marks filled chunks with a separate pregen bitmask so
    /// real explored data can overwrite them later without triggering a
    /// redundant disk write.
    ///
    /// Call from the generation thread or any background thread. Thread-safe:
    /// uses the same per-page lock as WriteChunk.
    /// </summary>
    public int FillPregenChunks(int pageX, int pageZ, OptimumTerrainSampler sampler)
    {
        if (!OptimumConfig.MapPageCachePregen || _disposed || sampler == null) return 0;

        long key = PackPageCoord(pageX, pageZ);
        ulong exploredMask = _pageBitmasks.GetOrAdd(key, 0UL);

        // Skip pages that are fully explored.
        if (exploredMask == ulong.MaxValue) return 0;

        int[] pageBuffer = _pagePixels.GetOrAdd(key, _ => new int[PagePixelCount]);
        object pageLock = _pageLocks.GetOrAdd(key, _ => new object());
        int filled = 0;

        for (int idx = 0; idx < PageLen * PageLen; idx++)
        {
            if ((exploredMask & (1UL << idx)) != 0) continue; // explored: skip

            int localZ = idx / PageLen;
            int localX = idx % PageLen;
            int chunkX = pageX * PageLen + localX;
            int chunkZ = pageZ * PageLen + localZ;

            int[]? pregen = sampler.SampleChunk(chunkX, chunkZ);
            if (pregen == null) continue; // region not loaded: skip

            int basePixelX = localX * GlobalConstants.ChunkSize;
            int basePixelZ = localZ * GlobalConstants.ChunkSize;

            lock (pageLock)
            {
                for (int row = 0; row < GlobalConstants.ChunkSize; row++)
                {
                    int srcOffset = row * GlobalConstants.ChunkSize;
                    int dstOffset = (basePixelZ + row) * PagePixelSize + basePixelX;
                    Array.Copy(pregen, srcOffset, pageBuffer, dstOffset, GlobalConstants.ChunkSize);
                }
            }
            filled++;
        }

        return filled;
    }

    /// <summary>
    /// Mark a chunk as dirty: clears its bit from the page bitmask so the
    /// next generation pass re-writes it.
    /// </summary>
    public void InvalidateChunk(int chunkX, int chunkZ)
    {
        var (pageX, pageZ) = ChunkToPage(chunkX, chunkZ);
        long key = PackPageCoord(pageX, pageZ);
        int idx = ChunkIndexInPage(chunkX, chunkZ);

        ulong oldMask, newMask;
        do
        {
            if (!_pageBitmasks.TryGetValue(key, out oldMask)) return;
            newMask = oldMask & ~(1UL << idx);
        } while (!_pageBitmasks.TryUpdate(key, newMask, oldMask));
    }

    /// <summary>
    /// Flush pending writes. Called on map close or shutdown.
    /// </summary>
    public void Flush()
    {
        byte[]? flushBuffer = null;
        while (_writeQueue.TryDequeue(out PageWriteRequest req))
        {
            flushBuffer ??= new byte[PagePixelCount * 4];
            WritePage(req, flushBuffer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Signal the writer thread to exit and wait for it to finish its
        // current page write before draining anything ourselves. This prevents
        // the main thread and writer thread from racing on the same .omp file.
        if (_writerThread.IsAlive)
        {
            _writerThread.Join(timeout: TimeSpan.FromSeconds(5));
        }

        // Drain anything left after the writer exited
        Flush();
    }

    private string GetPagePath(int pageX, int pageZ)
    {
        return Path.Combine(_cacheDir, $"{pageX}_{pageZ}.omp");
    }

    /// <summary>
    /// Strip path-separator characters and known traversal sequences from
    /// a string intended as a single directory name component.
    /// </summary>
    private static string SanitizePathComponent(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "default";
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (Array.IndexOf(invalid, c) < 0 && c != '.' && c != ' ')
                sb.Append(c);
        }
        string result = sb.ToString();
        return result.Length == 0 ? "default" : result;
    }

    private void WriterLoop()
    {
        // Coalesce writes: multiple chunks hitting the same page produce multiple
        // write requests, but we only need to flush once per page per batch.
        HashSet<long> written = new();
        // Reusable buffer for pixel->byte conversion (writer thread is single-threaded)
        byte[] writerRawBytes = new byte[PagePixelCount * 4];

        while (!_disposed)
        {
            if (_writeQueue.IsEmpty)
            {
                Thread.Sleep(50);
                continue;
            }

            written.Clear();
            while (_writeQueue.TryDequeue(out PageWriteRequest req))
            {
                long key = req.Key;
                if (written.Contains(key)) continue;
                written.Add(key);
                WritePage(req, writerRawBytes);
            }
        }

        // Drain remaining on shutdown
        while (_writeQueue.TryDequeue(out PageWriteRequest req))
        {
            WritePage(req, writerRawBytes);
        }
    }

    private void WritePage(PageWriteRequest req, byte[]? reusableBuffer = null)
    {
        long key = req.Key;
        if (!_pagePixels.TryGetValue(key, out int[]? pageBuffer)) return;
        if (!_pageBitmasks.TryGetValue(key, out ulong bitmask)) return;

        try
        {
            // Snapshot the pixel buffer under the page lock to avoid reading
            // a partially-written buffer from a concurrent WriteChunk call.
            byte[] rawBytes = reusableBuffer ?? new byte[PagePixelCount * 4];
            object pageLock = _pageLocks.GetOrAdd(key, _ => new object());
            lock (pageLock)
            {
                Buffer.BlockCopy(pageBuffer, 0, rawBytes, 0, PagePixelCount * 4);
            }

            byte[] compressed = CompressGzip(rawBytes);
            if (compressed == null) return;

            // Build file: header + compressed payload
            byte[] file = new byte[HeaderSize + compressed.Length];
            Array.Copy(Magic, 0, file, 0, 4);
            file[4] = 1; // version
            BitConverter.TryWriteBytes(file.AsSpan(5), req.PageX);
            BitConverter.TryWriteBytes(file.AsSpan(9), req.PageZ);
            BitConverter.TryWriteBytes(file.AsSpan(13), bitmask);
            Array.Copy(compressed, 0, file, HeaderSize, compressed.Length);

            string path = GetPagePath(req.PageX, req.PageZ);
            string tmpPath = path + ".tmp";
            File.WriteAllBytes(tmpPath, file);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (Exception)
        {
            // Disk failure: skip, page will be regenerated next session.
        }
    }

    /// <summary>
    /// Compress raw bytes with GZip (available in System.IO.Compression).
    /// Map pixel data (int[] RGBA, repetitive color patterns) compresses
    /// well with any dictionary-based codec. GZip gives 3-5x reduction on
    /// typical map pages without adding a native dependency.
    /// </summary>
    private static byte[] CompressGzip(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Decompress GZip-compressed bytes.
    /// </summary>
    private static byte[]? DecompressGzip(byte[] compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal readonly struct PageWriteRequest
    {
        public readonly int PageX;
        public readonly int PageZ;
        public readonly long Key;

        public PageWriteRequest(int pageX, int pageZ, long key)
        {
            PageX = pageX;
            PageZ = pageZ;
            Key = key;
        }
    }
}

/// <summary>
/// Result of a page read from disk cache.
/// </summary>
public sealed class PageReadResult
{
    public readonly int PageX;
    public readonly int PageZ;
    public readonly int[] Pixels;
    public readonly ulong Bitmask;

    public PageReadResult(int pageX, int pageZ, int[] pixels, ulong bitmask)
    {
        PageX = pageX;
        PageZ = pageZ;
        Pixels = pixels;
        Bitmask = bitmask;
    }
}
