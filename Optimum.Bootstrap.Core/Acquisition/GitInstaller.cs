using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Acquisition;

public interface IGitAcquisition
{
    Task<ToolAcquisitionResult> InstallAsync(
        IBuildObserver observer, CancellationToken cancellationToken);
}

/// <summary>
/// Acquires Git for Windows without requiring administrator rights by trying
/// the official MinGit portable variants in the user's Optimum cache. Each
/// archive is SHA-256 checked against GitHub release metadata before use.
/// </summary>
public sealed class GitInstaller : IGitAcquisition
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/git-for-windows/git/releases/latest";
    private const string ReleasePageUrl = "https://github.com/git-for-windows/git/releases/latest";
    private const string GitForWindowsUrl = "https://git-scm.com/install/windows";
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex ReleaseDigestRow = new(
        """<tr>\s*<td>(?<name>MinGit-[^<]+\.zip)</td>\s*<td>(?<digest>[0-9a-fA-F]{64})</td>\s*</tr>""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private readonly ISystemProbe _probe;
    private readonly HttpClient _http;
    private readonly Action<string> _prependProcessPath;

    public GitInstaller(ISystemProbe probe) : this(
        probe,
        CreateHttpClient(),
        ProcessPath.Prepend)
    {
    }

    internal GitInstaller(
        ISystemProbe probe,
        HttpClient http,
        Action<string> prependProcessPath)
    {
        _probe = probe;
        _http = http;
        _prependProcessPath = prependProcessPath;
    }

    public async Task<ToolAcquisitionResult> InstallAsync(
        IBuildObserver observer, CancellationToken cancellationToken)
    {
        if (_probe.Os != OsKind.Windows)
        {
            return ToolAcquisitionResult.Failure(FailureReason.UnsupportedVersion,
                "automatic Git acquisition is only available on Windows");
        }

        try
        {
            string? existing = GitForWindows.FindExecutable(_probe);
            if (existing is not null && VerifyGit(existing, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Activate(existing);
                return ToolAcquisitionResult.Success(existing);
            }

            string portable = await InstallPortableAsync(observer, cancellationToken);
            // A verified portable directory has been promoted at this point;
            // finish activation so the caller rescans the completed install.
            Activate(portable);
            observer.Log(LogLevel.Info, $"Installed portable Git at {portable}");
            return ToolAcquisitionResult.Success(portable);
        }
        catch (OperationCanceledException)
        {
            return ToolAcquisitionResult.Failure(FailureReason.Cancelled, "the Git installation was cancelled");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or CryptographicException)
        {
            return ToolAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                $"Git could not be acquired. Install it from {GitForWindowsUrl} and check again. {ex.Message}");
        }
    }

    public static string CacheDirectory(ISystemProbe probe)
    {
        string localAppData = probe.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Path.Combine(probe.HomeDirectory, "AppData", "Local");
        return Path.Combine(localAppData, "Optimum", "tools", "git");
    }

    public static string PortableExecutable(ISystemProbe probe) =>
        Path.Combine(CacheDirectory(probe), "cmd", "git.exe");

    private async Task<string> InstallPortableAsync(IBuildObserver observer, CancellationToken cancellationToken)
    {
        observer.Phase(ProgressPhase.Decompile, 1, "fetching official portable Git release details");
        GitRelease release = await GetReleaseAsync(observer, cancellationToken);
        string tag = release.Tag;
        if (!Regex.IsMatch(tag, "^v[0-9]+\\.[0-9]+\\.[0-9]+\\.windows\\.[0-9]+$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("the Git for Windows release tag has an unexpected format");

        string[] architectureVariants = _probe.Arch switch
        {
            System.Runtime.InteropServices.Architecture.X64 => ["64-bit", "busybox-64-bit"],
            System.Runtime.InteropServices.Architecture.Arm64 => ["arm64", "busybox-arm64"],
            System.Runtime.InteropServices.Architecture.X86 => ["32-bit", "busybox-32-bit"],
            _ => throw new InvalidDataException($"unsupported Windows architecture: {_probe.Arch}"),
        };

        string version = tag[1..].Replace(".windows.", ".", StringComparison.Ordinal);
        List<string> failures = [];
        foreach (string variant in architectureVariants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string assetName = $"MinGit-{version}-{variant}.zip";
            if (!release.Assets.TryGetValue(assetName, out PortableAsset? asset))
            {
                failures.Add($"{assetName} is not published with a SHA-256 digest");
                observer.Log(LogLevel.Warn, $"Portable Git variant {assetName} is unavailable or has no SHA-256 digest; trying the next variant.");
                continue;
            }

            try
            {
                string installed = await InstallPortableAssetAsync(asset, assetName, observer, cancellationToken);
                observer.Log(LogLevel.Info, $"Installed portable Git variant {assetName}");
                return installed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                or InvalidDataException or JsonException or CryptographicException)
            {
                failures.Add($"{assetName}: {ex.Message}");
                observer.Log(LogLevel.Warn, $"Portable Git variant {assetName} failed: {ex.Message}");
            }
        }

        throw new InvalidDataException(
            "All portable Git variants failed. Install Git manually and check again. " + string.Join("; ", failures));
    }

    private async Task<string> InstallPortableAssetAsync(
        PortableAsset asset,
        string assetName,
        IBuildObserver observer,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out Uri? assetUri)
            || assetUri.Scheme != Uri.UriSchemeHttps
            || !assetUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !assetUri.AbsolutePath.StartsWith(
                "/git-for-windows/git/releases/download/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("the Portable Git asset URL is not an official GitHub release URL");
        }

        string digest = asset.Digest;
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(digest[prefix.Length..], "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("the Portable Git release does not provide a SHA-256 digest");
        }

        string parent = Path.GetDirectoryName(CacheDirectory(_probe))!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, "git.partial-" + Guid.NewGuid().ToString("N")[..8]);
        string payload = Path.Combine(staging, "payload");
        string archive = Path.Combine(staging, assetName);
        Directory.CreateDirectory(payload);

        try
        {
            observer.Phase(ProgressPhase.Decompile, 2, $"downloading {assetName}");
            await DownloadAsync(assetUri, archive, cancellationToken);

            await using (FileStream file = File.OpenRead(archive))
            {
                byte[] actual = await SHA256.HashDataAsync(file, cancellationToken);
                string actualHex = Convert.ToHexString(actual);
                if (!actualHex.Equals(digest[prefix.Length..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("the downloaded Portable Git archive failed its SHA-256 check");
            }

            ZipFile.ExtractToDirectory(archive, payload, overwriteFiles: false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(payload, "cmd", "git.exe")))
                throw new InvalidDataException("the Portable Git archive did not contain cmd/git.exe");

            string target = CacheDirectory(_probe);
            PromoteAndVerify(payload, target, cancellationToken);
            // PromoteAndVerify commits the verified directory and removes any
            // previous cache. Do not turn a completed install into a cancelled result.
            return PortableExecutable(_probe);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private async Task<GitRelease> GetReleaseAsync(
        IBuildObserver observer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd("Optimum-Installer");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await _http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // GitHub's unauthenticated REST API has a shared per-IP limit. On busy
        // networks, resolve the same release tag and GitHub-published SHA-256
        // table from the official HTML release page instead of dropping to the
        // manual-install prompt before trying portable assets.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            observer.Log(LogLevel.Warn,
                "GitHub's release API is rate-limited; retrying release metadata from its official release page.");
            return await GetReleaseFromPageAsync(cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        GitRelease release = ParseApiRelease(document.RootElement);
        if (release.Assets.Count == 0)
        {
            observer.Log(LogLevel.Warn,
                "GitHub's release API did not include SHA-256 digests; retrying metadata from its official release page.");
            return await GetReleaseFromPageAsync(cancellationToken);
        }
        return release;
    }

    private async Task<GitRelease> GetReleaseFromPageAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasePageUrl);
        request.Headers.UserAgent.ParseAdd("Optimum-Installer");
        request.Headers.Accept.ParseAdd("text/html");
        using HttpResponseMessage response = await _http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        Uri finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("the official Git for Windows release page did not provide a final URL");
        const string tagPathPrefix = "/git-for-windows/git/releases/tag/";
        if (!finalUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !finalUri.AbsolutePath.StartsWith(tagPathPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("the official Git for Windows release page redirected to an unexpected URL");
        }

        string tag = Uri.UnescapeDataString(finalUri.AbsolutePath[tagPathPrefix.Length..]);
        if (!Regex.IsMatch(tag, "^v[0-9]+\\.[0-9]+\\.[0-9]+\\.windows\\.[0-9]+$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("the official release page has an unexpected Git for Windows tag");

        string html = await response.Content.ReadAsStringAsync(cancellationToken);
        var assets = new Dictionary<string, PortableAsset>(StringComparer.Ordinal);
        foreach (Match match in ReleaseDigestRow.Matches(html))
        {
            string name = System.Net.WebUtility.HtmlDecode(match.Groups["name"].Value);
            string digest = match.Groups["digest"].Value;
            string url = $"https://github.com/git-for-windows/git/releases/download/{tag}/{name}";
            assets[name] = new PortableAsset(url, "sha256:" + digest);
        }

        if (assets.Count == 0)
            throw new InvalidDataException("the official Git for Windows release page did not contain MinGit SHA-256 digests");

        return new GitRelease(tag, assets);
    }

    private static GitRelease ParseApiRelease(JsonElement root)
    {
        string tag = RequiredString(root, "tag_name");
        var assets = new Dictionary<string, PortableAsset>(StringComparer.Ordinal);
        if (!root.TryGetProperty("assets", out JsonElement releaseAssets)
            || releaseAssets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("the GitHub release metadata is missing its asset list");
        }

        foreach (JsonElement asset in releaseAssets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out JsonElement nameElement)
                || nameElement.ValueKind != JsonValueKind.String)
                continue;
            string name = nameElement.GetString()!;
            if (!name.StartsWith("MinGit-", StringComparison.Ordinal)
                || !name.EndsWith(".zip", StringComparison.Ordinal))
                continue;
            if (!asset.TryGetProperty("digest", out JsonElement digestElement)
                || digestElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(digestElement.GetString())
                || !asset.TryGetProperty("browser_download_url", out JsonElement urlElement)
                || urlElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(urlElement.GetString()))
                continue;

            assets[name] = new PortableAsset(urlElement.GetString()!, digestElement.GetString()!);
        }

        return new GitRelease(tag, assets);
    }

    private async Task DownloadAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
        await target.FlushAsync(cancellationToken);
    }

    private bool VerifyGit(string path, out string? version)
    {
        ProcessOutcome outcome = _probe.Run(path, ["--version"], VerifyTimeout);
        version = outcome.Started ? outcome.StandardOutput.Trim() : null;
        return outcome.Started && outcome.ExitCode == 0
            && version?.StartsWith("git version ", StringComparison.OrdinalIgnoreCase) == true;
    }

    private void Activate(string gitExecutable)
    {
        string? directory = Path.GetDirectoryName(gitExecutable);
        if (!string.IsNullOrWhiteSpace(directory))
            _prependProcessPath(directory);
    }

    private static string RequiredString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new InvalidDataException($"the Git release metadata is missing '{name}'");

    private sealed record PortableAsset(string DownloadUrl, string Digest);
    private sealed record GitRelease(string Tag, IReadOnlyDictionary<string, PortableAsset> Assets);

    private void PromoteAndVerify(string staging, string target, CancellationToken cancellationToken)
    {
        string? backup = null;
        bool promoted = false;
        try
        {
            if (Directory.Exists(target))
            {
                backup = target + ".previous-" + Guid.NewGuid().ToString("N")[..8];
                Directory.Move(target, backup);
            }
            Directory.Move(staging, target);
            promoted = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!VerifyGit(PortableExecutable(_probe), out _))
                throw new InvalidDataException("Portable Git was extracted but git --version did not succeed");
            cancellationToken.ThrowIfCancellationRequested();
            if (backup is not null)
                TryDeleteDirectory(backup);
        }
        catch
        {
            if (promoted)
                TryDeleteDirectory(target);
            if (backup is not null && !Directory.Exists(target) && Directory.Exists(backup))
                Directory.Move(backup, target);
            throw;
        }
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromMinutes(3) };

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}

internal static class GitForWindows
{
    public static string? FindExecutable(ISystemProbe probe)
    {
        string? onPath = CommandSearch.Which(probe, "git");
        if (onPath is not null)
            return onPath;

        string? localAppData = probe.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Path.Combine(probe.HomeDirectory, "AppData", "Local");
        string? programFiles = probe.GetEnvironmentVariable("ProgramFiles");
        string[] candidates =
        [
            GitInstaller.PortableExecutable(probe),
            Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe"),
            string.IsNullOrWhiteSpace(programFiles)
                ? Path.Combine("C:\\Program Files", "Git", "cmd", "git.exe")
                : Path.Combine(programFiles, "Git", "cmd", "git.exe"),
        ];
        return candidates.FirstOrDefault(probe.IsExecutable);
    }
}
