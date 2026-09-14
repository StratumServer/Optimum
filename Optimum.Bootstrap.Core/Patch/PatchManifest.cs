using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimum.Bootstrap.Core.Patch;

/// <summary>
/// Machine-readable manifest published alongside release overlays (e.g. optimum-manifest.json).
/// Used by RiftLauncher to discover Optimum variant overlays, verify hashes, and apply patches.
/// </summary>
public sealed record PatchOverlayManifest
{
    public const string FileName = "optimum-manifest.json";

    [JsonPropertyName("manifestVersion")]
    public int ManifestVersion { get; init; } = 1;

    [JsonPropertyName("optimumVersion")]
    public required string OptimumVersion { get; init; }

    [JsonPropertyName("supportedGameVersions")]
    public required IReadOnlyList<string> SupportedGameVersions { get; init; }

    [JsonPropertyName("rid")]
    public required string Rid { get; init; }

    [JsonPropertyName("archive")]
    public PatchOverlayArchive? Archive { get; init; }

    [JsonPropertyName("targets")]
    public IReadOnlyList<PatchTargetDefinition> Targets { get; init; } = [];

    [JsonPropertyName("files")]
    public IReadOnlyList<PatchOverlayFile> Files { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static PatchOverlayManifest? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<PatchOverlayManifest>(json, Json); }
        catch (JsonException) { return null; }
    }
}

public sealed record PatchOverlayArchive
{
    [JsonPropertyName("filename")]
    public required string Filename { get; init; }

    [JsonPropertyName("size")]
    public required long Size { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

public sealed record PatchTargetDefinition
{
    [JsonPropertyName("assembly")]
    public required string Assembly { get; init; }

    [JsonPropertyName("donor")]
    public required string Donor { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("modName")]
    public string? ModName { get; init; }

    [JsonPropertyName("expectedInputSha256")]
    public string? ExpectedInputSha256 { get; init; }

    [JsonPropertyName("expectedOutputSha256")]
    public string? ExpectedOutputSha256 { get; init; }
}

public sealed record PatchOverlayFile
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("size")]
    public required long Size { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

/// <summary>
/// Manifest written into &lt;gameDir&gt;/.optimum/manifest.json after a successful patch run.
/// </summary>
public sealed record PatchInstallManifest
{
    public const string RelativePath = ".optimum/manifest.json";

    [JsonPropertyName("optimumVersion")]
    public required string OptimumVersion { get; init; }

    [JsonPropertyName("patchedAtUtc")]
    public required DateTimeOffset PatchedAtUtc { get; init; }

    [JsonPropertyName("gameDirectory")]
    public required string GameDirectory { get; init; }

    [JsonPropertyName("targets")]
    public IReadOnlyList<PatchTargetRecord> Targets { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static PatchInstallManifest? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<PatchInstallManifest>(json, Json); }
        catch (JsonException) { return null; }
    }
}

public sealed record PatchTargetRecord
{
    [JsonPropertyName("assembly")]
    public required string Assembly { get; init; }

    [JsonPropertyName("vanillaHash")]
    public string? VanillaHash { get; init; }

    [JsonPropertyName("patchedHash")]
    public string? PatchedHash { get; init; }
}
