using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Decides when the driver's pipeline cache has grown enough to be worth writing out
/// before shutdown.
///
/// A session that crashes, or is killed, loses everything a shutdown-only save would
/// have written. Godot saves from a worker when the cache has grown by some megabytes
/// rather than on a timer (docs/vulkan.md#caches §1, godot#76348); this is that
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

/// <summary>The device and driver a pipeline cache blob was produced by.</summary>
internal readonly record struct PipelineCacheIdentity(uint VendorId, uint DeviceId, uint DriverVersion, byte[] Uuid)
{
    public static PipelineCacheIdentity Of(VulkanCapabilities capabilities) => new(
        capabilities.VendorId, capabilities.DeviceId, capabilities.DriverVersion, capabilities.PipelineCacheUuid);

    /// <summary>One file per GPU, so switching between two GPUs keeps both caches warm.</summary>
    public string FileName => $"{VendorId:x4}-{DeviceId:x4}-{Convert.ToHexStringLower(Uuid)}.bin";
}

/// <summary>
/// The driver's pipeline cache blob on disk, wrapped so that it is only ever handed
/// back to the driver that wrote it, whole.
///
/// The spec says a driver must start empty when the blob's own header does not match
/// it, but drivers have been seen to skip that check and crash after a driver update,
/// to keep the UUID across incompatible builds, and to fail on a zero-length blob;
/// files have been seen truncated, zero-filled and empty. So the wrapper records
/// the vendor, device, driver version, pointer size and UUID alongside a SHA-256 of
/// the blob. Every field and the blob's own Vulkan header are checked before loading,
/// and anything that fails is treated as no cache at all.
/// Design and sources: docs/vulkan.md#caches §1 and "Design for this renderer" item 2.
/// </summary>
internal static class PipelineCacheFile
{
    /// <summary>"OPLC", little-endian.</summary>
    internal const uint FileMagic = 0x434C504F;

    internal const uint FormatVersion = 1;

    /// <summary>Magic, version, data size, SHA-256, vendor, device, driver version, pointer size, UUID.</summary>
    internal const int HeaderSize = 4 + 4 + 8 + 32 + 4 + 4 + 4 + 4 + 16;

    /// <summary>VkPipelineCacheHeaderVersionOne: size, version, vendor, device, UUID.</summary>
    private const int VulkanHeaderSize = 32;

    private const uint VulkanHeaderVersionOne = 1;

    public static string PathFor(string cacheRoot, PipelineCacheIdentity identity) =>
        Path.Combine(cacheRoot, "pipeline", identity.FileName);

    /// <summary>The blob stored for <paramref name="identity" />, or null when there is none it can use.</summary>
    public static byte[]? Load(string path, PipelineCacheIdentity identity)
    {
        byte[]? file = CacheFileWriter.TryReadAll(path);
        return file == null ? null : Unwrap(file, identity);
    }

    /// <summary>Stores a blob; false when it was empty, not a pipeline cache, or could not be written.</summary>
    public static bool Save(string path, byte[] data, PipelineCacheIdentity identity)
    {
        if (!HasMatchingVulkanHeader(data, identity)) return false;
        return CacheFileWriter.WriteAtomically(path, Wrap(data, identity));
    }

    internal static byte[] Wrap(byte[] data, PipelineCacheIdentity identity)
    {
        var file = new byte[HeaderSize + data.Length];
        Span<byte> header = file.AsSpan(0, HeaderSize);
        BitConverter.TryWriteBytes(header[0..], FileMagic);
        BitConverter.TryWriteBytes(header[4..], FormatVersion);
        BitConverter.TryWriteBytes(header[8..], (ulong)data.Length);
        SHA256.HashData(data, header.Slice(16, 32));
        BitConverter.TryWriteBytes(header[48..], identity.VendorId);
        BitConverter.TryWriteBytes(header[52..], identity.DeviceId);
        BitConverter.TryWriteBytes(header[56..], identity.DriverVersion);
        BitConverter.TryWriteBytes(header[60..], (uint)IntPtr.Size);
        identity.Uuid.AsSpan(0, 16).CopyTo(header[64..]);
        data.CopyTo(file, HeaderSize);
        return file;
    }

    /// <summary>The blob inside a file written for exactly <paramref name="identity" />, or null.</summary>
    internal static byte[]? Unwrap(byte[] file, PipelineCacheIdentity identity)
    {
        if (file.Length <= HeaderSize) return null;
        ReadOnlySpan<byte> header = file.AsSpan(0, HeaderSize);
        if (BitConverter.ToUInt32(header[0..]) != FileMagic) return null;
        if (BitConverter.ToUInt32(header[4..]) != FormatVersion) return null;
        if (BitConverter.ToUInt64(header[8..]) != (ulong)(file.Length - HeaderSize)) return null;
        if (BitConverter.ToUInt32(header[48..]) != identity.VendorId) return null;
        if (BitConverter.ToUInt32(header[52..]) != identity.DeviceId) return null;
        if (BitConverter.ToUInt32(header[56..]) != identity.DriverVersion) return null;
        if (BitConverter.ToUInt32(header[60..]) != (uint)IntPtr.Size) return null;
        if (!header.Slice(64, 16).SequenceEqual(identity.Uuid)) return null;

        ReadOnlySpan<byte> data = file.AsSpan(HeaderSize);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        if (!hash.SequenceEqual(header.Slice(16, 32))) return null;

        byte[] blob = data.ToArray();
        return HasMatchingVulkanHeader(blob, identity) ? blob : null;
    }

    /// <summary>The blob's own VkPipelineCacheHeaderVersionOne names this device.</summary>
    internal static bool HasMatchingVulkanHeader(byte[] data, PipelineCacheIdentity identity)
    {
        if (data.Length < VulkanHeaderSize || identity.Uuid is not { Length: 16 }) return false;
        ReadOnlySpan<byte> header = data;
        return BitConverter.ToUInt32(header[0..]) >= VulkanHeaderSize
            && BitConverter.ToUInt32(header[4..]) == VulkanHeaderVersionOne
            && BitConverter.ToUInt32(header[8..]) == identity.VendorId
            && BitConverter.ToUInt32(header[12..]) == identity.DeviceId
            && header.Slice(16, 16).SequenceEqual(identity.Uuid);
    }
}

/// <summary>
/// Replaces a cache file without a reader ever seeing half of it.
///
/// The bytes go to a temporary file unique to this process and call, which is then
/// moved over the destination. Two game instances saving at once lose one of the
/// two writes, never corrupt the file. On Windows a virus scanner briefly holds
/// newly written files open, which makes the move fail; the move is retried with
/// a short backoff before the write is given up (docs/vulkan.md#caches §7).
/// </summary>
internal static class CacheFileWriter
{
    internal const int MoveAttempts = 5;

    /// <summary>Writes <paramref name="bytes" /> to <paramref name="path" />; false when it could not.</summary>
    public static bool WriteAtomically(string path, ReadOnlySpan<byte> bytes) =>
        WriteAtomically(path, bytes, static (from, to) => File.Move(from, to, overwrite: true), Thread.Sleep);

    /// <summary>
    /// The same, with the replace step and the backoff sleep supplied: tests stand in for a
    /// scanner holding the new file open. <paramref name="replace" /> moves its first argument
    /// over its second; <paramref name="sleep" /> takes milliseconds.
    /// </summary>
    internal static bool WriteAtomically(string path, ReadOnlySpan<byte> bytes, Action<string, string> replace,
        Action<int> sleep)
    {
        string temporary = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
            }

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    replace(temporary, path);
                    return true;
                }
                catch (Exception error) when (IsTransient(error) && attempt < MoveAttempts)
                {
                    sleep(10 << attempt);
                }
            }
        }
        catch (Exception error) when (IsTransient(error))
        {
            TryDelete(temporary);
            return false;
        }
    }

    /// <summary>The whole file, or null when it is missing or unreadable.</summary>
    public static byte[]? TryReadAll(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception error) when (IsTransient(error))
        {
            return null;
        }
    }

    private static bool IsTransient(Exception error) => error is IOException or UnauthorizedAccessException;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (IsTransient(error))
        {
        }
    }
}
