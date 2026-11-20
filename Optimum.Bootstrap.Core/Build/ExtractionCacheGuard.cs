using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Optimum.Bootstrap.Core.Acquisition;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Build;

/// <summary>Where the bootstrap and packaging scripts keep their extracted Vintage Story client.</summary>
internal readonly record struct ExtractionCachePaths(
    string VanillaRoot,
    string BootstrapRoot,
    string Bootstrap,
    string? PackageRoot,
    string? Package,
    string Legacy)
{
    /// <summary>Every directory the guard may trust or remove, parents before children.</summary>
    public IEnumerable<string> Components()
    {
        yield return VanillaRoot;
        yield return BootstrapRoot;
        yield return Bootstrap;
        if (PackageRoot is not null)
            yield return PackageRoot;
        if (Package is not null)
            yield return Package;
        yield return Legacy;
    }
}

/// <summary>
/// Decides whether the Vintage Story client extracted under <c>.vanilla</c> can be reused. The installer
/// only trusts files it extracted itself from a verified archive, never the player's installed game.
/// An extraction is kept when a stamp records the SHA-256 of the verified archive it came from and its key
/// files still are the official build. The stamp guards against accidents and stale caches, not against
/// someone who can write the folder, who could forge it.
/// </summary>
internal sealed class ExtractionCacheGuard(ISystemProbe probe)
{
    private sealed record VerificationStamp(
        [property: JsonPropertyName("archive")] string Archive,
        [property: JsonPropertyName("sha256")] string Sha256);

    public ExtractionCachePaths PathsFor(string repoRoot)
    {
        string vanillaRoot = Path.Combine(repoRoot, ".vanilla");
        string bootstrapRoot = Path.Combine(vanillaRoot, "win-x64");
        (string? root, string? package) = probe.Os switch
        {
            OsKind.MacOs => PackageCache(
                Path.Combine(vanillaRoot, $"osx-{(probe.Arch == Architecture.Arm64 ? "arm64" : "x64")}"), "Vintage Story.app"),
            OsKind.Linux => PackageCache(Path.Combine(vanillaRoot, "linux-x64"), "vintagestory"),
            _ => (null, null),
        };

        return new ExtractionCachePaths(
            vanillaRoot, bootstrapRoot, Path.Combine(bootstrapRoot, "vintagestory"), root, package,
            Path.Combine(vanillaRoot, "vintagestory"));

        static (string, string) PackageCache(string root, string directory) => (root, Path.Combine(root, directory));
    }

    /// <summary>
    /// Removes the cached extraction when it cannot be trusted, so the next step downloads, verifies and
    /// extracts the pinned archive again. Returns the reason, or null when the cache is kept or absent.
    /// </summary>
    public string? DiscardIfUnverified(string repoRoot, string? version, IVanillaArchivePins pins, Action<string> warn)
    {
        ExtractionCachePaths paths = PathsFor(repoRoot);
        string? problem = FindLink(paths) is { } link
            ? $"{link} is a link to another folder, and the installer only trusts files it extracted itself"
            : version is null ? null : FindCacheProblem(repoRoot, paths, version, pins);
        if (problem is null)
            return null;

        string message =
            $"The cached Vintage Story files do not match the official {version ?? "client"} build ({problem}). " +
            "They will be removed and downloaded and verified again.";
        warn(message);
        try
        {
            // The stamp goes first: a delete that stops partway then leaves no stamp vouching for a half tree.
            DeleteStamp(repoRoot);
            foreach (string directory in new[] { paths.BootstrapRoot, paths.PackageRoot, paths.Legacy }.OfType<string>())
                RemoveTree(repoRoot, directory);

            // bootstrap.sh drops these together with the extraction when it re-extracts.
            RemoveTree(repoRoot, Path.Combine(repoRoot, "build", "snapshot"));
            DeleteFileIfPresent(Path.Combine(repoRoot, ".bootstrap-complete"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"{message} They could not be removed ({ex.Message}). Delete the .vanilla folder and retry.", ex);
        }

        return problem;
    }

    private string? FindCacheProblem(string repoRoot, ExtractionCachePaths paths, string version, IVanillaArchivePins pins)
    {
        IReadOnlyDictionary<string, string> keyFiles = pins.FindExtractedFiles(version);
        if (keyFiles.Count == 0)
            return null;

        string[] caches = new[] { paths.Bootstrap, paths.Package }
            .OfType<string>()
            .Where(directory => Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            .ToArray();
        if (Directory.Exists(paths.Legacy))
            return $"{paths.Legacy} is a leftover cache without a verification stamp";
        if (caches.Length == 0)
            return null;

        string archiveName = ClientArchiveAcquisition.ArchiveFileName(version, probe.Os, probe.Arch);
        if (pins.FindArchive(archiveName) is { } archivePin && !StampMatches(repoRoot, archiveName, archivePin.Sha256))
            return "it was not extracted from a verified archive";

        foreach (string directory in caches)
        {
            if (FirstMismatchedFile(directory, keyFiles) is { } mismatch)
                return mismatch;
        }

        return null;
    }

    private static string? FirstMismatchedFile(string directory, IReadOnlyDictionary<string, string> expected)
    {
        foreach ((string name, string sha256) in expected)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path))
                return $"{name} is missing from {directory}";

            using FileStream stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(sha256, actual, StringComparison.OrdinalIgnoreCase))
                return $"{name} has SHA-256 {actual.ToLowerInvariant()}, expected {sha256}";
        }

        return null;
    }

    /// <summary>The first existing component of the cache path that is a link. RepoRoot itself may be one.</summary>
    private static string? FindLink(ExtractionCachePaths paths) => paths.Components().FirstOrDefault(IsLink);

    private static bool IsLink(string path)
    {
        if (Directory.Exists(path))
            return new DirectoryInfo(path).LinkTarget is not null;
        return File.Exists(path) && new FileInfo(path).LinkTarget is not null;
    }

    /// <summary>Removes a directory tree. A link, or a directory below one, is unlinked and never followed.</summary>
    private static void RemoveTree(string repoRoot, string path)
    {
        if (!Directory.Exists(path) && !IsLink(path))
            return;

        string root = Path.GetFullPath(repoRoot);
        string? link = null;
        for (string? current = Path.GetFullPath(path);
             current is not null && current.Length > root.Length && current.StartsWith(root, StringComparison.Ordinal);
             current = Path.GetDirectoryName(current))
        {
            if (IsLink(current))
                link = current; // keep walking up so the outermost link wins
        }

        if (link is not null)
        {
            Directory.Delete(link, recursive: false);
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Read-only files and directories, as in git or app bundle extractions, block the first attempt.
            ClearReadOnly(path);
            Directory.Delete(path, recursive: true);
        }
    }

    private static void ClearReadOnly(string path)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        // Listed first: a directory that cannot be read yet must be fixed before its children are enumerated.
        foreach (FileSystemInfo entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options).Prepend(new DirectoryInfo(path)).ToList())
        {
            if (!OperatingSystem.IsWindows())
            {
                entry.UnixFileMode |= UnixFileMode.UserRead | UnixFileMode.UserWrite
                                      | (entry is DirectoryInfo ? UnixFileMode.UserExecute : UnixFileMode.None);
            }
            else if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }

    public static string StampPath(string repoRoot) => Path.Combine(repoRoot, ".vanilla", "client-archive-verified.json");

    public static void DeleteStamp(string repoRoot) => DeleteFileIfPresent(StampPath(repoRoot));

    private static void DeleteFileIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static bool StampMatches(string repoRoot, string archiveName, string sha256)
    {
        try
        {
            VerificationStamp? stamp = JsonSerializer.Deserialize<VerificationStamp>(File.ReadAllText(StampPath(repoRoot)));
            return stamp is not null
                && string.Equals(stamp.Archive, archiveName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(stamp.Sha256, sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records that the extraction in <c>.vanilla</c> came from an archive that matched its pin. Call it only
    /// after the build step that extracted it succeeded, so a half finished extraction is never stamped.
    /// A stamp that cannot be written only costs one extraction on the next run, so this never throws.
    /// </summary>
    public bool TryWriteStamp(string repoRoot, string archivePath, IVanillaArchivePins pins)
    {
        string archiveName = Path.GetFileName(archivePath);
        if (pins.FindArchive(archiveName) is not { } pin)
            return true;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StampPath(repoRoot))!);
            File.WriteAllText(StampPath(repoRoot), JsonSerializer.Serialize(new VerificationStamp(archiveName, pin.Sha256)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
