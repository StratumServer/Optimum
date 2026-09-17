using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;
using VkFormat = Silk.NET.Vulkan.Format;
using VkPolygonMode = Silk.NET.Vulkan.PolygonMode;
using VkPrimitiveTopology = Silk.NET.Vulkan.PrimitiveTopology;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The on-disk caches: compiled SPIR-V and the driver's pipeline cache.
///
/// Both files are read back into the driver on the next launch, so the property that
/// matters is that anything not written whole, by this format, for this compiler or
/// this GPU and driver, reads as a miss - never as data. The failure shapes pinned
/// here are the ones seen in the wild (docs/research/vulkan-caching.md §1).
/// </summary>
public sealed class ShaderCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimum-cache-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private const string VertexSource = """
        #version 450
        void main() { gl_Position = vec4(0.0, 0.0, 0.0, 1.0); }
        """;

    /// <summary>A minimal SPIR-V header: magic, version, generator, bound, schema.</summary>
    private static byte[] FakeSpirv(uint bound = 7)
    {
        var words = new uint[] { 0x07230203, 0x00010500, 0, bound, 0, 0x00020011, 0x00000001 };
        return words.SelectMany(BitConverter.GetBytes).ToArray();
    }

    // ------------------------------------------------------------ SPIR-V cache

    [Fact]
    public void AStoredModuleReadsBackIdentically()
    {
        var cache = new ShaderBinaryCache(_root);
        string key = ShaderBinaryCache.KeyFor(VertexSource, EnumShaderType.VertexShader, "compiler-a");
        byte[] spirv = FakeSpirv();

        cache.Put(key, spirv);

        Assert.Equal(spirv, cache.TryGet(key));
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void TheKeyChangesWithCompilerStageAndSource()
    {
        string baseline = ShaderBinaryCache.KeyFor(VertexSource, EnumShaderType.VertexShader, "compiler-a");

        Assert.NotEqual(baseline, ShaderBinaryCache.KeyFor(VertexSource, EnumShaderType.VertexShader, "compiler-b"));
        Assert.NotEqual(baseline, ShaderBinaryCache.KeyFor(VertexSource, EnumShaderType.FragmentShader, "compiler-a"));
        Assert.NotEqual(baseline, ShaderBinaryCache.KeyFor(VertexSource + " ", EnumShaderType.VertexShader, "compiler-a"));
        Assert.Equal(baseline, ShaderBinaryCache.KeyFor(VertexSource, EnumShaderType.VertexShader, "compiler-a"));
    }

    [Fact]
    public void DamagedModuleFilesAreMisses()
    {
        byte[] file = ShaderBinaryCache.Wrap(FakeSpirv());
        Assert.NotNull(ShaderBinaryCache.Unwrap(file));

        // Truncated by an interrupted write.
        Assert.Null(ShaderBinaryCache.Unwrap(file[..^4]));
        // Empty.
        Assert.Null(ShaderBinaryCache.Unwrap(Array.Empty<byte>()));
        // A zero-filled block inside the payload.
        byte[] zeroed = (byte[])file.Clone();
        Array.Clear(zeroed, ShaderBinaryCache.HeaderSize + 8, 8);
        Assert.Null(ShaderBinaryCache.Unwrap(zeroed));
        // A file from another format version.
        byte[] otherVersion = (byte[])file.Clone();
        BitConverter.TryWriteBytes(otherVersion.AsSpan(4), ShaderBinaryCache.FormatVersion + 1);
        Assert.Null(ShaderBinaryCache.Unwrap(otherVersion));
        // Raw SPIR-V dropped in without the header.
        Assert.Null(ShaderBinaryCache.Unwrap(FakeSpirv()));
    }

    [Fact]
    public void ADamagedFileOnDiskIsAMissAndIsReplacedByTheNextPut()
    {
        var cache = new ShaderBinaryCache(_root);
        string key = ShaderBinaryCache.KeyFor(VertexSource, EnumShaderType.VertexShader, "compiler-a");
        cache.Put(key, FakeSpirv());
        string path = Directory.GetFiles(_root, "*.spv", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(path, new byte[File.ReadAllBytes(path).Length]);

        Assert.Null(cache.TryGet(key));

        cache.Put(key, FakeSpirv(bound: 9));
        Assert.Equal(FakeSpirv(bound: 9), cache.TryGet(key));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void SomethingThatIsNotSpirvIsNeverStored()
    {
        var cache = new ShaderBinaryCache(_root);
        cache.Put("aa00", new byte[] { 1, 2, 3, 4 });

        Assert.False(Directory.Exists(_root));
    }

    /// <summary>The compiler's cache hook: the second compile of a source is read, not compiled, and is the same module.</summary>
    [Fact]
    public void TheCompilerServesARepeatedSourceFromTheCache()
    {
        using var compiler = new ShaderCompiler { BinaryCache = new ShaderBinaryCache(_root) };

        ShaderCompileResult first = compiler.Compile(VertexSource, "cached.vsh", EnumShaderType.VertexShader);
        ShaderCompileResult second = compiler.Compile(VertexSource, "renamed.vsh", EnumShaderType.VertexShader);

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.Equal(first.Spirv, second.Spirv);
        Assert.Equal(1, compiler.BinaryCache.Misses);
        Assert.Equal(1, compiler.BinaryCache.Hits);
    }

    [Fact]
    public void TheCompilerIdentityNamesTheOptionsAndTheShadercBuild()
    {
        using var compiler = new ShaderCompiler();

        Assert.StartsWith(ShaderCompiler.OptionsIdentity + ";", compiler.Identity);
        Assert.Matches("(shaderc-sha256:[0-9a-f]{64}|silk-shaderc-.+)$", compiler.Identity);
    }

    [Fact]
    public void AFailedCompileIsNotCached()
    {
        using var compiler = new ShaderCompiler { BinaryCache = new ShaderBinaryCache(_root) };

        Assert.False(compiler.Compile("#version 450\nvoid main() { oops }", "broken.vsh", EnumShaderType.VertexShader).Success);
        Assert.False(Directory.Exists(_root) && Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any());
    }

    // ------------------------------------------------------------ pipeline cache file

    private static PipelineCacheIdentity Identity(uint vendor = 0x10de, uint device = 0x2803, uint driver = 0x8c4a4000,
        byte uuidSeed = 1) =>
        new(vendor, device, driver, Enumerable.Range(uuidSeed, 16).Select(i => (byte)i).ToArray());

    /// <summary>A blob whose VkPipelineCacheHeaderVersionOne names <paramref name="identity" />, plus driver data.</summary>
    private static byte[] DriverBlob(PipelineCacheIdentity identity, int extra = 64)
    {
        var blob = new byte[32 + extra];
        BitConverter.TryWriteBytes(blob.AsSpan(0), 32u);
        BitConverter.TryWriteBytes(blob.AsSpan(4), 1u);
        BitConverter.TryWriteBytes(blob.AsSpan(8), identity.VendorId);
        BitConverter.TryWriteBytes(blob.AsSpan(12), identity.DeviceId);
        identity.Uuid.CopyTo(blob, 16);
        for (int i = 32; i < blob.Length; i++) blob[i] = (byte)(i * 7);
        return blob;
    }

    [Fact]
    public void APipelineCacheReadsBackForTheDeviceThatWroteIt()
    {
        PipelineCacheIdentity identity = Identity();
        string path = PipelineCacheFile.PathFor(_root, identity);
        byte[] blob = DriverBlob(identity);

        Assert.True(PipelineCacheFile.Save(path, blob, identity));

        Assert.Equal(blob, PipelineCacheFile.Load(path, identity));
    }

    [Fact]
    public void APipelineCacheFromAnotherGpuOrDriverIsNotLoaded()
    {
        PipelineCacheIdentity identity = Identity();
        byte[] file = PipelineCacheFile.Wrap(DriverBlob(identity), identity);

        Assert.Null(PipelineCacheFile.Unwrap(file, Identity(vendor: 0x1002)));
        Assert.Null(PipelineCacheFile.Unwrap(file, Identity(device: 0x2804)));
        // A driver update that kept its UUID: the case drivers get wrong.
        Assert.Null(PipelineCacheFile.Unwrap(file, Identity(driver: 0x8c4b0000)));
        Assert.Null(PipelineCacheFile.Unwrap(file, Identity(uuidSeed: 2)));
    }

    [Fact]
    public void DamagedPipelineCacheFilesAreNotLoaded()
    {
        PipelineCacheIdentity identity = Identity();
        byte[] file = PipelineCacheFile.Wrap(DriverBlob(identity), identity);
        Assert.NotNull(PipelineCacheFile.Unwrap(file, identity));

        Assert.Null(PipelineCacheFile.Unwrap(file[..^1], identity));
        Assert.Null(PipelineCacheFile.Unwrap(file[..PipelineCacheFile.HeaderSize], identity));
        Assert.Null(PipelineCacheFile.Unwrap(Array.Empty<byte>(), identity));
        Assert.Null(PipelineCacheFile.Unwrap(new byte[file.Length], identity));
        byte[] flipped = (byte[])file.Clone();
        flipped[^1] ^= 0xff;
        Assert.Null(PipelineCacheFile.Unwrap(flipped, identity));
    }

    [Fact]
    public void ABlobWhoseOwnVulkanHeaderNamesAnotherDeviceIsNeitherSavedNorLoaded()
    {
        PipelineCacheIdentity identity = Identity();
        byte[] foreign = DriverBlob(Identity(device: 0x1234));
        string path = PipelineCacheFile.PathFor(_root, identity);

        Assert.False(PipelineCacheFile.Save(path, foreign, identity));
        Assert.Null(PipelineCacheFile.Unwrap(PipelineCacheFile.Wrap(foreign, identity), identity));
        Assert.False(PipelineCacheFile.Save(path, Array.Empty<byte>(), identity));
    }

    [Fact]
    public void EachGpuHasItsOwnPipelineCacheFile()
    {
        Assert.NotEqual(
            PipelineCacheFile.PathFor(_root, Identity()),
            PipelineCacheFile.PathFor(_root, Identity(device: 0x2804)));
    }

    // ------------------------------------------------------------ location

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("C:/cache", null, "C:/cache")]
    [InlineData("C:/cache", "", "C:/cache")]
    [InlineData("C:/cache", "D:/elsewhere", "D:/elsewhere")]
    [InlineData(null, "D:/elsewhere", "D:/elsewhere")]
    [InlineData("C:/cache", "0", null)]
    [InlineData("C:/cache", "off", null)]
    public void TheEnvironmentOverridesOrDisablesTheCacheDirectory(string? configured, string? environment, string? expected)
    {
        Assert.Equal(expected, VulkanDevice.ResolveShaderCacheRoot(configured, environment));
    }

    // ------------------------------------------------------- pipeline-key log

    private static PipelineKeyLogEntry KeyEntry(ulong program, ulong settings = 1,
        VkFormat color = VkFormat.R8G8B8A8Unorm, bool blend = false) => new()
    {
        SettingsHash = settings,
        ProgramHash = new UInt128(program, ~program),
        Bindings = new[] { new VertexBinding(0, 24, false), new VertexBinding(15, 16, false) },
        Attributes = new[]
        {
            new VertexAttribute(0, 0, VkFormat.R32G32B32Sfloat, 0),
            new VertexAttribute(1, 0, VkFormat.R32G32B32Sfloat, 12),
            new VertexAttribute(2, 15, VkFormat.R32G32B32A32Sfloat, 0),
        },
        ColorFormats = new[] { color, VkFormat.R16G16B16A16Sfloat },
        DepthFormat = VkFormat.D32Sfloat,
        Blend = new[] { AttachmentBlend.Default with { Enabled = blend }, AttachmentBlend.Default },
        PolygonMode = VkPolygonMode.Fill,
        Topology = VkPrimitiveTopology.TriangleList,
    };

    [Fact]
    public void AKeyLogRoundTripsEveryEntryWithItsLastSeenTime()
    {
        var log = new PipelineKeyLog();
        PipelineKeyLogEntry first = KeyEntry(program: 7);
        PipelineKeyLogEntry blended = KeyEntry(program: 7, blend: true);
        PipelineKeyLogEntry otherSettings = KeyEntry(program: 7, settings: 2);
        log.Record(first, 1000);
        log.Record(blended, 2000);
        log.Record(otherSettings, 3000);
        // The same content again is the same entry, seen later.
        log.Record(KeyEntry(program: 7), 4000);

        Assert.Equal(3, log.Count);
        Assert.NotEqual(first.ContentId, blended.ContentId);
        Assert.NotEqual(first.ContentId, otherSettings.ContentId);

        string path = Path.Combine(_root, "pipeline", "gpu.keys");
        Assert.True(log.HasUnsavedChanges);
        Assert.True(log.Save(path));
        Assert.False(log.HasUnsavedChanges);

        PipelineKeyLog loaded = PipelineKeyLog.Load(path);
        Assert.Equal(3, loaded.Count);
        Assert.False(loaded.HasUnsavedChanges);

        List<PipelineKeyLogEntry> matching = loaded.Matching(1, new UInt128(7, ~7ul));
        Assert.Equal(2, matching.Count);
        PipelineKeyLogEntry roundTripped = Assert.Single(matching, e => e.ContentId == first.ContentId);
        Assert.Equal(4000, roundTripped.LastSeenUnixMs);
        Assert.Equal(first.Bindings, roundTripped.Bindings);
        Assert.Equal(first.Attributes, roundTripped.Attributes);
        Assert.Equal(first.ColorFormats, roundTripped.ColorFormats);
        Assert.Equal(first.DepthFormat, roundTripped.DepthFormat);
        Assert.Equal(first.Blend, roundTripped.Blend);
        Assert.Equal(first.PolygonMode, roundTripped.PolygonMode);
        Assert.Equal(first.Topology, roundTripped.Topology);
        Assert.Equal(3000, Assert.Single(loaded.Matching(2, new UInt128(7, ~7ul))).LastSeenUnixMs);
        Assert.Empty(loaded.Matching(1, new UInt128(8, ~8ul)));
    }

    [Fact]
    public void AKeyLogOfAnotherFormatVersionIsDiscarded()
    {
        var log = new PipelineKeyLog();
        log.Record(KeyEntry(program: 1), 10);
        byte[] file = log.Serialize();
        Assert.Equal(1, PipelineKeyLog.Parse(file).Count);

        // A whole, correctly hashed file that only differs in its version: the version check rejects it.
        byte[] future = (byte[])file.Clone();
        BitConverter.TryWriteBytes(future.AsSpan(4), PipelineKeyLog.FormatVersion + 1);
        System.Security.Cryptography.SHA256.HashData(future.AsSpan(0, future.Length - 32), future.AsSpan(future.Length - 32));
        Assert.Equal(0, PipelineKeyLog.Parse(future).Count);
    }

    [Fact]
    public void AKeyLogKeepsOnlyTheMostRecentlyUsedEntriesUpToItsCap()
    {
        var log = new PipelineKeyLog(capacity: 4);
        for (ulong program = 1; program <= 4; program++) log.Record(KeyEntry(program), (long)program);
        // Entry 1 is used again, so entry 2 is now the least recently seen.
        log.Record(KeyEntry(1), 10);
        log.Record(KeyEntry(5), 5);

        PipelineKeyLog loaded = PipelineKeyLog.Parse(log.Serialize(), capacity: 4);
        Assert.Equal(4, loaded.Count);
        Assert.Empty(loaded.Matching(1, new UInt128(2, ~2ul)));
        foreach (ulong kept in new ulong[] { 1, 3, 4, 5 })
        {
            Assert.Single(loaded.Matching(1, new UInt128(kept, ~kept)));
        }

        // A file written with a larger cap is trimmed to the reader's.
        Assert.Equal(2, PipelineKeyLog.Parse(log.Serialize(), capacity: 2).Count);
        Assert.Single(PipelineKeyLog.Parse(log.Serialize(), capacity: 2).Matching(1, new UInt128(1, ~1ul)));
    }

    [Fact]
    public void ACorruptKeyLogIsIgnored()
    {
        var log = new PipelineKeyLog();
        log.Record(KeyEntry(program: 1), 10);
        log.Record(KeyEntry(program: 2), 20);
        byte[] file = log.Serialize();
        Assert.Equal(2, PipelineKeyLog.Parse(file).Count);

        byte[] flipped = (byte[])file.Clone();
        flipped[40] ^= 0x20;
        byte[] garbage = new byte[file.Length];
        new Random(3).NextBytes(garbage);

        Assert.Equal(0, PipelineKeyLog.Parse(null).Count);
        Assert.Equal(0, PipelineKeyLog.Parse(Array.Empty<byte>()).Count);
        Assert.Equal(0, PipelineKeyLog.Parse(file[..^1]).Count);
        Assert.Equal(0, PipelineKeyLog.Parse(file[..(file.Length / 2)]).Count);
        Assert.Equal(0, PipelineKeyLog.Parse(new byte[file.Length]).Count);
        Assert.Equal(0, PipelineKeyLog.Parse(flipped).Count);
        Assert.Equal(0, PipelineKeyLog.Parse(garbage).Count);
        Assert.Equal(0, PipelineKeyLog.Load(Path.Combine(_root, "missing.keys")).Count);

        // A damaged file on disk loads as empty and is replaced whole by the next save.
        string path = Path.Combine(_root, "pipeline", "damaged.keys");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, flipped);
        PipelineKeyLog reloaded = PipelineKeyLog.Load(path);
        Assert.Equal(0, reloaded.Count);
        reloaded.Record(KeyEntry(program: 3), 30);
        Assert.True(reloaded.Save(path));
        Assert.Equal(1, PipelineKeyLog.Load(path).Count);
    }

    [Fact]
    public void TheKeyLogSitsBesideItsGpusPipelineCache()
    {
        PipelineCacheIdentity identity = Identity();
        string keys = PipelineKeyLog.PathFor(_root, identity);
        Assert.Equal(Path.GetDirectoryName(PipelineCacheFile.PathFor(_root, identity)), Path.GetDirectoryName(keys));
        Assert.EndsWith(".keys", keys);
        Assert.NotEqual(keys, PipelineKeyLog.PathFor(_root, Identity(device: 0x2804)));
        Assert.NotEqual(PipelineKeyLog.SettingsHashFor(ColorWriteTier.PipelineKey, false),
            PipelineKeyLog.SettingsHashFor(ColorWriteTier.DynamicMask, true));
    }

    // -------------------------------------------------------------- safe write

    [Fact]
    public void AReplaceBlockedByAScannerIsRetriedWithBackoff()
    {
        string path = Path.Combine(_root, "retry", "cache.bin");
        int calls = 0;
        var sleeps = new List<int>();

        bool written = CacheFileWriter.WriteAtomically(path, new byte[] { 1, 2, 3 }, (from, to) =>
        {
            if (++calls < 3) throw new IOException("the file is held open by another process");
            File.Move(from, to, overwrite: true);
        }, sleeps.Add);

        Assert.True(written);
        Assert.Equal(3, calls);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Equal(2, sleeps.Count);
        Assert.True(sleeps[1] > sleeps[0], "the backoff does not grow");
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void AReplaceThatKeepsFailingGivesUpAndKeepsTheOldFile()
    {
        string path = Path.Combine(_root, "retry", "cache.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 9, 9 });
        int calls = 0;

        bool written = CacheFileWriter.WriteAtomically(path, new byte[] { 1, 2, 3 }, (_, _) =>
        {
            calls++;
            throw new UnauthorizedAccessException("access denied");
        }, _ => { });

        Assert.False(written);
        Assert.Equal(CacheFileWriter.MoveAttempts, calls);
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
}
