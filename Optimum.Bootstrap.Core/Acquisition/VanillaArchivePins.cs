using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Optimum.Bootstrap.Core.Acquisition;

/// <summary>The size and SHA-256 of one official Vintage Story client archive.</summary>
public sealed record VanillaArchivePin(long Bytes, string Sha256);

/// <summary>
/// Known-good fingerprints of the official Vintage Story client that an Optimum release is built against.
/// The installer never reads the player's own game: it downloads the pinned archive, proves it is the
/// official build, and only then extracts and patches it. The pins live in <c>forks.json</c>
/// (<c>vintageStoryPins</c>), next to the version they belong to, so the installer, the bootstrap scripts
/// and the launcher all read one source.
/// </summary>
public interface IVanillaArchivePins
{
    /// <summary>The pin for an archive file name such as <c>vs_install_win-x64_1.22.7.exe</c>, or null when none is pinned.</summary>
    VanillaArchivePin? FindArchive(string archiveFileName);

    /// <summary>
    /// SHA-256 of the extracted files that identify the official build, keyed by file name relative to
    /// the extraction root. Empty when the version is not pinned.
    /// </summary>
    IReadOnlyDictionary<string, string> FindExtractedFiles(string version);
}

public sealed class VanillaArchivePins : IVanillaArchivePins
{
    private static readonly IReadOnlyDictionary<string, string> NoFiles = new Dictionary<string, string>();

    private readonly IReadOnlyDictionary<string, VanillaArchivePin> _archives;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _extractedFilesByVersion;

    public VanillaArchivePins(
        IReadOnlyDictionary<string, VanillaArchivePin> archives,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> extractedFilesByVersion)
    {
        _archives = new Dictionary<string, VanillaArchivePin>(archives, StringComparer.OrdinalIgnoreCase);
        _extractedFilesByVersion = extractedFilesByVersion;
    }

    /// <summary>No pins: every archive is accepted as before. For tests and sources that predate the pins.</summary>
    public static VanillaArchivePins None { get; } = new(
        new Dictionary<string, VanillaArchivePin>(),
        new Dictionary<string, IReadOnlyDictionary<string, string>>());

    /// <summary>
    /// The pins of the source tree at <paramref name="repoRoot"/>, or <see cref="None"/> when it has no
    /// forks.json or no pins. A pin block that exists but is malformed throws <see cref="InvalidDataException"/>:
    /// it is a defect of that release, and treating it as "no pins" would silently skip the verification.
    /// </summary>
    public static VanillaArchivePins ForRepo(string repoRoot)
    {
        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(repoRoot, "forks.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return None;
        }

        return FromJson(text);
    }

    /// <summary>Reads <c>vintageStoryPins</c> from the text of a <c>forks.json</c>.</summary>
    /// <exception cref="InvalidDataException">The pin block is present but malformed.</exception>
    public static VanillaArchivePins FromJson(string forksJson)
    {
        JsonDocument forks;
        try
        {
            forks = JsonDocument.Parse(forksJson);
        }
        catch (JsonException)
        {
            return None; // Not a forks.json at all; whoever needs it reports that.
        }

        using (forks)
        {
            if (forks.RootElement.ValueKind != JsonValueKind.Object
                || !forks.RootElement.TryGetProperty("vintageStoryPins", out JsonElement pins))
            {
                return None;
            }

            try
            {
                return ReadPins(pins);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException
                                           or FormatException or ArgumentException)
            {
                throw new InvalidDataException("forks.json has a malformed vintageStoryPins block: " + ex.Message, ex);
            }
        }
    }

    private static VanillaArchivePins ReadPins(JsonElement pins)
    {
        var archives = new Dictionary<string, VanillaArchivePin>();
        var extracted = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (JsonProperty version in pins.EnumerateObject())
        {
            if (version.Value.ValueKind != JsonValueKind.Object)
                continue; // "_comment"

            if (version.Value.TryGetProperty("archives", out JsonElement archiveList))
            {
                foreach (JsonProperty archive in archiveList.EnumerateObject())
                {
                    archives[archive.Name] = new VanillaArchivePin(
                        archive.Value.GetProperty("bytes").GetInt64(),
                        RequireSha256(archive.Value.GetProperty("sha256"), archive.Name));
                }
            }

            if (version.Value.TryGetProperty("extractedFiles", out JsonElement files))
            {
                extracted[version.Name] = files.EnumerateObject()
                    .ToDictionary(file => file.Name, file => RequireSha256(file.Value, file.Name));
            }
        }

        return new VanillaArchivePins(archives, extracted);
    }

    private static string RequireSha256(JsonElement value, string name)
    {
        string? sha = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (sha is not { Length: 64 } || !sha.All(Uri.IsHexDigit))
            throw new InvalidOperationException($"{name} has no valid SHA-256");
        return sha;
    }

    public VanillaArchivePin? FindArchive(string archiveFileName) =>
        _archives.TryGetValue(archiveFileName, out VanillaArchivePin? pin) ? pin : null;

    public IReadOnlyDictionary<string, string> FindExtractedFiles(string version) =>
        _extractedFilesByVersion.TryGetValue(version, out IReadOnlyDictionary<string, string>? files) ? files : NoFiles;
}
