using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Optimum.Update;

/// <summary>
/// The release manifest (optimum-manifest-&lt;rid&gt;.json), read strictly.
///
/// The manifest decides what is downloaded and which files may be written, so a
/// field that does not look exactly right makes the whole document unusable
/// instead of being skipped. This mirrors how RiftLauncher reads the same file.
/// </summary>
public sealed class OverlayManifest
{
    public const long MaxArchiveBytes = 512L * 1024 * 1024;
    public const int MaxFiles = 10_000;
    public const int MaxManifestBytes = 256 * 1024;

    public static readonly string[] SupportedRids = { "win-x64", "linux-x64" };

    private static readonly Regex VersionPattern = new(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new(@"^sha256:([a-f0-9]{64})$", RegexOptions.CultureInvariant);

    public string OptimumVersion { get; private set; } = "";
    public string Rid { get; private set; } = "";
    public IReadOnlyList<string> SupportedGameVersions { get; private set; } = Array.Empty<string>();
    public string ArchiveFileName { get; private set; } = "";
    public long ArchiveSize { get; private set; }
    /// <summary>Bare lowercase hex.</summary>
    public string ArchiveSha256 { get; private set; } = "";
    public IReadOnlyList<OverlayFile> Files { get; private set; } = Array.Empty<OverlayFile>();

    /// <summary>The one archive name a release for this version and platform may carry.</summary>
    public static string ExpectedArchiveName(string version, string rid) => $"Optimum-v{version}-{rid}-overlay.tar.gz";

    public static bool TryParse(ReadOnlySpan<byte> json, out OverlayManifest? manifest, out string error)
    {
        manifest = null;
        if (json.Length == 0 || json.Length > MaxManifestBytes)
        {
            error = "the manifest is empty or too large";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("the manifest is not an object", out error);

            if (!root.TryGetProperty("manifestVersion", out JsonElement mv) || mv.ValueKind != JsonValueKind.Number || !mv.TryGetInt32(out int mvInt) || mvInt != 1)
                return Fail("unsupported manifest version", out error);

            string? version = ReadString(root, "optimumVersion");
            if (version is null || !VersionPattern.IsMatch(version)) return Fail("the manifest has no valid optimumVersion", out error);

            string? rid = ReadString(root, "rid");
            if (rid is null || Array.IndexOf(SupportedRids, rid) < 0) return Fail("the manifest has no supported rid", out error);

            if (!root.TryGetProperty("supportedGameVersions", out JsonElement games) || games.ValueKind != JsonValueKind.Array)
                return Fail("the manifest lists no supported game versions", out error);
            var gameVersions = new List<string>();
            foreach (JsonElement game in games.EnumerateArray())
            {
                if (game.ValueKind != JsonValueKind.String) return Fail("a supported game version is not text", out error);
                string value = game.GetString()!;
                if (value.Length == 0 || value.Length > 32 || gameVersions.Count >= 32) return Fail("a supported game version is not usable", out error);
                gameVersions.Add(value);
            }
            if (gameVersions.Count == 0) return Fail("the manifest supports no game version", out error);

            if (!root.TryGetProperty("archive", out JsonElement archive) || archive.ValueKind != JsonValueKind.Object)
                return Fail("the manifest describes no archive", out error);
            string? fileName = ReadString(archive, "filename");
            if (fileName is null || fileName != ExpectedArchiveName(version, rid)) return Fail("the archive name is not the one this release must have", out error);
            if (!ReadSize(archive, "size", allowZero: false, out long archiveSize)) return Fail("the archive size is not usable", out error);
            string? archiveHash = ReadHash(archive, "sha256");
            if (archiveHash is null) return Fail("the archive hash is not usable", out error);

            if (!root.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
                return Fail("the manifest lists no files", out error);
            var entries = new List<OverlayFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement file in files.EnumerateArray())
            {
                if (entries.Count >= MaxFiles) return Fail("the manifest lists too many files", out error);
                if (file.ValueKind != JsonValueKind.Object) return Fail("a file entry is not an object", out error);
                string? path = ReadString(file, "path");
                if (path is null || !IsSafeRelativePath(path)) return Fail("a file path is not safe", out error);
                if (!seen.Add(path)) return Fail("a file path is listed twice", out error);
                if (!ReadSize(file, "size", allowZero: true, out long size)) return Fail("a file size is not usable", out error);
                string? hash = ReadHash(file, "sha256");
                if (hash is null) return Fail("a file hash is not usable", out error);
                entries.Add(new OverlayFile(path, size, hash));
            }
            if (entries.Count == 0) return Fail("the manifest lists no files", out error);

            manifest = new OverlayManifest
            {
                OptimumVersion = version,
                Rid = rid,
                SupportedGameVersions = gameVersions,
                ArchiveFileName = fileName,
                ArchiveSize = archiveSize,
                ArchiveSha256 = archiveHash,
                Files = entries,
            };
            error = "";
            return true;
        }
        catch (JsonException)
        {
            return Fail("the manifest is not valid JSON", out error);
        }
    }

    /// <summary>True when this release was published for <paramref name="gameVersion"/>.</summary>
    public bool SupportsGameVersion(string gameVersion)
    {
        foreach (string supported in SupportedGameVersions)
            if (string.Equals(supported, gameVersion, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Compares two major.minor.patch versions. Anything that does not parse compares as 0.0.0.</summary>
    public static int CompareVersions(string a, string b)
    {
        (int, int, int) pa = Parse(a);
        (int, int, int) pb = Parse(b);
        return pa.CompareTo(pb);
    }

    public static bool IsValidVersion(string value) => VersionPattern.IsMatch(value);

    private static (int, int, int) Parse(string value)
    {
        string[] parts = value.Split('.');
        int Part(int i) => i < parts.Length && int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
        return (Part(0), Part(1), Part(2));
    }

    /// <summary>
    /// A path that stays inside the folder it is joined to: relative, forward slashes, no empty or dot segments,
    /// no drive or stream separators, no control characters.
    /// </summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (path.Length == 0 || path.Length > 240) return false;
        if (path[0] == '/' || path.Contains('\\') || path.Contains(':')) return false;
        foreach (char c in path)
            if (c < 0x20 || c == 0x7f || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return false;
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..") return false;
            if (segment.EndsWith(' ') || segment.EndsWith('.')) return false;
        }
        return true;
    }

    private static string? ReadString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadHash(JsonElement parent, string name)
    {
        string? text = ReadString(parent, name);
        if (text is null) return null;
        Match match = HashPattern.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool ReadSize(JsonElement parent, string name, bool allowZero, out long size)
    {
        size = 0;
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out size)) return false;
        if (size > MaxArchiveBytes) return false;
        return allowZero ? size >= 0 : size > 0;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

/// <summary>
/// One file the overlay archive carries, as the release workflow recorded it. A plain class rather than a
/// record: the patcher that transplants this code into the game cannot clone init-only accessors.
/// </summary>
public sealed class OverlayFile
{
    public OverlayFile(string path, long size, string sha256)
    {
        Path = path;
        Size = size;
        Sha256 = sha256;
    }

    public string Path { get; }
    public long Size { get; }
    public string Sha256 { get; }
}
