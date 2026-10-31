using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Optimum.Update;

/// <summary>
/// Why a stage failed. Integer constants rather than an enum: the patcher that transplants this code into the
/// game cannot resolve an enum-typed constant in a type it has just cloned.
/// </summary>
public static class UpdateFailure
{
    public const int None = 0;
    public const int NotConfigured = 1;
    public const int UnsupportedInstall = 2;
    public const int Network = 3;
    public const int BadManifest = 4;
    public const int BadSignature = 5;
    public const int NotNewer = 6;
    public const int UnsupportedGameVersion = 7;
    public const int BadArchive = 8;
    public const int Disk = 9;
    public const int Busy = 10;
    public const int Cancelled = 11;
}

public sealed class StageProgress
{
    public StageProgress(string phase, long done, long total)
    {
        Phase = phase;
        Done = done;
        Total = total;
    }

    public string Phase { get; }
    public long Done { get; }
    public long Total { get; }
}

public sealed class StageResult
{
    public StageResult(bool ok, int failure, string message, string? stagedDirectory = null, OverlayManifest? manifest = null)
    {
        Ok = ok;
        Failure = failure;
        Message = message;
        StagedDirectory = stagedDirectory;
        Manifest = manifest;
    }

    public bool Ok { get; }
    public int Failure { get; }
    public string Message { get; }
    public string? StagedDirectory { get; }
    public OverlayManifest? Manifest { get; }

    public static StageResult Fail(int failure, string message) => new(false, failure, message);
}

/// <summary>
/// Downloads a release overlay and stages it beside the install, touching nothing the game has loaded.
///
/// Order matters and is the point of this class: the signed manifest is trusted first, the archive is
/// trusted only because the manifest names its hash, and each extracted file is trusted only because
/// the manifest names its hash. Nothing the manifest does not list is written. The staged folder
/// appears in one rename, so a partial download or a killed game never leaves a half-staged update
/// that the apply step could mistake for a whole one.
///
/// Everything here is synchronous and runs on a thread the caller owns. The code is transplanted into the game
/// assembly by the patcher, which handles plain methods reliably and async state machines and lambdas poorly.
/// </summary>
public sealed class OverlayStager
{
    public const string DefaultReleaseBase = "https://github.com/StratumServer/Optimum/releases/download/";
    public const string PendingFolder = ".optimum/pending";
    public const string StagedMarker = "staged.json";
    /// <summary>The exact signed manifest bytes and signature, kept beside the files so the apply step can verify them again.</summary>
    public const string StagedManifest = "staged-manifest.json";
    public const string StagedManifestSignature = "staged-manifest.json.sig";

    private readonly HttpClient http;
    private readonly Uri releaseBase;
    private readonly string publicKey;

    public OverlayStager(HttpClient http, string? releaseBase = null, string? publicKeySpkiBase64 = null)
    {
        this.http = http;
        this.releaseBase = new Uri(releaseBase ?? DefaultReleaseBase, UriKind.Absolute);
        publicKey = publicKeySpkiBase64 ?? OptimumUpdateTrust.ManifestPublicKey;
    }

    public StageResult Stage(
        string gameDir,
        string installedVersion,
        string gameVersion,
        string rid,
        string targetVersion,
        Action<StageProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? tempRoot = null;
        try
        {
            if (publicKey.Length == 0)
                return StageResult.Fail(UpdateFailure.NotConfigured, "this build has no release signing key");
            if (!InstallLayout.IsPatchedInPlace(gameDir))
                return StageResult.Fail(UpdateFailure.UnsupportedInstall, "this install is not patched in place");
            if (Array.IndexOf(OverlayManifest.SupportedRids, rid) < 0 || !OverlayManifest.IsValidVersion(targetVersion))
                return StageResult.Fail(UpdateFailure.BadManifest, "the platform or version is not valid");
            if (OverlayManifest.CompareVersions(targetVersion, installedVersion) <= 0)
                return StageResult.Fail(UpdateFailure.NotNewer, "the release is not newer than the installed version");

            string tag = "v" + targetVersion;
            Report(progress, "manifest", 0, 0);
            byte[] manifestBytes = GetBytes(new Uri(releaseBase, tag + "/optimum-manifest-" + rid + ".json"), OverlayManifest.MaxManifestBytes, cancellationToken);
            byte[] signature = GetBytes(new Uri(releaseBase, tag + "/optimum-manifest-" + rid + ".json.sig"), OptimumUpdateTrust.MaxSignatureBytes, cancellationToken);

            if (!ManifestSignature.Verify(manifestBytes, signature, publicKey))
                return StageResult.Fail(UpdateFailure.BadSignature, "the manifest signature does not verify");
            if (!OverlayManifest.TryParse(manifestBytes, out OverlayManifest? manifest, out string parseError) || manifest is null)
                return StageResult.Fail(UpdateFailure.BadManifest, parseError);
            if (manifest.OptimumVersion != targetVersion || manifest.Rid != rid)
                return StageResult.Fail(UpdateFailure.BadManifest, "the manifest is for a different version or platform");
            if (!manifest.SupportsGameVersion(gameVersion))
                return StageResult.Fail(UpdateFailure.UnsupportedGameVersion, "this release does not support game version " + gameVersion);

            string pending = Path.Combine(gameDir, PendingFolder.Replace('/', Path.DirectorySeparatorChar));
            string finalDir = Path.Combine(pending, manifest.OptimumVersion + "-" + manifest.Rid);
            Directory.CreateDirectory(pending);
            SweepTemporaryFolders(pending);

            if (IsStaged(finalDir, manifest, publicKey))
                return new StageResult(true, UpdateFailure.None, "already staged", finalDir, manifest);

            tempRoot = Path.Combine(pending, ".tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            string archivePath = Path.Combine(tempRoot, "overlay.tar.gz");

            DownloadArchive(new Uri(releaseBase, tag + "/" + manifest.ArchiveFileName), manifest, archivePath, progress, cancellationToken);

            string extractTo = Path.Combine(tempRoot, "overlay");
            Report(progress, "verify", 0, manifest.Files.Count);
            ExtractAndVerify(archivePath, extractTo, manifest, progress, cancellationToken);

            File.WriteAllBytes(Path.Combine(extractTo, StagedManifest), manifestBytes);
            File.WriteAllBytes(Path.Combine(extractTo, StagedManifestSignature), signature);
            File.WriteAllText(Path.Combine(extractTo, StagedMarker), JsonSerializer.Serialize(new StagedMarkerData(manifest.OptimumVersion, manifest.Rid, manifest.ArchiveSha256, DateTimeOffset.UtcNow)));
            if (Directory.Exists(finalDir)) Directory.Delete(finalDir, recursive: true);
            Directory.Move(extractTo, finalDir);
            return new StageResult(true, UpdateFailure.None, "staged", finalDir, manifest);
        }
        catch (OperationCanceledException)
        {
            return StageResult.Fail(UpdateFailure.Cancelled, "cancelled");
        }
        catch (HttpRequestException ex)
        {
            return StageResult.Fail(UpdateFailure.Network, ex.Message);
        }
        catch (OverlayException ex)
        {
            return StageResult.Fail(ex.Failure, ex.Message);
        }
        catch (IOException ex)
        {
            return StageResult.Fail(UpdateFailure.Disk, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StageResult.Fail(UpdateFailure.Disk, ex.Message);
        }
        finally
        {
            if (tempRoot is not null) TryDelete(tempRoot);
        }
    }

    private static void Report(Action<StageProgress>? progress, string phase, long done, long total)
    {
        if (progress is not null) progress(new StageProgress(phase, done, total));
    }

    private HttpResponseMessage Get(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        HttpResponseMessage response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch (Exception)
        {
            response.Dispose();
            throw;
        }
    }

    private byte[] GetBytes(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = Get(uri, cancellationToken);
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw new OverlayException(UpdateFailure.BadManifest, "a release file is larger than allowed");

        using Stream stream = response.Content.ReadAsStream(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length + read > maxBytes)
                throw new OverlayException(UpdateFailure.BadManifest, "a release file is larger than allowed");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private void DownloadArchive(Uri uri, OverlayManifest manifest, string path, Action<StageProgress>? progress, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = Get(uri, cancellationToken);
        if (response.Content.Headers.ContentLength is long length && length != manifest.ArchiveSize)
            throw new OverlayException(UpdateFailure.BadArchive, "the archive size differs from the manifest");

        using Stream source = response.Content.ReadAsStream(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
        {
            byte[] chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += read;
                if (total > manifest.ArchiveSize)
                    throw new OverlayException(UpdateFailure.BadArchive, "the archive is larger than the manifest says");
                hash.AppendData(chunk, 0, read);
                target.Write(chunk, 0, read);
                Report(progress, "download", total, manifest.ArchiveSize);
            }
            if (total != manifest.ArchiveSize)
                throw new OverlayException(UpdateFailure.BadArchive, "the archive is shorter than the manifest says");
        }

        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new OverlayException(UpdateFailure.BadArchive, "the archive hash does not match the manifest");
    }

    private static void ExtractAndVerify(string archivePath, string destination, OverlayManifest manifest, Action<StageProgress>? progress, CancellationToken cancellationToken)
    {
        string top = manifest.ArchiveFileName.Substring(0, manifest.ArchiveFileName.Length - ".tar.gz".Length);
        var expected = new Dictionary<string, OverlayFile>(StringComparer.OrdinalIgnoreCase);
        foreach (OverlayFile file in manifest.Files) expected[file.Path] = file;
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string manifestCopy = "optimum-manifest.json";
        string manifestCopyForRid = "optimum-manifest-" + manifest.Rid + ".json";
        string root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        long totalBytes = 0;

        using FileStream archive = File.OpenRead(archivePath);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: false);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue;

            string name = entry.Name.Replace('\\', '/');
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
            if (name.EndsWith('/')) name = name.TrimEnd('/');
            if (name != top && !name.StartsWith(top + "/", StringComparison.Ordinal))
                throw new OverlayException(UpdateFailure.BadArchive, "the archive has an entry outside its folder");
            string relative = name == top ? "" : name.Substring(top.Length + 1);

            if (entry.EntryType == TarEntryType.Directory)
            {
                if (relative.Length != 0 && !OverlayManifest.IsSafeRelativePath(relative))
                    throw new OverlayException(UpdateFailure.BadArchive, "the archive has an unsafe folder name");
                continue;
            }
            if (entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile)
                throw new OverlayException(UpdateFailure.BadArchive, "the archive has an entry that is not a plain file");
            if (!OverlayManifest.IsSafeRelativePath(relative))
                throw new OverlayException(UpdateFailure.BadArchive, "the archive has an unsafe file name");

            bool isManifestCopy = relative == manifestCopy || relative == manifestCopyForRid;
            if (!isManifestCopy && !expected.ContainsKey(relative))
                throw new OverlayException(UpdateFailure.BadArchive, "the archive carries a file the manifest does not list");
            if (!written.Add(relative))
                throw new OverlayException(UpdateFailure.BadArchive, "the archive carries a file twice");

            string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new OverlayException(UpdateFailure.BadArchive, "the archive tries to write outside its folder");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            long limit = isManifestCopy ? OverlayManifest.MaxManifestBytes : expected[relative].Size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            using (Stream? data = entry.DataStream)
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (data is not null)
                {
                    byte[] chunk = new byte[81920];
                    int read;
                    while ((read = data.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        size += read;
                        totalBytes += read;
                        if (size > limit || totalBytes > OverlayManifest.MaxArchiveBytes)
                            throw new OverlayException(UpdateFailure.BadArchive, relative + " is larger than the manifest says");
                        hash.AppendData(chunk, 0, read);
                        output.Write(chunk, 0, read);
                    }
                }
            }

            if (!isManifestCopy)
            {
                OverlayFile want = expected[relative];
                if (size != want.Size || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), want.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new OverlayException(UpdateFailure.BadArchive, relative + " does not match the manifest");
            }

            if (!OperatingSystem.IsWindows())
            {
                bool executable = (entry.Mode & UnixFileMode.UserExecute) != 0;
                File.SetUnixFileMode(target, executable
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            Report(progress, "verify", written.Count, manifest.Files.Count);
        }

        foreach (OverlayFile file in manifest.Files)
            if (!written.Contains(file.Path))
                throw new OverlayException(UpdateFailure.BadArchive, "the archive is missing " + file.Path);
    }

    /// <summary>
    /// The folder of a copy of <paramref name="version"/> staged earlier, or null. The copy is trusted only if the signed
    /// manifest kept beside it still verifies and every file in it still hashes to what that manifest says.
    /// </summary>
    public static string? FindStaged(string gameDir, string version, string rid, string publicKey)
    {
        try
        {
            string directory = Path.Combine(gameDir, PendingFolder.Replace('/', Path.DirectorySeparatorChar), version + "-" + rid);
            string manifestPath = Path.Combine(directory, StagedManifest);
            if (!File.Exists(manifestPath) || publicKey.Length == 0) return null;
            if (!OverlayManifest.TryParse(File.ReadAllBytes(manifestPath), out OverlayManifest? manifest, out string _) || manifest is null) return null;
            if (manifest.OptimumVersion != version || manifest.Rid != rid) return null;
            return IsStaged(directory, manifest, publicKey) ? directory : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="directory"/> holds a staged copy of this exact release whose files still hash correctly.</summary>
    private static bool IsStaged(string directory, OverlayManifest manifest, string publicKey)
    {
        try
        {
            string manifestPath = Path.Combine(directory, StagedManifest);
            string signaturePath = Path.Combine(directory, StagedManifestSignature);
            if (!File.Exists(manifestPath) || !File.Exists(signaturePath)) return false;
            if (!ManifestSignature.Verify(File.ReadAllBytes(manifestPath), File.ReadAllBytes(signaturePath), publicKey)) return false;
            string markerPath = Path.Combine(directory, StagedMarker);
            if (!File.Exists(markerPath)) return false;
            StagedMarkerData? marker = JsonSerializer.Deserialize<StagedMarkerData>(File.ReadAllText(markerPath));
            if (marker is null || marker.Version != manifest.OptimumVersion || marker.Rid != manifest.Rid || marker.ArchiveSha256 != manifest.ArchiveSha256) return false;
            foreach (OverlayFile file in manifest.Files)
            {
                string path = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != file.Size) return false;
                using FileStream stream = File.OpenRead(path);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Removes every staged copy of <paramref name="version"/>, so one that just failed to apply is not offered again.</summary>
    public static void RemoveStaged(string gameDir, string version)
    {
        try
        {
            string pending = Path.Combine(gameDir, PendingFolder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(pending) || !OverlayManifest.IsValidVersion(version)) return;
            foreach (string directory in Directory.EnumerateDirectories(pending, version + "-*"))
                TryDelete(directory);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Removes staged copies of versions that are no longer newer than the installed one. The helper cannot delete the
    /// folder it ran from on Windows, so the first start after an update does it.
    /// </summary>
    public static void SweepStale(string gameDir, string installedVersion)
    {
        try
        {
            string pending = Path.Combine(gameDir, PendingFolder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(pending)) return;
            foreach (string directory in Directory.EnumerateDirectories(pending))
            {
                string name = Path.GetFileName(directory);
                int dash = name.IndexOf('-');
                if (dash <= 0) continue;
                string version = name.Substring(0, dash);
                if (OverlayManifest.IsValidVersion(version) && OverlayManifest.CompareVersions(version, installedVersion) <= 0)
                    TryDelete(directory);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Removes temporary folders a killed run left behind, once they are old enough not to belong to a live one.</summary>
    private static void SweepTemporaryFolders(string pending)
    {
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(pending, ".tmp-*"))
                if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddHours(-1)) TryDelete(directory);
        }
        catch (Exception)
        {
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private sealed class StagedMarkerData
    {
        public StagedMarkerData(string version, string rid, string archiveSha256, DateTimeOffset stagedAtUtc)
        {
            Version = version;
            Rid = rid;
            ArchiveSha256 = archiveSha256;
            StagedAtUtc = stagedAtUtc;
        }

        public string Version { get; }
        public string Rid { get; }
        public string ArchiveSha256 { get; }
        public DateTimeOffset StagedAtUtc { get; }
    }

    private sealed class OverlayException : Exception
    {
        public int Failure { get; }
        public OverlayException(int failure, string message) : base(message) => Failure = failure;
    }
}
