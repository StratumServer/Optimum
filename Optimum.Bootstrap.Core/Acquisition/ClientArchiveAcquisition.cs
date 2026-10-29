using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Acquisition;

public sealed record ClientArchivePlan(
    string Version,
    string Url,
    string ArchivePath,
    long ExpectedBytes,
    string? EntityTag,
    long ExistingPartialBytes,
    bool CachedArchiveIsValid,
    bool PartialCanResume,
    long? SourceFreeBytes,
    long? TemporaryFreeBytes,
    long SourceRequiredBytes,
    long TemporaryRequiredBytes,
    string Summary,
    string? SpaceWarning);

public readonly record struct ClientArchiveProgress(long DownloadedBytes, long ExpectedBytes);

public interface IClientArchiveAcquisition
{
    Task<ClientArchivePlan> InspectAsync(string repoRoot, string? version, CancellationToken cancellationToken);

    Task<string> EnsureAsync(
        ClientArchivePlan plan,
        IProgress<ClientArchiveProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class ClientArchiveAcquisition : IClientArchiveAcquisition
{
    private const long EstimatedSourceWorkspaceBytes = 3L * 1024 * 1024 * 1024;
    private const long EstimatedTemporaryWorkspaceBytes = 1L * 1024 * 1024 * 1024;
    private static readonly Uri ClientCdn = new("https://cdn.vintagestory.at/gamefiles/stable/");
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    private readonly ISystemProbe _probe;
    private readonly HttpClient _http;
    private readonly IStorageSpaceProbe _storage;

    public ClientArchiveAcquisition(
        ISystemProbe probe,
        HttpClient? http = null,
        IStorageSpaceProbe? storage = null)
    {
        _probe = probe;
        _http = http ?? SharedHttpClient;
        _storage = storage ?? new StorageSpaceProbe();
    }

    public async Task<ClientArchivePlan> InspectAsync(
        string repoRoot,
        string? version,
        CancellationToken cancellationToken)
    {
        string resolvedVersion = ResolveVersion(repoRoot, version);
        ValidateVersion(resolvedVersion);
        string fileName = ArchiveFileName(resolvedVersion, _probe.Os, _probe.Arch);
        string url = new Uri(ClientCdn, fileName).AbsoluteUri;
        string archivePath = Path.Combine(repoRoot, ".vanilla", "archives", fileName);
        string partialPath = archivePath + ".partial";
        string etagPath = partialPath + ".etag";
        string metadataPath = archivePath + ".meta";

        if (File.Exists(archivePath)
            && await IsLocallyValidatedCacheAsync(archivePath, metadataPath, resolvedVersion, cancellationToken)
                .ConfigureAwait(false))
        {
            long cachedBytes = new FileInfo(archivePath).Length;
            long sourceRequired = EstimatedSourceWorkspaceBytes;
            StorageSpace? localSource = _storage.GetAvailable(archivePath);
            StorageSpace? localTemporary = _storage.GetAvailable(Path.GetTempPath());
            string localSummary = BuildSummary(
                cachedBytes, 0, false, true,
                localSource?.AvailableBytes, localTemporary?.AvailableBytes,
                sourceRequired, EstimatedTemporaryWorkspaceBytes);
            string? localWarning = BuildSpaceWarning(
                sourceRequired, localSource, localTemporary, EstimatedTemporaryWorkspaceBytes);
            return new ClientArchivePlan(
                resolvedVersion, url, archivePath, cachedBytes, null, 0, true, false,
                localSource?.AvailableBytes, localTemporary?.AvailableBytes,
                sourceRequired, EstimatedTemporaryWorkspaceBytes, localSummary, localWarning);
        }

        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long expectedBytes = response.Content.Headers.ContentLength
            ?? throw new HttpRequestException("The client download endpoint did not report its file size.");
        string? entityTag = response.Headers.ETag?.ToString();
        bool supportsRanges = response.Headers.AcceptRanges.Any(value =>
            string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase));

        long partialBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        string? partialTag = File.Exists(etagPath) ? File.ReadAllText(etagPath).Trim() : null;
        bool partialCanResume = partialBytes > 0
            && partialBytes < expectedBytes
            && supportsRanges
            && IsStrongEntityTag(entityTag)
            && string.Equals(partialTag, entityTag, StringComparison.Ordinal);

        bool cachedArchiveIsValid = File.Exists(archivePath)
            && new FileInfo(archivePath).Length == expectedBytes
            && await CacheMetadataMatchesAsync(
                metadataPath, archivePath, expectedBytes, entityTag, cancellationToken).ConfigureAwait(false)
            && await ValidateArchiveAsync(
                archivePath, resolvedVersion, cancellationToken: cancellationToken).ConfigureAwait(false);

        long remainingDownloadBytes = cachedArchiveIsValid
            ? 0
            : partialCanResume ? expectedBytes - partialBytes : expectedBytes;
        long sourceRequiredBytes = EstimatedSourceWorkspaceBytes + remainingDownloadBytes;
        long temporaryRequiredBytes = EstimatedTemporaryWorkspaceBytes;
        StorageSpace? sourceSpace = _storage.GetAvailable(archivePath);
        StorageSpace? temporarySpace = _storage.GetAvailable(Path.GetTempPath());
        long? sourceFreeBytes = sourceSpace?.AvailableBytes;
        long? temporaryFreeBytes = temporarySpace?.AvailableBytes;
        string summary = BuildSummary(
            expectedBytes, partialBytes, partialCanResume, cachedArchiveIsValid,
            sourceFreeBytes, temporaryFreeBytes, sourceRequiredBytes, temporaryRequiredBytes);
        string? warning = BuildSpaceWarning(
            sourceRequiredBytes, sourceSpace, temporarySpace, temporaryRequiredBytes);

        return new ClientArchivePlan(
            resolvedVersion, url, archivePath, expectedBytes, entityTag, partialBytes,
            cachedArchiveIsValid, partialCanResume, sourceFreeBytes, temporaryFreeBytes,
            sourceRequiredBytes, temporaryRequiredBytes, summary, warning);
    }

    public async Task<string> EnsureAsync(
        ClientArchivePlan plan,
        IProgress<ClientArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string partialPath = plan.ArchivePath + ".partial";
        string partialTagPath = partialPath + ".etag";
        string archiveTagPath = plan.ArchivePath + ".meta";
        Directory.CreateDirectory(Path.GetDirectoryName(plan.ArchivePath)!);

        if (plan.CachedArchiveIsValid)
            return plan.ArchivePath;

        if (File.Exists(plan.ArchivePath))
        {
            File.Delete(plan.ArchivePath);
            TryDelete(archiveTagPath);
        }

        long offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        bool resume = plan.PartialCanResume && offset == plan.ExistingPartialBytes;
        if (!resume && offset > 0)
        {
            TryDelete(partialPath);
            TryDelete(partialTagPath);
            offset = 0;
        }

        using HttpResponseMessage response = await SendDownloadAsync(plan, offset, resume, cancellationToken)
            .ConfigureAwait(false);

        if (resume && response.StatusCode == HttpStatusCode.OK)
        {
            // If-Range makes the server return a full body when the archive changed.
            // SaveResponseAsync rejects a changed ETag; the next attempt re-inspects metadata.
            offset = 0;
            resume = false;
        }
        else if (resume && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            TryDelete(partialPath);
            TryDelete(partialTagPath);
            using HttpResponseMessage fresh = await SendDownloadAsync(plan, 0, false, cancellationToken)
                .ConfigureAwait(false);
            await SaveResponseAsync(fresh, plan, partialPath, partialTagPath, 0, progress, cancellationToken)
                .ConfigureAwait(false);
            return await PromoteVerifiedArchiveAsync(
                plan, partialPath, partialTagPath, archiveTagPath, cancellationToken).ConfigureAwait(false);
        }
        else if (resume && response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.EnsureSuccessStatusCode();
            throw new HttpRequestException("The server returned an invalid response while resuming the client download.");
        }

        await SaveResponseAsync(response, plan, partialPath, partialTagPath, offset, progress, cancellationToken)
            .ConfigureAwait(false);
        return await PromoteVerifiedArchiveAsync(
            plan, partialPath, partialTagPath, archiveTagPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveResponseAsync(
        HttpResponseMessage response,
        ClientArchivePlan plan,
        string partialPath,
        string partialTagPath,
        long offset,
        IProgress<ClientArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        bool appending = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        string? responseTag = response.Headers.ETag?.ToString();
        if (plan.EntityTag is not null
            && responseTag is not null
            && !string.Equals(plan.EntityTag, responseTag, StringComparison.Ordinal))
        {
            TryDelete(partialPath);
            TryDelete(partialTagPath);
            throw new HttpRequestException("The client archive changed while it was being downloaded. Retry the install.");
        }

        string? resumeTag = responseTag ?? plan.EntityTag;
        if (IsStrongEntityTag(resumeTag))
            WriteAtomic(partialTagPath, resumeTag!);
        else
            TryDelete(partialTagPath);

        long downloaded = appending ? offset : 0;
        FileMode mode = appending ? FileMode.Append : FileMode.Create;
        await using var target = new FileStream(
            partialPath, mode, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[64 * 1024];
        long lastReportAt = Environment.TickCount64;
        while (true)
        {
            int count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;

            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            downloaded += count;
            long now = Environment.TickCount64;
            if (now - lastReportAt >= 1000)
            {
                progress?.Report(new(downloaded, plan.ExpectedBytes));
                lastReportAt = now;
            }
        }

        await target.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        progress?.Report(new(downloaded, plan.ExpectedBytes));
    }

    private static async Task<string> PromoteVerifiedArchiveAsync(
        ClientArchivePlan plan,
        string partialPath,
        string partialTagPath,
        string archiveTagPath,
        CancellationToken cancellationToken)
    {
        long actualBytes = new FileInfo(partialPath).Length;
        if (actualBytes != plan.ExpectedBytes)
        {
            if (actualBytes > plan.ExpectedBytes)
            {
                TryDelete(partialPath);
                TryDelete(partialTagPath);
            }
            throw new IOException(
                $"The client download is incomplete ({FormatBytes(actualBytes)} of {FormatBytes(plan.ExpectedBytes)}). The partial file is kept for retry.");
        }

        if (!await ValidateArchiveAsync(partialPath, plan.Version, plan.ArchivePath, cancellationToken)
                .ConfigureAwait(false))
        {
            TryDelete(partialPath);
            TryDelete(partialTagPath);
            throw new InvalidDataException("The downloaded client archive failed its integrity check and was discarded. Retry the install.");
        }

        File.Move(partialPath, plan.ArchivePath, overwrite: true);
        string? partialTag = File.Exists(partialTagPath) ? File.ReadAllText(partialTagPath).Trim() : null;
        await using var archive = File.OpenRead(plan.ArchivePath);
        byte[] digest = await SHA256.HashDataAsync(archive, cancellationToken).ConfigureAwait(false);
        WriteAtomic(archiveTagPath, FormatArchiveMetadata(
            plan.ExpectedBytes, partialTag ?? plan.EntityTag ?? string.Empty, Convert.ToHexString(digest)));
        TryDelete(partialTagPath);
        return plan.ArchivePath;
    }

    private async Task<HttpResponseMessage> SendDownloadAsync(
        ClientArchivePlan plan,
        long offset,
        bool resume,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, plan.Url);
        if (resume)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(plan.EntityTag!));
        }

        HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        if (resume && response.StatusCode == HttpStatusCode.PartialContent
            && (range?.From != offset || range.Length != plan.ExpectedBytes))
        {
            response.Dispose();
            throw new HttpRequestException("The server returned an invalid byte range for the client download.");
        }
        return response;
    }

    private static string ResolveVersion(string repoRoot, string? version)
    {
        if (!string.IsNullOrWhiteSpace(version))
            return version;

        string forksPath = Path.Combine(repoRoot, "forks.json");
        using JsonDocument forks = JsonDocument.Parse(File.ReadAllText(forksPath));
        return forks.RootElement.GetProperty("vintageStoryVersion").GetString()
            ?? throw new InvalidDataException("forks.json does not declare a Vintage Story version.");
    }

    private static void ValidateVersion(string version)
    {
        if (version.Length is 0 or > 64
            || !version.Any(char.IsAsciiDigit)
            || version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '+'))
        {
            throw new InvalidDataException($"Unsupported Vintage Story version value: {version}");
        }
    }

    private static string ArchiveFileName(string version, OsKind os, Architecture architecture) => os switch
    {
        OsKind.Windows => $"vs_install_win-x64_{version}.exe",
        OsKind.MacOs => $"vs_client_osx-{(architecture == Architecture.Arm64 ? "arm64" : "x64")}_{version}.tar.gz",
        _ => $"vs_client_linux-{(architecture == Architecture.Arm64 ? "arm64" : "x64")}_{version}.tar.gz",
    };

    private static async Task<bool> ValidateArchiveAsync(
        string path,
        string version,
        string? formatPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string extensionPath = formatPath ?? path;
            if (extensionPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                return await ValidateTarGzipAsync(path, version, cancellationToken).ConfigureAwait(false);

            if (!extensionPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return false;

            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
                return false;
            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset < 0x40 || peOffset > stream.Length - 4)
                return false;
            stream.Position = peOffset;
            return reader.ReadUInt32() == 0x00004550;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> ValidateTarGzipAsync(
        string path,
        string version,
        CancellationToken cancellationToken)
    {
        bool hasClientLibrary = false;
        bool hasVersionMarker = false;
        await using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
        using (var reader = new TarReader(gzip, leaveOpen: true))
        {
            TarEntry? entry;
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } currentEntry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = currentEntry.Name.Replace('\\', '/');
                hasClientLibrary |= name.EndsWith("/VintagestoryLib.dll", StringComparison.OrdinalIgnoreCase);
                hasVersionMarker |= string.Equals(
                    Path.GetFileName(name), $"version-{version}.txt", StringComparison.OrdinalIgnoreCase);
                if (currentEntry.DataStream is not null)
                    await currentEntry.DataStream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            }
        }

        // Drain gzip after the tar end marker so GZipStream verifies its trailer.
        await gzip.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        return hasClientLibrary && hasVersionMarker;
    }

    private static async Task<bool> CacheMetadataMatchesAsync(
        string metadataPath,
        string archivePath,
        long expectedBytes,
        string? entityTag,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(metadataPath))
            return false;
        string[] metadata = File.ReadAllText(metadataPath).Trim().Split('\n');
        if (metadata.Length != 3
            || !long.TryParse(metadata[0], out long cachedBytes)
            || cachedBytes != expectedBytes
            || (entityTag is not null && !string.Equals(metadata[1].Trim(), entityTag, StringComparison.Ordinal)))
        {
            return false;
        }

        await using var archive = File.OpenRead(archivePath);
        byte[] digest = await SHA256.HashDataAsync(archive, cancellationToken).ConfigureAwait(false);
        return string.Equals(metadata[2].Trim(), Convert.ToHexString(digest), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsLocallyValidatedCacheAsync(
        string archivePath,
        string metadataPath,
        string version,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(metadataPath))
            return false;
        string[] metadata = File.ReadAllText(metadataPath).Trim().Split('\n');
        if (metadata.Length != 3
            || !long.TryParse(metadata[0], out long expectedBytes)
            || new FileInfo(archivePath).Length != expectedBytes)
        {
            return false;
        }

        return await CacheMetadataMatchesAsync(
                metadataPath, archivePath, expectedBytes, entityTag: null, cancellationToken)
                .ConfigureAwait(false)
            && await ValidateArchiveAsync(archivePath, version, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
    }

    private static string FormatArchiveMetadata(long length, string entityTag, string sha256) =>
        $"{length}\n{entityTag}\n{sha256}";

    private static bool IsStrongEntityTag(string? entityTag) =>
        entityTag is not null && !entityTag.StartsWith("W/", StringComparison.OrdinalIgnoreCase);

    private static string BuildSummary(
        long expectedBytes,
        long partialBytes,
        bool canResume,
        bool cached,
        long? sourceFreeBytes,
        long? temporaryFreeBytes,
        long sourceRequiredBytes,
        long temporaryRequiredBytes)
    {
        string download = cached
            ? $"client archive cached ({FormatBytes(expectedBytes)})"
            : canResume
                ? $"client download {FormatBytes(partialBytes)} of {FormatBytes(expectedBytes)} saved; retry resumes"
                : $"client download {FormatBytes(expectedBytes)}";
        string sourceFree = sourceFreeBytes is long source ? FormatBytes(source) : "unknown";
        string temporaryFree = temporaryFreeBytes is long temporary ? FormatBytes(temporary) : "unknown";
        return $"{download}. Estimated space needed: {FormatBytes(sourceRequiredBytes)} at the source and {FormatBytes(temporaryRequiredBytes)} in temp. Free now: {sourceFree} at the source and {temporaryFree} in temp.";
    }

    private static string? BuildSpaceWarning(
        long sourceRequiredBytes,
        StorageSpace? source,
        StorageSpace? temporary,
        long temporaryRequiredBytes)
    {
        if (source is not null && temporary is not null
            && string.Equals(source.VolumeRoot, temporary.VolumeRoot, PathComparison))
        {
            long combinedRequired = sourceRequiredBytes + temporaryRequiredBytes;
            return source.AvailableBytes < combinedRequired
                ? $"Estimated free space needed on this volume is {FormatBytes(combinedRequired)}; {FormatBytes(source.AvailableBytes)} is available. You can cancel, free space, and retry."
                : null;
        }

        if (source is not null && source.AvailableBytes < sourceRequiredBytes)
        {
            long sourceAvailable = source.AvailableBytes;
            return $"The source volume has {FormatBytes(sourceAvailable)} free; the download and estimated workspace need {FormatBytes(sourceRequiredBytes)}. You can cancel, free space, and retry.";
        }
        if (temporary is not null && temporary.AvailableBytes < temporaryRequiredBytes)
        {
            long tempAvailable = temporary.AvailableBytes;
            return $"The temporary volume has {FormatBytes(tempAvailable)} free; the estimated build output needs {FormatBytes(temporaryRequiredBytes)}. You can cancel, free space, and retry.";
        }
        return null;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024d * 1024 * 1024):0.#} GiB"
            : $"{bytes / (1024d * 1024):0.#} MiB";

    private static void WriteAtomic(string path, string value)
    {
        string temporary = path + ".new";
        File.WriteAllText(temporary, value, Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

}

public sealed record StorageSpace(string VolumeRoot, long AvailableBytes);

public interface IStorageSpaceProbe
{
    StorageSpace? GetAvailable(string path);
}

public sealed class StorageSpaceProbe : IStorageSpaceProbe
{
    public StorageSpace? GetAvailable(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            DriveInfo? drive = DriveInfo.GetDrives()
                .Where(candidate => candidate.IsReady && IsWithin(fullPath, candidate.Name))
                .OrderByDescending(candidate => candidate.Name.Length)
                .FirstOrDefault();
            return drive is null ? null : new StorageSpace(drive.Name, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsWithin(string path, string root)
    {
        string normalizedRoot = Path.GetFullPath(root);
        if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
            normalizedRoot += Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal)
            || string.Equals(path, Path.TrimEndingDirectorySeparator(normalizedRoot), OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}
