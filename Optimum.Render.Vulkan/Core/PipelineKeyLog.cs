using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// One pipeline the renderer built, described by what decides it rather than by the
/// session's interned ids.
///
/// A <see cref="PipelineKey" /> names its program, vertex layout, target formats and
/// blend set by ids handed out in first-use order, which mean nothing in the next
/// launch. This entry carries the content those ids stood for: the program's SPIR-V
/// hash (<see cref="ShaderProgramResources.SourceHash" />, which already has the
/// program's defines resolved), the vertex layout, the attachment formats, the blend
/// state per attachment, polygon mode and topology, plus the settings hash of the
/// device-wide state that shapes every pipeline (<see cref="PipelineKeyLog.SettingsHashFor" />).
/// Design: docs/research/vulkan-caching.md "Design for this renderer" item 5.
/// </summary>
internal sealed class PipelineKeyLogEntry
{
    /// <summary>More than any attachment or attribute count Vulkan allows; a larger count is a damaged file.</summary>
    private const int MaxArrayLength = 64;

    public required ulong SettingsHash { get; init; }
    public required UInt128 ProgramHash { get; init; }
    public required VertexBinding[] Bindings { get; init; }
    public required VertexAttribute[] Attributes { get; init; }
    public required Format[] ColorFormats { get; init; }
    public required Format DepthFormat { get; init; }

    /// <summary>Exactly one per colour format: the pipeline builder pads a short blend array with the default.</summary>
    public required AttachmentBlend[] Blend { get; init; }

    public required PolygonMode PolygonMode { get; init; }
    public required PrimitiveTopology Topology { get; init; }

    /// <summary>Unix milliseconds of the last session that used this pipeline; the LRU order.</summary>
    public long LastSeenUnixMs { get; set; }

    private UInt128? _contentId;

    /// <summary>A hash of everything above except <see cref="LastSeenUnixMs" />: equal ids build equal pipelines.</summary>
    public UInt128 ContentId => _contentId ??= ComputeContentId();

    public static PipelineKeyLogEntry From(ulong settingsHash, GraphicsPipelineCache.PipelineRequest request)
    {
        Format[] colors = request.Targets.ColorFormats;
        var blend = new AttachmentBlend[colors.Length];
        for (int i = 0; i < blend.Length; i++)
        {
            blend[i] = i < request.Blend.Length ? request.Blend[i] : AttachmentBlend.Default;
        }

        return new PipelineKeyLogEntry
        {
            SettingsHash = settingsHash,
            ProgramHash = request.Program.SourceHash,
            Bindings = (VertexBinding[])request.VertexLayout.Bindings.Clone(),
            Attributes = (VertexAttribute[])request.VertexLayout.Attributes.Clone(),
            ColorFormats = (Format[])colors.Clone(),
            DepthFormat = request.Targets.DepthFormat,
            Blend = blend,
            PolygonMode = request.PolygonMode,
            Topology = request.Topology,
        };
    }

    /// <summary>The request that rebuilds this pipeline for <paramref name="program" />, whose hash must match.</summary>
    public GraphicsPipelineCache.PipelineRequest ToRequest(ShaderProgramResources program) => new()
    {
        Program = program,
        VertexLayout = new VertexLayoutDescription(Bindings, Attributes),
        Targets = new RenderTargetFormats(ColorFormats, DepthFormat),
        Blend = Blend,
        PolygonMode = PolygonMode,
        Topology = Topology,
    };

    private UInt128 ComputeContentId()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteContent(writer);
        }
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length), hash);
        return new UInt128(BitConverter.ToUInt64(hash[..8]), BitConverter.ToUInt64(hash.Slice(8, 8)));
    }

    internal void WriteContent(BinaryWriter writer)
    {
        writer.Write(SettingsHash);
        writer.Write((ulong)(ProgramHash >> 64));
        writer.Write((ulong)ProgramHash);

        writer.Write(Bindings.Length);
        foreach (VertexBinding binding in Bindings)
        {
            writer.Write(binding.Binding);
            writer.Write(binding.Stride);
            writer.Write(binding.PerInstance);
        }

        writer.Write(Attributes.Length);
        foreach (VertexAttribute attribute in Attributes)
        {
            writer.Write(attribute.Location);
            writer.Write(attribute.Binding);
            writer.Write((int)attribute.Format);
            writer.Write(attribute.Offset);
        }

        writer.Write(ColorFormats.Length);
        foreach (Format format in ColorFormats) writer.Write((int)format);
        writer.Write((int)DepthFormat);

        writer.Write(Blend.Length);
        foreach (AttachmentBlend blend in Blend)
        {
            writer.Write(blend.Enabled);
            writer.Write((int)blend.SrcColor);
            writer.Write((int)blend.DstColor);
            writer.Write((int)blend.ColorOp);
            writer.Write((int)blend.SrcAlpha);
            writer.Write((int)blend.DstAlpha);
            writer.Write((int)blend.AlphaOp);
            writer.Write((uint)blend.WriteMask);
        }

        writer.Write((int)PolygonMode);
        writer.Write((int)Topology);
    }

    /// <summary>One entry's content, or null when a count is out of range. Truncation throws EndOfStreamException.</summary>
    internal static PipelineKeyLogEntry? ReadContent(BinaryReader reader)
    {
        ulong settingsHash = reader.ReadUInt64();
        ulong programHigh = reader.ReadUInt64();
        ulong programLow = reader.ReadUInt64();

        int bindingCount = reader.ReadInt32();
        if ((uint)bindingCount > MaxArrayLength) return null;
        var bindings = new VertexBinding[bindingCount];
        for (int i = 0; i < bindingCount; i++)
        {
            bindings[i] = new VertexBinding(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadBoolean());
        }

        int attributeCount = reader.ReadInt32();
        if ((uint)attributeCount > MaxArrayLength) return null;
        var attributes = new VertexAttribute[attributeCount];
        for (int i = 0; i < attributeCount; i++)
        {
            attributes[i] = new VertexAttribute(reader.ReadUInt32(), reader.ReadUInt32(), (Format)reader.ReadInt32(),
                reader.ReadUInt32());
        }

        int colorCount = reader.ReadInt32();
        if ((uint)colorCount > MaxArrayLength) return null;
        var colors = new Format[colorCount];
        for (int i = 0; i < colorCount; i++) colors[i] = (Format)reader.ReadInt32();
        var depth = (Format)reader.ReadInt32();

        int blendCount = reader.ReadInt32();
        if (blendCount != colorCount) return null;
        var blend = new AttachmentBlend[blendCount];
        for (int i = 0; i < blendCount; i++)
        {
            blend[i] = new AttachmentBlend
            {
                Enabled = reader.ReadBoolean(),
                SrcColor = (BlendFactor)reader.ReadInt32(),
                DstColor = (BlendFactor)reader.ReadInt32(),
                ColorOp = (BlendOp)reader.ReadInt32(),
                SrcAlpha = (BlendFactor)reader.ReadInt32(),
                DstAlpha = (BlendFactor)reader.ReadInt32(),
                AlphaOp = (BlendOp)reader.ReadInt32(),
                WriteMask = (ColorComponentFlags)reader.ReadUInt32(),
            };
        }

        return new PipelineKeyLogEntry
        {
            SettingsHash = settingsHash,
            ProgramHash = new UInt128(programHigh, programLow),
            Bindings = bindings,
            Attributes = attributes,
            ColorFormats = colors,
            DepthFormat = depth,
            Blend = blend,
            PolygonMode = (PolygonMode)reader.ReadInt32(),
            Topology = (PrimitiveTopology)reader.ReadInt32(),
        };
    }
}

/// <summary>
/// Every pipeline the renderer has used, kept next to the driver's pipeline cache so the
/// next launch can build them on a background worker before the first draw asks
/// (docs/research/vulkan-caching.md §6 and "Design for this renderer" item 5).
///
/// Settings combinations make such a log grow without bound (§7), so it is capped by
/// entry count and evicts the entries whose last use is oldest. The file is versioned
/// and carries a SHA-256 of its contents; a file of another version, a truncated or
/// damaged one, reads as an empty log - it only ever costs a prewarm, never a crash.
/// </summary>
internal sealed class PipelineKeyLog
{
    /// <summary>"OPKL", little-endian.</summary>
    internal const uint FileMagic = 0x4C4B504F;

    /// <summary>Bumped whenever the entry layout or the meaning of a hash in it changes.</summary>
    internal const uint FormatVersion = 1;

    public const int DefaultCapacity = 4096;

    private const int HeaderSize = 4 + 4 + 4;
    private const int TrailerSize = 32;

    private readonly object _lock = new();
    private readonly Dictionary<UInt128, PipelineKeyLogEntry> _entries = new();
    private long _changes;
    private long _savedChanges;

    public PipelineKeyLog(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>Something was recorded since the log was loaded or last saved.</summary>
    public bool HasUnsavedChanges
    {
        get { lock (_lock) return _changes != _savedChanges; }
    }

    /// <summary>
    /// The device-wide state every pipeline of a cache is built with beyond its request:
    /// the colour write tier (which states are dynamic) and dynamic blend. Program defines
    /// are not here - they are part of the SPIR-V the program hash covers.
    /// </summary>
    public static ulong SettingsHashFor(ColorWriteTier tier, bool dynamicBlend)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(
            "optimum-pipeline-settings-" + FormatVersion + "|" + (int)tier + "|" + (dynamicBlend ? 1 : 0)), hash);
        return BitConverter.ToUInt64(hash[..8]);
    }

    /// <summary>Beside the driver cache file of the same GPU.</summary>
    public static string PathFor(string cacheRoot, PipelineCacheIdentity identity) =>
        Path.Combine(cacheRoot, "pipeline", Path.ChangeExtension(identity.FileName, ".keys"));

    /// <summary>Notes a pipeline used now; an entry already present only moves its last-seen time.</summary>
    public void Record(PipelineKeyLogEntry entry, long nowUnixMs)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(entry.ContentId, out PipelineKeyLogEntry? existing))
            {
                if (existing.LastSeenUnixMs >= nowUnixMs) return;
                existing.LastSeenUnixMs = nowUnixMs;
            }
            else
            {
                entry.LastSeenUnixMs = nowUnixMs;
                _entries[entry.ContentId] = entry;
                // Amortised: evict only once the log is a quarter over its cap.
                if (_entries.Count > Capacity + Capacity / 4) TrimLocked();
            }
            _changes++;
        }
    }

    /// <summary>The entries built for this settings hash and program.</summary>
    public List<PipelineKeyLogEntry> Matching(ulong settingsHash, UInt128 programHash)
    {
        lock (_lock)
        {
            return _entries.Values.Where(e => e.SettingsHash == settingsHash && e.ProgramHash == programHash).ToList();
        }
    }

    /// <summary>The file bytes: at most <see cref="Capacity" /> entries, most recently used first.</summary>
    public byte[] Serialize()
    {
        lock (_lock) return SerializeLocked();
    }

    private byte[] SerializeLocked()
    {
        TrimLocked();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FileMagic);
            writer.Write(FormatVersion);
            writer.Write(_entries.Count);
            foreach (PipelineKeyLogEntry entry in _entries.Values.OrderByDescending(e => e.LastSeenUnixMs))
            {
                entry.WriteContent(writer);
                writer.Write(entry.LastSeenUnixMs);
            }
        }

        long length = stream.Length;
        var file = new byte[length + TrailerSize];
        stream.GetBuffer().AsSpan(0, (int)length).CopyTo(file);
        SHA256.HashData(file.AsSpan(0, (int)length), file.AsSpan((int)length, TrailerSize));
        return file;
    }

    /// <summary>A log from file bytes; anything not written whole by this version is an empty log.</summary>
    public static PipelineKeyLog Parse(byte[]? file, int capacity = DefaultCapacity)
    {
        var log = new PipelineKeyLog(capacity);
        if (file == null || file.Length < HeaderSize + TrailerSize) return log;
        if (BitConverter.ToUInt32(file, 0) != FileMagic) return log;
        if (BitConverter.ToUInt32(file, 4) != FormatVersion) return log;

        int payload = file.Length - TrailerSize;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(file.AsSpan(0, payload), hash);
        if (!hash.SequenceEqual(file.AsSpan(payload, TrailerSize))) return log;

        var parsed = new Dictionary<UInt128, PipelineKeyLogEntry>();
        try
        {
            using var stream = new MemoryStream(file, 0, payload, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            reader.ReadUInt32();
            reader.ReadUInt32();
            int count = reader.ReadInt32();
            if (count < 0) return log;
            for (int i = 0; i < count; i++)
            {
                PipelineKeyLogEntry? entry = PipelineKeyLogEntry.ReadContent(reader);
                if (entry == null) return log;
                entry.LastSeenUnixMs = reader.ReadInt64();
                parsed[entry.ContentId] = entry;
            }
            if (stream.Position != payload) return log;
        }
        catch (EndOfStreamException)
        {
            return log;
        }

        foreach (KeyValuePair<UInt128, PipelineKeyLogEntry> entry in parsed) log._entries.Add(entry.Key, entry.Value);
        log.TrimLocked();
        return log;
    }

    public static PipelineKeyLog Load(string path, int capacity = DefaultCapacity) =>
        Parse(CacheFileWriter.TryReadAll(path), capacity);

    /// <summary>Writes the log; false when it could not. Safe from any thread.</summary>
    public bool Save(string path)
    {
        long changes;
        byte[] bytes;
        lock (_lock)
        {
            changes = _changes;
            bytes = SerializeLocked();
        }
        if (!CacheFileWriter.WriteAtomically(path, bytes)) return false;
        lock (_lock) _savedChanges = Math.Max(_savedChanges, changes);
        return true;
    }

    /// <summary>Drops the least recently used entries beyond <see cref="Capacity" />.</summary>
    private void TrimLocked()
    {
        int excess = _entries.Count - Capacity;
        if (excess <= 0) return;
        foreach (PipelineKeyLogEntry stale in _entries.Values.OrderBy(e => e.LastSeenUnixMs).Take(excess).ToList())
        {
            _entries.Remove(stale.ContentId);
        }
    }
}
