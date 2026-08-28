using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimum.Bootstrap.Core.Install;

/// <summary>
/// The record an install leaves behind so <c>optimum uninstall</c> and the
/// upgrade check know exactly what was placed and where. Written to
/// <c>&lt;installDir&gt;/.optimum/install-manifest.json</c>.
/// </summary>
public sealed record InstallManifest
{
    public const string RelativePath = ".optimum/install-manifest.json";

    [JsonPropertyName("optimumVersion")]
    public required string OptimumVersion { get; init; }

    [JsonPropertyName("installedAtUtc")]
    public required DateTimeOffset InstalledAtUtc { get; init; }

    [JsonPropertyName("installDirectory")]
    public required string InstallDirectory { get; init; }

    [JsonPropertyName("dataPath")]
    public string? DataPath { get; init; }

    [JsonPropertyName("launcher")]
    public string? Launcher { get; init; }

    /// <summary>Top-level entries the install created, relative to the install directory.</summary>
    [JsonPropertyName("entries")]
    public required IReadOnlyList<string> Entries { get; init; }

    /// <summary>Absolute paths of shortcuts and menu entries the install wrote outside the install directory.</summary>
    [JsonPropertyName("shortcuts")]
    public IReadOnlyList<string> Shortcuts { get; init; } = [];

    /// <summary>A Windows uninstall registry key to remove, if one was registered.</summary>
    [JsonPropertyName("uninstallRegistryKey")]
    public string? UninstallRegistryKey { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static InstallManifest? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<InstallManifest>(json, Json); }
        catch (JsonException) { return null; }
    }
}
