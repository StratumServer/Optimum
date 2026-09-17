using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Decides when the driver's pipeline cache has grown enough to be worth writing out
/// before shutdown.
///
/// A session that crashes, or is killed, loses everything a shutdown-only save would
/// have written. Godot saves from a worker when the cache has grown by some megabytes
/// rather than on a timer (docs/research/vulkan-caching.md §1, godot#76348); this is that
/// rule, with the size sampled at most once per interval because the size query goes
/// through the driver.
/// </summary>
internal sealed class PipelineCacheGrowthTrigger
{
    public const long DefaultThresholdBytes = 8L * 1024 * 1024;

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly long _thresholdBytes;
    private readonly long _intervalTicks;
    private long _lastSampleTimestamp;
    private long _baselineBytes;

    /// <param name="baselineBytes">What is on disk already: the seed the cache was created from, or 0.</param>
    public PipelineCacheGrowthTrigger(long thresholdBytes, TimeSpan interval, long baselineBytes)
    {
        _thresholdBytes = Math.Max(1, thresholdBytes);
        _intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        _baselineBytes = baselineBytes;
    }

    public long BaselineBytes => Interlocked.Read(ref _baselineBytes);

    /// <summary>
    /// Whether a size sample is due at <paramref name="timestamp" /> (Stopwatch ticks). The
    /// first call only arms the clock; afterwards at most one sample per interval.
    /// </summary>
    public bool SampleDue(long timestamp)
    {
        if (_lastSampleTimestamp == 0)
        {
            _lastSampleTimestamp = timestamp;
            return false;
        }
        if (timestamp - _lastSampleTimestamp < _intervalTicks) return false;
        _lastSampleTimestamp = timestamp;
        return true;
    }

    /// <summary>The cache is at least the threshold larger than what was last written.</summary>
    public bool GrewEnough(long currentBytes) => currentBytes - BaselineBytes >= _thresholdBytes;

    public void NoteSaved(long bytes) => Interlocked.Exchange(ref _baselineBytes, bytes);
}

/// <summary>
/// The pipeline cache and pipeline-key log files of one GPU, and the saves that write them.
///
/// The render thread only calls <see cref="Tick" />, a timestamp comparison; the size query,
/// the serialisation and the file writes run on a thread-pool task, one at a time. The
/// shutdown save stays: <see cref="SaveAtShutdown" /> waits for a running save and writes
/// both files once more.
/// </summary>
internal sealed class PipelineCachePersistence
{
    private readonly PipelineCacheIdentity _identity;
    private readonly PipelineCacheGrowthTrigger _trigger;
    private Task? _pending;
    private long _saves;

    public string CachePath { get; }
    public string KeyLogPath { get; }
    public PipelineKeyLog KeyLog { get; }

    /// <summary>Where a failed write is reported (the validation mirror in the device).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Driver cache files written, opportunistic and shutdown saves together.</summary>
    public long Saves => Interlocked.Read(ref _saves);

    private PipelineCachePersistence(string cachePath, string keyLogPath, PipelineCacheIdentity identity,
        PipelineKeyLog keyLog, PipelineCacheGrowthTrigger trigger)
    {
        CachePath = cachePath;
        KeyLogPath = keyLogPath;
        _identity = identity;
        KeyLog = keyLog;
        _trigger = trigger;
    }

    /// <summary>Loads both files for <paramref name="identity" />; <paramref name="seed" /> is the usable driver blob or null.</summary>
    public static PipelineCachePersistence Open(string cacheRoot, PipelineCacheIdentity identity, out byte[]? seed,
        long thresholdBytes = PipelineCacheGrowthTrigger.DefaultThresholdBytes, TimeSpan? interval = null)
    {
        string cachePath = PipelineCacheFile.PathFor(cacheRoot, identity);
        string keyLogPath = PipelineKeyLog.PathFor(cacheRoot, identity);
        seed = PipelineCacheFile.Load(cachePath, identity);
        return new PipelineCachePersistence(cachePath, keyLogPath, identity, PipelineKeyLog.Load(keyLogPath),
            new PipelineCacheGrowthTrigger(thresholdBytes, interval ?? PipelineCacheGrowthTrigger.DefaultInterval,
                seed?.Length ?? 0));
    }

    /// <summary>
    /// Render thread, once per frame: starts a background save pass when a sample is due
    /// and no pass is running. True when one started.
    /// </summary>
    public bool Tick(GraphicsPipelineCache cache, long timestamp)
    {
        if (_pending is { IsCompleted: false }) return false;
        if (!_trigger.SampleDue(timestamp)) return false;
        _pending = Task.Run(() => BackgroundPass(cache));
        return true;
    }

    /// <summary>Waits for a running background pass. Before the cache is disposed.</summary>
    public void WaitForPendingSave()
    {
        Task? pending = _pending;
        if (pending == null) return;
        try
        {
            pending.Wait();
        }
        catch (AggregateException error)
        {
            Log?.Invoke("--- pipeline cache background save failed: " + error.InnerException?.Message);
        }
    }

    public void SaveAtShutdown(GraphicsPipelineCache cache)
    {
        WaitForPendingSave();
        SaveDriverCache(cache);
        if (KeyLog.HasUnsavedChanges && !KeyLog.Save(KeyLogPath))
        {
            Log?.Invoke("--- pipeline key log not saved to " + KeyLogPath);
        }
    }

    private void BackgroundPass(GraphicsPipelineCache cache)
    {
        long size = cache.DriverCacheSize();
        VulkanStats.NotePipelineCacheBytes(size);
        if (_trigger.GrewEnough(size)) SaveDriverCache(cache);
        if (KeyLog.HasUnsavedChanges && !KeyLog.Save(KeyLogPath))
        {
            Log?.Invoke("--- pipeline key log not saved to " + KeyLogPath);
        }
    }

    private void SaveDriverCache(GraphicsPipelineCache cache)
    {
        byte[] blob = cache.SerializeDriverCache();
        if (blob.Length == 0) return;
        VulkanStats.NotePipelineCacheBytes(blob.Length);
        if (!PipelineCacheFile.Save(CachePath, blob, _identity))
        {
            Log?.Invoke("--- pipeline cache not saved to " + CachePath);
            return;
        }
        _trigger.NoteSaved(blob.Length);
        Interlocked.Increment(ref _saves);
        VulkanStats.NotePipelineCacheSave();
    }
}
