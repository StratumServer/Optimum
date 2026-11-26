using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Vintagestory.GameContent;

/// <summary>
/// Page-based disk and memory cache for the world map terrain layer. Groups map
/// chunks into 8x8 pages (256x256 pixels) and persists them as gzip-compressed
/// RGBA on disk. Background threads handle I/O and coalesced dirty tracking lets
/// the main thread flush updates to GL_TEXTURE_2D_ARRAY in a single batched pass.
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

    /// <summary>Total uncompressed RGBA byte count per page (256*256*4 = 262144).</summary>
    private const int PageByteSize = PagePixelCount * 4;

    /// <summary>Maximum accepted compressed .omp file size (1 MB).</summary>
    private const int MaxPageFileBytes = 1024 * 1024;

    /// <summary>
    /// Maximum incremental 32x32 chunk sub-uploads per page before promoting
    /// a fully cached page to a single 256x256 upload.
    /// </summary>
    internal const int MaxSubChunkUploadsPerPage = 16;

    /// <summary>
    /// File header: magic (4 bytes) + version (1) + pageX (4) + pageZ (4) + bitmask (8) = 21 bytes.
    /// </summary>
    private const int HeaderSize = 21;
    private static readonly byte[] Magic = "OMP\x01"u8.ToArray();

    private sealed class PageEntry
    {
        public readonly object SyncRoot = new();
        public readonly int[] Pixels = new int[PagePixelCount];
        public ulong Bitmask;
        public ulong InvalidatedMask;
        public ulong DirtyGpuMask;
        public bool HasPregen;
        public bool FullGpuUploadNeeded;
        public bool QueuedForGpuUpload;
        public bool QueuedForDiskWrite;
        public bool DiskReadAttempted;
        public long LastAccessTick;
    }

    private readonly string cacheDir;
    private readonly ILogger? logger;
    private readonly int maxInMemoryPages;
    private readonly ConcurrentDictionary<long, PageEntry> pages = new();
    private readonly ConcurrentQueue<PageWriteRequest> writeQueue = new();
    private readonly ConcurrentQueue<long> dirtyGpuPages = new();
    private readonly object writerSync = new();
    private readonly AutoResetEvent writeSignal = new(false);
    private readonly Thread writerThread;
    private int evicting;
    private volatile bool disposed;

    // Main-thread-only scratch buffers reused across FlushDirtyPagesToGpu calls (zero per-tick allocs).
    private int[]? gpuFullPageScratch;
    private int[][]? gpuChunkSlices;

    public OptimumMapPageCache(ICoreClientAPI capi, string worldId)
        : this(
            Path.Combine(GamePaths.DataPath, "ModData", "optimum-map", SanitizePathComponent(worldId)),
            capi?.Logger,
            maxInMemoryPages: 0)
    {
    }

    internal OptimumMapPageCache(string cacheDir, ILogger? logger = null, int maxInMemoryPages = 0)
    {
        this.cacheDir = cacheDir;
        this.logger = logger;
        this.maxInMemoryPages = maxInMemoryPages > 0
            ? maxInMemoryPages
            : Math.Max(OptimumConfig.MapPageCacheMaxLayers * 2, 256);

        Directory.CreateDirectory(this.cacheDir);
        ValidateCacheVersion();

        writerThread = new Thread(WriterLoop)
        {
            Name = "Optimum-MapPageWriter",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        writerThread.Start();
    }

    internal int CachedPageCount => pages.Count;

    private void ValidateCacheVersion()
    {
        string versionFile = Path.Combine(cacheDir, ".version");
        string currentVersion = GameVersion.ShortGameVersion;

        try
        {
            if (File.Exists(versionFile))
            {
                string cached = File.ReadAllText(versionFile).Trim();
                if (cached == currentVersion) return;

                // Version mismatch: purge all .omp files (terrain noise params
                // can change between VS major versions, producing wrong heights)
                logger?.Notification("[Optimum] Map cache version mismatch ({0} vs {1}), purging stale pages.", cached, currentVersion);
                foreach (string file in Directory.EnumerateFiles(cacheDir, "*.omp"))
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
        if (!OptimumConfig.EffectiveMapPageCache || disposed) return;
        if (chunkPixels == null || chunkPixels.Length != GlobalConstants.ChunkSize * GlobalConstants.ChunkSize) return;

        var (pageX, pageZ) = ChunkToPage(chunkX, chunkZ);
        long key = PackPageCoord(pageX, pageZ);
        int idx = ChunkIndexInPage(chunkX, chunkZ);
        ulong bit = 1UL << idx;

        PageEntry entry = GetOrAddPage(key);

        int localX = idx % PageLen;
        int localZ = idx / PageLen;
        int basePixelX = localX * GlobalConstants.ChunkSize;
        int basePixelZ = localZ * GlobalConstants.ChunkSize;

        bool enqueueGpu = false;
        bool enqueueDisk = false;

        lock (entry.SyncRoot)
        {
            for (int row = 0; row < GlobalConstants.ChunkSize; row++)
            {
                int srcOffset = row * GlobalConstants.ChunkSize;
                int dstOffset = (basePixelZ + row) * PagePixelSize + basePixelX;
                Array.Copy(chunkPixels, srcOffset, entry.Pixels, dstOffset, GlobalConstants.ChunkSize);
            }

            entry.Bitmask |= bit;
            entry.InvalidatedMask &= ~bit;
            entry.DirtyGpuMask |= bit;
            entry.LastAccessTick = Environment.TickCount64;

            if (!entry.QueuedForGpuUpload)
            {
                entry.QueuedForGpuUpload = true;
                enqueueGpu = true;
            }

            if (!entry.QueuedForDiskWrite)
            {
                entry.QueuedForDiskWrite = true;
                enqueueDisk = true;
            }
        }

        if (enqueueGpu)
        {
            dirtyGpuPages.Enqueue(key);
        }

        if (enqueueDisk)
        {
            writeQueue.Enqueue(new PageWriteRequest(pageX, pageZ, key));
            SignalWriter();
        }
    }

    /// <summary>
    /// Try to read a full page from disk cache on the background generation thread.
    /// Merges disk chunks into memory without overwriting fresher live or invalidated
    /// chunks, and schedules a coalesced GPU upload on the main thread.
    /// </summary>
    public bool TryReadPage(int pageX, int pageZ)
    {
        if (!OptimumConfig.EffectiveMapPageCache || disposed) return false;

        long key = PackPageCoord(pageX, pageZ);
        if (pages.TryGetValue(key, out PageEntry? existing))
        {
            lock (existing.SyncRoot)
            {
                if (existing.DiskReadAttempted)
                {
                    return false;
                }
            }
        }

        string path = GetPagePath(pageX, pageZ);
        if (!File.Exists(path))
        {
            if (existing != null)
            {
                lock (existing.SyncRoot)
                {
                    existing.DiskReadAttempted = true;
                }
            }
            return false;
        }

        byte[]? rentedRaw = null;
        try
        {
            rentedRaw = ArrayPool<byte>.Shared.Rent(PageByteSize);
            if (!TryReadPagePayloadFromDisk(path, pageX, pageZ, rentedRaw, out ulong diskBitmask))
            {
                return false;
            }

            PageEntry entry = GetOrAddPage(key);
            bool enqueueGpu = false;

            lock (entry.SyncRoot)
            {
                if (entry.DiskReadAttempted)
                {
                    return false;
                }

                entry.DiskReadAttempted = true;
                entry.LastAccessTick = Environment.TickCount64;
                MergeDiskPixelsLocked(entry, rentedRaw, diskBitmask);

                if (entry.Bitmask != 0UL || entry.HasPregen)
                {
                    entry.FullGpuUploadNeeded = true;
                    if (!entry.QueuedForGpuUpload)
                    {
                        entry.QueuedForGpuUpload = true;
                        enqueueGpu = true;
                    }
                }
            }

            if (enqueueGpu)
            {
                dirtyGpuPages.Enqueue(key);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (rentedRaw != null)
            {
                ArrayPool<byte>.Shared.Return(rentedRaw);
            }
        }
    }

    /// <summary>
    /// Request that an in-memory page be uploaded to the GPU texture array (for example
    /// after closing and reopening the map). Returns true if the page has already checked
    /// disk so the caller can skip queuing another disk read.
    /// </summary>
    public bool RequestGpuUpload(long pageKey)
    {
        if (!OptimumConfig.EffectiveMapPageCache || disposed) return false;
        if (!pages.TryGetValue(pageKey, out PageEntry? entry)) return false;

        bool enqueueGpu = false;
        bool diskChecked;

        lock (entry.SyncRoot)
        {
            entry.LastAccessTick = Environment.TickCount64;
            diskChecked = entry.DiskReadAttempted;
            if (entry.Bitmask != 0UL || entry.HasPregen)
            {
                entry.FullGpuUploadNeeded = true;
                if (!entry.QueuedForGpuUpload)
                {
                    entry.QueuedForGpuUpload = true;
                    enqueueGpu = true;
                }
            }
        }

        if (enqueueGpu)
        {
            dirtyGpuPages.Enqueue(pageKey);
        }

        return diskChecked;
    }

    /// <summary>
    /// Clear pending GPU uploads when the map closes and disposes its texture array.
    /// </summary>
    public void ClearPendingGpuUploads()
    {
        while (dirtyGpuPages.TryDequeue(out long pageKey))
        {
            if (pages.TryGetValue(pageKey, out PageEntry? entry))
            {
                lock (entry.SyncRoot)
                {
                    entry.QueuedForGpuUpload = false;
                    entry.FullGpuUploadNeeded = false;
                    entry.DirtyGpuMask = 0UL;
                }
            }
        }
    }

    /// <summary>
    /// Flush up to <paramref name="maxPagesPerTick"/> dirty pages to the GPU texture array
    /// on the main render thread in a single GL bind batch. Resident pages with incremental
    /// chunk updates upload only their 32x32 dirty sub-regions instead of full 256x256 pages.
    /// </summary>
    public int FlushDirtyPagesToGpu(OptimumMapTextureArray textureArray, int maxPagesPerTick = 16)
    {
        if (textureArray == null || disposed || maxPagesPerTick <= 0 || dirtyGpuPages.IsEmpty)
        {
            return 0;
        }

        int[] fullScratch = gpuFullPageScratch ??= new int[PagePixelCount];
        int[][] chunkSlices = EnsureChunkSlices();

        int processed = 0;
        bool batchStarted = false;

        try
        {
            while (processed < maxPagesPerTick && dirtyGpuPages.TryDequeue(out long pageKey))
            {
                bool hasLayer = textureArray.HasPage(pageKey);
                if (!TrySnapshotDirtyPage(
                    pageKey,
                    hasLayer,
                    fullScratch,
                    chunkSlices,
                    out bool isFullPage,
                    out ulong dirtyChunkMask,
                    out int _))
                {
                    continue;
                }

                if (!batchStarted)
                {
                    textureArray.BeginUploadBatch();
                    batchStarted = true;
                }

                if (isFullPage)
                {
                    textureArray.UploadPage(pageKey, fullScratch);
                }
                else
                {
                    int sliceIndex = 0;
                    ulong remaining = dirtyChunkMask;
                    bool fallbackToFull = false;

                    while (remaining != 0UL)
                    {
                        int idx = BitOperations.TrailingZeroCount(remaining);
                        remaining &= remaining - 1;
                        int localX = idx % PageLen;
                        int localZ = idx / PageLen;

                        if (!textureArray.UploadChunk(pageKey, localX, localZ, chunkSlices[sliceIndex++]))
                        {
                            fallbackToFull = true;
                            break;
                        }
                    }

                    if (fallbackToFull)
                    {
                        RequestGpuUpload(pageKey);
                    }
                }

                processed++;
            }
        }
        finally
        {
            if (batchStarted)
            {
                textureArray.EndUploadBatch();
            }
        }

        return processed;
    }

    internal bool TryDequeueDirtyGpuPage(out long pageKey)
    {
        return dirtyGpuPages.TryDequeue(out pageKey);
    }

    internal bool TrySnapshotDirtyPage(
        long pageKey,
        bool hasLayer,
        int[] fullScratch,
        int[][] chunkSlices,
        out bool isFullPage,
        out ulong dirtyChunkMask,
        out int chunkSliceCount)
    {
        isFullPage = false;
        dirtyChunkMask = 0UL;
        chunkSliceCount = 0;

        if (!pages.TryGetValue(pageKey, out PageEntry? entry))
        {
            return false;
        }

        bool requeueRemaining = false;

        lock (entry.SyncRoot)
        {
            entry.QueuedForGpuUpload = false;
            bool needFull = entry.FullGpuUploadNeeded || !hasLayer;
            ulong dirtyMask = entry.DirtyGpuMask;
            entry.FullGpuUploadNeeded = false;
            entry.DirtyGpuMask = 0UL;
            entry.LastAccessTick = Environment.TickCount64;

            if (!needFull && dirtyMask == 0UL)
            {
                return false;
            }

            int dirtyCount = BitOperations.PopCount(dirtyMask);
            if (needFull || (dirtyCount > chunkSlices.Length && entry.Bitmask == ulong.MaxValue))
            {
                Array.Copy(entry.Pixels, 0, fullScratch, 0, PagePixelCount);
                isFullPage = true;
                return true;
            }

            ulong uploadedMask = 0UL;
            ulong remaining = dirtyMask;
            int sliceIndex = 0;
            while (remaining != 0UL && sliceIndex < chunkSlices.Length)
            {
                int idx = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                uploadedMask |= 1UL << idx;

                int localX = idx % PageLen;
                int localZ = idx / PageLen;
                int basePixelX = localX * GlobalConstants.ChunkSize;
                int basePixelZ = localZ * GlobalConstants.ChunkSize;
                int[] slice = chunkSlices[sliceIndex++];

                for (int row = 0; row < GlobalConstants.ChunkSize; row++)
                {
                    int srcOffset = (basePixelZ + row) * PagePixelSize + basePixelX;
                    int dstOffset = row * GlobalConstants.ChunkSize;
                    Array.Copy(entry.Pixels, srcOffset, slice, dstOffset, GlobalConstants.ChunkSize);
                }
            }

            if (remaining != 0UL)
            {
                entry.DirtyGpuMask |= remaining;
                entry.QueuedForGpuUpload = true;
                requeueRemaining = true;
            }

            dirtyChunkMask = uploadedMask;
            chunkSliceCount = sliceIndex;
        }

        if (requeueRemaining)
        {
            dirtyGpuPages.Enqueue(pageKey);
        }

        return chunkSliceCount > 0;
    }

    /// <summary>
    /// Retained for API compatibility; completed reads are now flushed directly via
    /// <see cref="FlushDirtyPagesToGpu"/> without per-chunk main-thread re-enqueueing.
    /// </summary>
    public bool TryGetReadResult(out PageReadResult result)
    {
        result = default!;
        return false;
    }

    /// <summary>
    /// Get the full page pixel buffer by packed page key. Returns true if the
    /// page exists in memory.
    /// </summary>
    public bool TryGetPagePixels(long pageKey, out int[] pixels)
    {
        if (pages.TryGetValue(pageKey, out PageEntry? entry))
        {
            pixels = entry.Pixels;
            return true;
        }

        pixels = default!;
        return false;
    }

    /// <summary>
    /// Check if a specific chunk is cached in memory (in the page pixel buffer).
    /// </summary>
    public bool IsChunkCached(int chunkX, int chunkZ)
    {
        var (pageX, pageZ) = ChunkToPage(chunkX, chunkZ);
        long key = PackPageCoord(pageX, pageZ);
        int idx = ChunkIndexInPage(chunkX, chunkZ);

        if (pages.TryGetValue(key, out PageEntry? entry))
        {
            lock (entry.SyncRoot)
            {
                return (entry.Bitmask & (1UL << idx)) != 0UL;
            }
        }
        return false;
    }

    /// <summary>
    /// Get a thread-safe copy of the cached pixels for a single chunk from the
    /// in-memory page buffer. Returns null if not cached.
    /// </summary>
    public int[]? GetCachedChunkPixels(int chunkX, int chunkZ)
    {
        var (pageX, pageZ) = ChunkToPage(chunkX, chunkZ);
        long key = PackPageCoord(pageX, pageZ);
        int idx = ChunkIndexInPage(chunkX, chunkZ);

        if (!pages.TryGetValue(key, out PageEntry? entry))
        {
            return null;
        }

        int localX = idx % PageLen;
        int localZ = idx / PageLen;
        int basePixelX = localX * GlobalConstants.ChunkSize;
        int basePixelZ = localZ * GlobalConstants.ChunkSize;

        int[] result = new int[GlobalConstants.ChunkSize * GlobalConstants.ChunkSize];
        lock (entry.SyncRoot)
        {
            if ((entry.Bitmask & (1UL << idx)) == 0UL)
            {
                return null;
            }

            entry.LastAccessTick = Environment.TickCount64;
            for (int row = 0; row < GlobalConstants.ChunkSize; row++)
            {
                int srcOffset = (basePixelZ + row) * PagePixelSize + basePixelX;
                int dstOffset = row * GlobalConstants.ChunkSize;
                Array.Copy(entry.Pixels, srcOffset, result, dstOffset, GlobalConstants.ChunkSize);
            }
        }

        return result;
    }

    /// <summary>
    /// Fill uncached chunks in the given page with pregen (biome-estimated)
    /// pixels from the terrain sampler. Only writes chunks whose bitmask bit
    /// is unset and avoids allocating a page buffer until at least one chunk
    /// samples successfully.
    ///
    /// Call from the generation thread or any background thread.
    /// </summary>
    public int FillPregenChunks(int pageX, int pageZ, OptimumTerrainSampler sampler)
    {
        if (!OptimumConfig.MapPageCachePregen || disposed || sampler == null) return 0;

        long key = PackPageCoord(pageX, pageZ);
        ulong exploredMask = 0UL;
        if (pages.TryGetValue(key, out PageEntry? existing))
        {
            lock (existing.SyncRoot)
            {
                exploredMask = existing.Bitmask;
            }
            if (exploredMask == ulong.MaxValue) return 0;
        }

        PageEntry? entry = existing;
        int filled = 0;

        for (int idx = 0; idx < PageLen * PageLen; idx++)
        {
            ulong bit = 1UL << idx;
            if ((exploredMask & bit) != 0UL) continue;

            int localZ = idx / PageLen;
            int localX = idx % PageLen;
            int chunkX = pageX * PageLen + localX;
            int chunkZ = pageZ * PageLen + localZ;

            int[]? pregen = sampler.SampleChunk(chunkX, chunkZ);
            if (pregen == null) continue;

            entry ??= GetOrAddPage(key);

            int basePixelX = localX * GlobalConstants.ChunkSize;
            int basePixelZ = localZ * GlobalConstants.ChunkSize;

            lock (entry.SyncRoot)
            {
                if ((entry.Bitmask & bit) != 0UL) continue;

                for (int row = 0; row < GlobalConstants.ChunkSize; row++)
                {
                    int srcOffset = row * GlobalConstants.ChunkSize;
                    int dstOffset = (basePixelZ + row) * PagePixelSize + basePixelX;
                    Array.Copy(pregen, srcOffset, entry.Pixels, dstOffset, GlobalConstants.ChunkSize);
                }
                entry.HasPregen = true;
                entry.LastAccessTick = Environment.TickCount64;
            }
            filled++;
        }

        if (filled > 0 && entry != null)
        {
            bool enqueueGpu = false;
            lock (entry.SyncRoot)
            {
                entry.FullGpuUploadNeeded = true;
                if (!entry.QueuedForGpuUpload)
                {
                    entry.QueuedForGpuUpload = true;
                    enqueueGpu = true;
                }
            }

            if (enqueueGpu)
            {
                dirtyGpuPages.Enqueue(key);
            }
        }

        return filled;
    }

    /// <summary>
    /// Mark a chunk as dirty: clears its bit from the page bitmask and records it
    /// as invalidated so concurrent disk reads cannot resurrect stale pixels.
    /// </summary>
    public void InvalidateChunk(int chunkX, int chunkZ)
    {
        var (pageX, pageZ) = ChunkToPage(chunkX, chunkZ);
        long key = PackPageCoord(pageX, pageZ);
        if (!pages.TryGetValue(key, out PageEntry? entry)) return;

        int idx = ChunkIndexInPage(chunkX, chunkZ);
        ulong bit = 1UL << idx;

        lock (entry.SyncRoot)
        {
            entry.Bitmask &= ~bit;
            entry.InvalidatedMask |= bit;
            entry.DirtyGpuMask &= ~bit;
        }
    }

    /// <summary>
    /// Flush pending writes. Called on map close or shutdown.
    /// </summary>
    public void Flush()
    {
        lock (writerSync)
        {
            byte[]? flushBuffer = null;
            MemoryStream? compressBuffer = null;
            while (writeQueue.TryDequeue(out PageWriteRequest req))
            {
                flushBuffer ??= new byte[PageByteSize];
                compressBuffer ??= new MemoryStream(128 * 1024);
                WritePageLocked(req, flushBuffer, compressBuffer);
            }
            compressBuffer?.Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        SignalWriter();
        if (writerThread.IsAlive)
        {
            writerThread.Join(timeout: TimeSpan.FromSeconds(5));
        }

        Flush();
        ClearPendingGpuUploads();
        pages.Clear();
        writeSignal.Dispose();
    }

    private PageEntry GetOrAddPage(long key)
    {
        if (pages.TryGetValue(key, out PageEntry? existing))
        {
            return existing;
        }

        var created = new PageEntry { LastAccessTick = Environment.TickCount64 };
        PageEntry actual = pages.GetOrAdd(key, created);
        if (ReferenceEquals(actual, created) && pages.Count > maxInMemoryPages)
        {
            EvictIfOverCapacity();
        }
        return actual;
    }

    private void EvictIfOverCapacity()
    {
        if (Interlocked.CompareExchange(ref evicting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            int count = pages.Count;
            if (count <= maxInMemoryPages)
            {
                return;
            }

            int target = Math.Max(1, maxInMemoryPages * 7 / 8);
            var candidates = new List<(long Key, PageEntry Entry, long Tick)>(count);
            foreach (var kv in pages)
            {
                PageEntry entry = kv.Value;
                long tick;
                bool pinned;
                lock (entry.SyncRoot)
                {
                    tick = entry.LastAccessTick;
                    pinned = entry.QueuedForDiskWrite || entry.QueuedForGpuUpload;
                }
                if (!pinned)
                {
                    candidates.Add((kv.Key, entry, tick));
                }
            }

            candidates.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
            int toEvict = count - target;
            for (int i = 0; i < candidates.Count && toEvict > 0; i++)
            {
                var (key, entry, _) = candidates[i];
                bool canEvict;
                lock (entry.SyncRoot)
                {
                    canEvict = !entry.QueuedForDiskWrite && !entry.QueuedForGpuUpload;
                }

                if (canEvict && pages.TryRemove(new KeyValuePair<long, PageEntry>(key, entry)))
                {
                    toEvict--;
                }
            }
        }
        finally
        {
            Volatile.Write(ref evicting, 0);
        }
    }

    private int[][] EnsureChunkSlices()
    {
        if (gpuChunkSlices != null)
        {
            return gpuChunkSlices;
        }

        int chunkPixels = GlobalConstants.ChunkSize * GlobalConstants.ChunkSize;
        var slices = new int[MaxSubChunkUploadsPerPage][];
        for (int i = 0; i < slices.Length; i++)
        {
            slices[i] = new int[chunkPixels];
        }
        gpuChunkSlices = slices;
        return slices;
    }

    private void SignalWriter()
    {
        try
        {
            writeSignal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string GetPagePath(int pageX, int pageZ)
    {
        return Path.Combine(cacheDir, $"{pageX}_{pageZ}.omp");
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
        byte[] writerRawBytes = new byte[PageByteSize];
        using var compressBuffer = new MemoryStream(128 * 1024);

        while (!disposed)
        {
            if (writeQueue.IsEmpty)
            {
                writeSignal.WaitOne(250);
                continue;
            }

            lock (writerSync)
            {
                while (writeQueue.TryDequeue(out PageWriteRequest req))
                {
                    WritePageLocked(req, writerRawBytes, compressBuffer);
                }
            }
        }

        lock (writerSync)
        {
            while (writeQueue.TryDequeue(out PageWriteRequest req))
            {
                WritePageLocked(req, writerRawBytes, compressBuffer);
            }
        }
    }

    private void WritePageLocked(PageWriteRequest req, byte[] rawBytes, MemoryStream compressBuffer)
    {
        if (!pages.TryGetValue(req.Key, out PageEntry? entry)) return;

        string path = GetPagePath(req.PageX, req.PageZ);
        bool needsDiskMerge;
        lock (entry.SyncRoot)
        {
            needsDiskMerge = !entry.DiskReadAttempted;
        }

        // Preserve any previously persisted chunks on disk before overwriting the .omp file
        // if a live chunk write happened before TryReadPage loaded this page.
        if (needsDiskMerge && File.Exists(path) && TryReadPagePayloadFromDisk(path, req.PageX, req.PageZ, rawBytes, out ulong diskBitmask))
        {
            bool enqueueGpu = false;
            lock (entry.SyncRoot)
            {
                if (!entry.DiskReadAttempted)
                {
                    entry.DiskReadAttempted = true;
                    ulong mergedMask = MergeDiskPixelsLocked(entry, rawBytes, diskBitmask);
                    if (mergedMask != 0UL)
                    {
                        entry.FullGpuUploadNeeded = true;
                        if (!entry.QueuedForGpuUpload)
                        {
                            entry.QueuedForGpuUpload = true;
                            enqueueGpu = true;
                        }
                    }
                }
            }

            if (enqueueGpu)
            {
                dirtyGpuPages.Enqueue(req.Key);
            }
        }

        ulong bitmask;
        lock (entry.SyncRoot)
        {
            entry.QueuedForDiskWrite = false;
            entry.DiskReadAttempted = true;
            bitmask = entry.Bitmask;
            Buffer.BlockCopy(entry.Pixels, 0, rawBytes, 0, PageByteSize);
        }

        try
        {
            compressBuffer.SetLength(0);
            compressBuffer.Position = 0;

            Span<byte> header = stackalloc byte[HeaderSize];
            Magic.CopyTo(header);
            header[4] = 1; // version
            BitConverter.TryWriteBytes(header.Slice(5, 4), req.PageX);
            BitConverter.TryWriteBytes(header.Slice(9, 4), req.PageZ);
            BitConverter.TryWriteBytes(header.Slice(13, 8), bitmask);
            compressBuffer.Write(header);

            using (var gzip = new GZipStream(compressBuffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(rawBytes, 0, PageByteSize);
            }

            string tmpPath = path + ".tmp";
            using (var outStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                outStream.Write(compressBuffer.GetBuffer(), 0, (int)compressBuffer.Length);
            }
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (Exception)
        {
            // Disk failure: skip, page will be regenerated next session.
        }
    }

    private static bool TryReadPagePayloadFromDisk(string path, int expectedPageX, int expectedPageZ, byte[] destinationRaw, out ulong diskBitmask)
    {
        diskBitmask = 0UL;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < HeaderSize || stream.Length > MaxPageFileBytes)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[HeaderSize];
            if (!ReadExact(stream, header))
            {
                return false;
            }

            if (header[0] != Magic[0] || header[1] != Magic[1] ||
                header[2] != Magic[2] || header[3] != Magic[3])
            {
                return false;
            }

            if (header[4] != 1)
            {
                return false;
            }

            int storedPageX = BitConverter.ToInt32(header.Slice(5, 4));
            int storedPageZ = BitConverter.ToInt32(header.Slice(9, 4));
            ulong bitmask = BitConverter.ToUInt64(header.Slice(13, 8));

            if (storedPageX != expectedPageX || storedPageZ != expectedPageZ)
            {
                return false;
            }

            if (!DecompressGzipExact(stream, destinationRaw, PageByteSize))
            {
                return false;
            }

            diskBitmask = bitmask;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ulong MergeDiskPixelsLocked(PageEntry entry, byte[] rawBytes, ulong diskBitmask)
    {
        if (entry.Bitmask == 0UL && entry.InvalidatedMask == 0UL && !entry.HasPregen)
        {
            Buffer.BlockCopy(rawBytes, 0, entry.Pixels, 0, PageByteSize);
            entry.Bitmask = diskBitmask;
            return diskBitmask;
        }

        ulong copyMask = diskBitmask & ~(entry.Bitmask | entry.InvalidatedMask);
        ulong remaining = copyMask;
        while (remaining != 0UL)
        {
            int idx = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            int localX = idx % PageLen;
            int localZ = idx / PageLen;
            int basePixelX = localX * GlobalConstants.ChunkSize;
            int basePixelZ = localZ * GlobalConstants.ChunkSize;

            for (int row = 0; row < GlobalConstants.ChunkSize; row++)
            {
                int pixelOffset = (basePixelZ + row) * PagePixelSize + basePixelX;
                Buffer.BlockCopy(rawBytes, pixelOffset * 4, entry.Pixels, pixelOffset * 4, GlobalConstants.ChunkSize * 4);
            }
        }

        entry.Bitmask |= copyMask;
        return copyMask;
    }

    private static bool ReadExact(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination.Slice(total));
            if (read <= 0)
            {
                return false;
            }
            total += read;
        }
        return true;
    }

    private static bool DecompressGzipExact(Stream compressedStream, byte[] destination, int expectedLength)
    {
        try
        {
            using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
            int total = 0;
            while (total < expectedLength)
            {
                int read = gzip.Read(destination, total, expectedLength - total);
                if (read <= 0)
                {
                    return false;
                }
                total += read;
            }

            // Reject streams with trailing uncompressed bytes (blocks gzip bombs).
            return gzip.ReadByte() == -1;
        }
        catch (Exception)
        {
            return false;
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
