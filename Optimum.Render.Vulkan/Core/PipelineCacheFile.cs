using System;
using System.IO;
using System.Security.Cryptography;

namespace Optimum.Render.Vulkan.Core;

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
/// Design and sources: docs/research/vulkan-caching.md §1 and "Design for this renderer" item 2.
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
