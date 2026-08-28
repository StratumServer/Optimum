using System.Text;
using CliWrap;
using CliWrap.EventStream;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Build;

/// <summary>
/// What the front end asks for when there is no Optimum checkout on the machine:
/// the version to fetch (the installer's own version), an optional cache
/// location, and whether to re-clone even if a cached tree is already there.
/// </summary>
public sealed record SourceRequest(string Version, string? CacheRoot = null, bool Refresh = false)
{
    /// <summary>The public repository the installer clones. HTTPS, so an end user
    /// needs no SSH key.</summary>
    public const string RepositoryUrl = "https://github.com/StratumServer/Optimum.git";
}

public sealed record SourceAcquisitionResult(bool Ok, string? RepoRoot, FailureReason? Reason, string? Message)
{
    public static SourceAcquisitionResult Success(string repoRoot) => new(true, repoRoot, null, null);

    public static SourceAcquisitionResult Failure(FailureReason reason, string message) =>
        new(false, null, reason, message);
}

/// <summary>
/// Obtains an Optimum checkout the build pipeline can drive. The GUI runs one on
/// the Prerequisites screen; the CLI runs one for <c>build --acquire-source</c>.
/// </summary>
public interface ISourceProvider
{
    Task<SourceAcquisitionResult> EnsureAsync(
        SourceRequest request, IBuildObserver observer, CancellationToken cancellationToken);
}

/// <summary>
/// Pure helpers that decide where a downloaded checkout lives and what ref to
/// fetch. Split out from <see cref="GitSourceProvider"/> so they are testable
/// without a git process.
/// </summary>
public static class SourceCache
{
    /// <summary>
    /// The directory a checkout for <paramref name="version"/> is cached in:
    /// <c>&lt;cache&gt;/optimum/src-&lt;version&gt;</c>, where the cache root is
    /// the platform's per-user cache location unless <paramref name="overrideRoot"/>
    /// is given.
    /// </summary>
    public static string Directory(ISystemProbe probe, string version, string? overrideRoot = null)
    {
        string root = overrideRoot ?? DefaultRoot(probe);
        return Path.Combine(root, "optimum", "src-" + SanitizeVersion(version));
    }

    private static string DefaultRoot(ISystemProbe probe) => probe.Os switch
    {
        OsKind.Windows => probe.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Path.Combine(probe.HomeDirectory, "AppData", "Local"),
        OsKind.MacOs => Path.Combine(probe.HomeDirectory, "Library", "Caches"),
        _ => probe.GetEnvironmentVariable("XDG_CACHE_HOME")
            ?? Path.Combine(probe.HomeDirectory, ".cache"),
    };

    /// <summary>
    /// A filesystem-safe token for the version, prefixed <c>v</c> when it starts
    /// with a digit so it matches the release tag naming (<c>v0.3.14</c>).
    /// </summary>
    internal static string SanitizeVersion(string version)
    {
        string core = (version ?? string.Empty).Trim().Split('+', 2)[0];
        var safe = new string(core.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray())
            .Trim('.', '-', '_');
        if (safe.Length == 0)
            return "dev";
        return char.IsDigit(safe[0]) ? "v" + safe : safe;
    }

    /// <summary>
    /// The git ref to clone: the <c>v&lt;version&gt;</c> release tag for a real
    /// version, or null for a dev build (clone the default branch instead).
    /// </summary>
    public static string? TagRef(string version)
    {
        string v = SanitizeVersion(version);
        return v.Length > 1 && v[0] == 'v' && char.IsDigit(v[1]) ? v : null;
    }

    /// <summary>True when a directory holds the two files the pipeline needs.</summary>
    public static bool IsUsableCheckout(ISystemProbe probe, string directory) =>
        probe.FileExists(Path.Combine(directory, "forks.json"))
        && probe.FileExists(Path.Combine(directory, "scripts", "bootstrap.sh"));

    internal static IReadOnlyList<string> CloneArguments(string? tagRef, string targetDirectory)
    {
        List<string> args = ["clone", "--depth", "1", "--single-branch"];
        if (tagRef is not null)
            args.AddRange(["--branch", tagRef]);
        args.Add(SourceRequest.RepositoryUrl);
        args.Add(targetDirectory);
        return args;
    }
}

/// <summary>
/// Clones <see cref="SourceRequest.RepositoryUrl"/> at the release tag with a
/// shallow, single-branch clone. A cached tree is reused as is; a clone that
/// fails on the tag retries once on the default branch (for a version whose tag
/// is not published yet). The clone lands in a staging sibling and is swapped
/// into place only once it verifies, so an interrupted download never leaves a
/// half-tree that looks usable.
/// </summary>
public sealed class GitSourceProvider(ISystemProbe probe) : ISourceProvider
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public async Task<SourceAcquisitionResult> EnsureAsync(
        SourceRequest request, IBuildObserver observer, CancellationToken cancellationToken)
    {
        string targetDir = SourceCache.Directory(probe, request.Version, request.CacheRoot);

        if (!request.Refresh && SourceCache.IsUsableCheckout(probe, targetDir))
        {
            observer.Log(LogLevel.Info, $"Using the cached Optimum source at {targetDir}");
            return SourceAcquisitionResult.Success(targetDir);
        }

        string? git = CommandSearch.Which(probe, "git");
        if (git is null)
        {
            return SourceAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                "git was not found on PATH; install git so the installer can download the Optimum source");
        }

        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SourceAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                $"could not create the source cache directory: {ex.Message}");
        }

        string staging = targetDir + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        string? tagRef = SourceCache.TagRef(request.Version);

        try
        {
            observer.Phase(ProgressPhase.Decompile, 1, $"downloading the Optimum source ({tagRef ?? "default branch"})");

            int exit = await Clone(git, tagRef, staging, observer, cancellationToken);
            if (exit != 0 && tagRef is not null)
            {
                observer.Log(LogLevel.Warn, $"no {tagRef} tag upstream yet; downloading the default branch");
                TryDelete(staging);
                exit = await Clone(git, null, staging, observer, cancellationToken);
            }

            if (exit != 0)
            {
                TryDelete(staging);
                return SourceAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                    $"git clone failed (exit {exit}); check the network connection and retry");
            }

            if (!SourceCache.IsUsableCheckout(probe, staging))
            {
                TryDelete(staging);
                return SourceAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                    "the downloaded source is missing forks.json or scripts/bootstrap.sh");
            }

            Promote(staging, targetDir);

            observer.Phase(ProgressPhase.Decompile, 2, $"Optimum source ready at {targetDir}");
            return SourceAcquisitionResult.Success(targetDir);
        }
        catch (OperationCanceledException)
        {
            TryDelete(staging);
            return SourceAcquisitionResult.Failure(FailureReason.Cancelled, "the source download was cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            TryDelete(staging);
            return SourceAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                $"could not place the downloaded source: {ex.Message}");
        }
    }

    private static async Task<int> Clone(
        string git, string? tagRef, string destination, IBuildObserver observer, CancellationToken cancellationToken)
    {
        int exitCode = -1;
        Command cmd = Cli.Wrap(git)
            .WithArguments(SourceCache.CloneArguments(tagRef, destination))
            .WithValidation(CommandResultValidation.None);

        await foreach (CommandEvent commandEvent in
            cmd.ListenAsync(Utf8, Utf8, cancellationToken, CancellationToken.None))
        {
            switch (commandEvent)
            {
                case StandardOutputCommandEvent stdout:
                    observer.RawOutput(false, stdout.Text);
                    break;
                case StandardErrorCommandEvent stderr:
                    // git writes its clone progress to stderr; it is not an error.
                    observer.RawOutput(false, stderr.Text);
                    break;
                case ExitedCommandEvent exited:
                    exitCode = exited.ExitCode;
                    break;
            }
        }

        return exitCode;
    }

    /// <summary>
    /// Swaps a verified clone into place without first deleting a usable cache.
    /// The old checkout is restored if the promotion fails.
    /// </summary>
    internal static void Promote(string staging, string target)
    {
        string? backup = null;
        try
        {
            if (System.IO.Directory.Exists(target))
            {
                backup = target + ".previous-" + Guid.NewGuid().ToString("N")[..8];
                System.IO.Directory.Move(target, backup);
            }

            System.IO.Directory.Move(staging, target);
        }
        catch (Exception promotionError) when (promotionError is IOException or UnauthorizedAccessException)
        {
            if (backup is not null
                && !System.IO.Directory.Exists(target)
                && System.IO.Directory.Exists(backup))
            {
                try
                {
                    System.IO.Directory.Move(backup, target);
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"source promotion failed and the previous checkout could not be restored; it remains at {backup}",
                        new AggregateException(promotionError, restoreError));
                }
            }

            throw;
        }

        if (backup is not null)
            TryDelete(backup);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
