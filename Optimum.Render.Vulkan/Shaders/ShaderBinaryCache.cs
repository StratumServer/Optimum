using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>
/// Compiled SPIR-V on disk, addressed by everything that decides it.
///
/// Without it every program is compiled from GLSL at every launch - the whole
/// vanilla set, every settings variant the session reaches, every mod shader. The
/// rewritten source of a stage, which already has its defines resolved and its
/// includes expanded, fully determines its SPIR-V for a given compiler build and set
/// of options. The key is a hash of exactly that: this format's version, the
/// compiler identity (<see cref="ShaderCompiler.Identity" />), the stage and the
/// source. A mod that edits a shader changes the source and so misses, rather than
/// loading stale SPIR-V under a file name. Nothing about the program's layout is
/// stored: the layout is rebuilt from the source before the cache is consulted, and
/// the cache only replaces the one step that is expensive.
///
/// Each file carries a small header - magic, format version, payload length and a
/// SHA-256 of the payload - checked before anything is handed to the driver.
/// Anything that fails the check is a miss, never an error: a truncated file from an
/// interrupted write, a zero-filled block, a file another tool dropped in the
/// directory. The miss recompiles and overwrites it. Design and sources:
/// docs/research/vulkan-caching.md §5 and "Design for this renderer" item 1.
/// </summary>
internal sealed class ShaderBinaryCache
{
    private const uint SpirvMagic = 0x07230203;

    /// <summary>"OSPV", little-endian.</summary>
    internal const uint FileMagic = 0x5650534F;

    /// <summary>
    /// Bumped whenever the file layout changes, or whenever something that decides the
    /// SPIR-V for a given source changes without appearing in the key.
    /// </summary>
    internal const uint FormatVersion = 1;

    /// <summary>Magic, format version, payload length, SHA-256 of the payload.</summary>
    internal const int HeaderSize = 4 + 4 + 4 + 32;

    private long _hits;
    private long _misses;
    private long _writeFailures;

    public string Directory { get; }

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long WriteFailures => Interlocked.Read(ref _writeFailures);

    public ShaderBinaryCache(string directory)
    {
        Directory = directory;
    }

    /// <summary>The key for one stage's source under one compiler identity: lowercase hex SHA-256.</summary>
    public static string KeyFor(string code, EnumShaderType stage, string compilerIdentity)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(
            "optimum-spirv-" + FormatVersion + "\n" + compilerIdentity + "\n" + (int)stage + "\n"));
        hash.AppendData(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The cached module for <paramref name="key" />, or null.</summary>
    public byte[]? TryGet(string key)
    {
        byte[]? file = CacheFileWriter.TryReadAll(PathFor(key));
        byte[]? spirv = file == null ? null : Unwrap(file);
        Interlocked.Increment(ref spirv == null ? ref _misses : ref _hits);
        return spirv;
    }

    /// <summary>Stores a module. A failure to write only costs the next launch a compile.</summary>
    public void Put(string key, byte[] spirv)
    {
        if (!IsSpirv(spirv)) return;
        if (!CacheFileWriter.WriteAtomically(PathFor(key), Wrap(spirv)))
        {
            Interlocked.Increment(ref _writeFailures);
        }
    }

    internal static byte[] Wrap(byte[] spirv)
    {
        var file = new byte[HeaderSize + spirv.Length];
        BitConverter.TryWriteBytes(file.AsSpan(0), FileMagic);
        BitConverter.TryWriteBytes(file.AsSpan(4), FormatVersion);
        BitConverter.TryWriteBytes(file.AsSpan(8), (uint)spirv.Length);
        SHA256.HashData(spirv, file.AsSpan(12, 32));
        spirv.CopyTo(file, HeaderSize);
        return file;
    }

    /// <summary>The payload of a well-formed file, or null for anything else.</summary>
    internal static byte[]? Unwrap(byte[] file)
    {
        if (file.Length < HeaderSize) return null;
        if (BitConverter.ToUInt32(file, 0) != FileMagic) return null;
        if (BitConverter.ToUInt32(file, 4) != FormatVersion) return null;
        if (BitConverter.ToUInt32(file, 8) != (uint)(file.Length - HeaderSize)) return null;

        ReadOnlySpan<byte> payload = file.AsSpan(HeaderSize);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        if (!hash.SequenceEqual(file.AsSpan(12, 32))) return null;

        byte[] spirv = payload.ToArray();
        return IsSpirv(spirv) ? spirv : null;
    }

    /// <summary>A module header and a whole number of words; anything else is not SPIR-V.</summary>
    internal static bool IsSpirv(byte[] data) =>
        data.Length >= 20 && data.Length % 4 == 0 && BitConverter.ToUInt32(data, 0) == SpirvMagic;

    // Two hex digits of fan-out keep a directory of a few thousand modules browsable.
    private string PathFor(string key) => Path.Combine(Directory, key[..2], key + ".spv");
}
