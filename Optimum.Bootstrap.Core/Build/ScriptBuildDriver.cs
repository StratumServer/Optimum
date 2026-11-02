using System.Text;
using System.Text.Json;
using CliWrap;
using CliWrap.EventStream;
using Optimum.Bootstrap.Core.Acquisition;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;

namespace Optimum.Bootstrap.Core.Build;

file static class Encodings
{
    public static readonly Encoding Utf8 = new UTF8Encoding(false);
}

/// <summary>
/// The real build pipeline: it drives <c>scripts/bootstrap.*</c>,
/// <c>dotnet build VintageStory.slnx</c>, <c>scripts/check-patches.sh</c>, and
/// the platform packaging script through CliWrap, the same sequence
/// <c>.github/workflows/ci-platform-bootstrap.yml</c> runs by hand. It never
/// reimplements those scripts.
/// </summary>
public sealed class ScriptBuildDriver(
    ISystemProbe probe,
    IClientArchiveAcquisition? clientArchives = null) : IBuildDriver
{
    private const long EstimatedSourceWorkspaceBytes = 3L * 1024 * 1024 * 1024;
    private const long EstimatedTemporaryWorkspaceBytes = 1024L * 1024 * 1024;
    private readonly IClientArchiveAcquisition _clientArchives =
        clientArchives ?? new ClientArchiveAcquisition(probe);

    public async Task<BuildResult> RunAsync(
        BuildRequest request,
        IBuildObserver observer,
        CancellationToken forceful,
        CancellationToken graceful = default)
    {
        var scanner = new PrerequisiteScanner(probe, request.RepoRoot);
        string[] missing = scanner.Scan().Where(r => r.BlocksBuild).Select(r => r.Definition.DisplayName).ToArray();
        if (missing.Length > 0)
            return BuildResult.Failure(FailureReason.BadInput, "Required tools missing: " + string.Join(", ", missing));

        bool outputPreexisted = probe.DirectoryExists(request.OutputDirectory);
        if (outputPreexisted
            && (probe.EnumerateFiles(request.OutputDirectory, "*").Any()
                || probe.EnumerateDirectories(request.OutputDirectory, "*").Any()))
        {
            return BuildResult.Failure(FailureReason.OutputExists,
                $"The output directory must be empty or absent: {request.OutputDirectory}");
        }

        try
        {
            BuildRequest preparedRequest = request;
            if (request.ClientArchive is null)
            {
                if (HasMatchingExtractionCaches(request))
                {
                    var storage = new StorageSpaceProbe();
                    StorageSpace? source = storage.GetAvailable(Path.Combine(request.RepoRoot, ".vanilla"));
                    StorageSpace? temporary = storage.GetAvailable(Path.GetTempPath());
                    string detail =
                        $"Using the existing Vintage Story {ResolveVersion(request) ?? "client"} extraction cache; no client download is needed. " +
                        $"Estimated build space: 3 GiB at the source and 1 GiB in temp. " +
                        $"Free now: {FormatAvailable(source)} at the source and {FormatAvailable(temporary)} in temp.";
                    observer.Phase(ProgressPhase.Decompile, 2, detail);
                    observer.Log(LogLevel.Info, detail);
                    if (HasLowSpace(source, temporary))
                    {
                        observer.Log(LogLevel.Warn,
                            "Available space is below the estimate. You can cancel, free space, and retry.");
                    }
                }
                else
                {
                    ClientArchivePlan plan = await _clientArchives.InspectAsync(
                        request.RepoRoot, request.Version, forceful);
                    observer.Phase(ProgressPhase.Decompile, 2, plan.Summary);
                    observer.Log(LogLevel.Info, plan.Summary);
                    if (plan.SpaceWarning is not null)
                        observer.Log(LogLevel.Warn, plan.SpaceWarning);

                    var progress = new InlineProgress<ClientArchiveProgress>(download =>
                    {
                        double fraction = plan.ExpectedBytes == 0
                            ? 1
                            : (double)download.DownloadedBytes / plan.ExpectedBytes;
                        int percent = 2 + (int)Math.Clamp(fraction * 6, 0, 6);
                        observer.Phase(ProgressPhase.Decompile, percent,
                            $"Downloading Vintage Story client: {FormatBytes(download.DownloadedBytes)} of {FormatBytes(download.ExpectedBytes)}");
                    });
                    using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(forceful, graceful);
                    string archive = await _clientArchives.EnsureAsync(plan, progress, downloadCancellation.Token);
                    preparedRequest = request with { ClientArchive = archive };
                }
            }

            Directory.CreateDirectory(request.OutputDirectory);

            StepOutcome bootstrap = await RunStep(
                BootstrapCommand(preparedRequest), request.RepoRoot, ProgressPhase.Decompile, 2, 48, observer,
                clearPlatformEnv: false, forceful, graceful);
            if (!bootstrap.Ok)
            {
                return BuildResult.Failure(
                    BootstrapFailureClassifier.Classify(bootstrap.Output),
                    $"bootstrap exited {bootstrap.ExitCode}");
            }

            observer.Phase(ProgressPhase.Patch, 50, "patches applied");

            StepOutcome build = await RunStep(
                (DotnetExecutable(), ["build", "VintageStory.slnx", "-c", "Release", "--nologo"]),
                request.RepoRoot, ProgressPhase.Assemble, 52, 82, observer,
                clearPlatformEnv: true, forceful, graceful);
            if (!build.Ok)
                return BuildResult.Failure(FailureReason.AssembleFailed, $"dotnet build exited {build.ExitCode}");

            // check-patches.sh is a POSIX shell script with no PowerShell port.
            // On Windows the only `bash` on a stock machine is System32\bash.exe
            // (WSL), which would run the script against a different filesystem
            // view with a different dotnet and git — silently wrong. Skip it
            // there; install-windows.ps1 never ran this check either.
            if (probe.Os == OsKind.Windows)
            {
                observer.Phase(ProgressPhase.Patch, 86,
                    "skipped the decompile round-trip check (needs a POSIX shell)");
            }
            else
            {
                StepOutcome checkPatches = await RunStep(
                    ("bash", ["scripts/check-patches.sh", "--strict-unavailable"]),
                    request.RepoRoot, ProgressPhase.Patch, 82, 86, observer,
                    clearPlatformEnv: false, forceful, graceful);
                if (!checkPatches.Ok)
                    return BuildResult.Failure(FailureReason.PatchConflict,
                        $"check-patches.sh exited {checkPatches.ExitCode}: a patch did not survive the decompile round trip");
            }

            StepOutcome package = await RunStep(
                PackageCommand(preparedRequest), request.RepoRoot, ProgressPhase.Assemble, 86, 95, observer,
                clearPlatformEnv: false, forceful, graceful);
            if (!package.Ok)
                return BuildResult.Failure(FailureReason.AssembleFailed, $"packaging exited {package.ExitCode}");

            string? produced = LocatePackage(request.OutputDirectory);
            if (produced is null)
                return BuildResult.Failure(FailureReason.AssembleFailed,
                    $"the packaging script produced no package under {request.OutputDirectory}");

            observer.Phase(ProgressPhase.Verify, 96, "validating the runtime");
            RuntimeValidationResult validation = new RuntimeValidator(probe).Validate(produced);
            if (!validation.Ok)
                return BuildResult.Failure(FailureReason.VerificationFailed, validation.Detail ?? "runtime validation failed");

            observer.Phase(ProgressPhase.Verify, 98, "package produced");
            return BuildResult.Success(produced);
        }
        catch (OperationCanceledException)
        {
            CleanOutput(request.OutputDirectory, outputPreexisted);
            return BuildResult.Failure(FailureReason.Cancelled, "the build was cancelled");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            CleanOutput(request.OutputDirectory, outputPreexisted);
            return BuildResult.Failure(FailureReason.DecompileFailed,
                $"client download preflight or transfer failed: {ex.Message}");
        }
    }

    internal (string Exe, IReadOnlyList<string> Args) BootstrapCommand(BuildRequest request)
    {
        if (probe.Os == OsKind.Windows)
        {
            List<string> win = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(request.RepoRoot, "bootstrap.ps1")];
            if (request.ClientArchive is not null)
                win.AddRange(["-ClientArchive", request.ClientArchive]);
            if (request.Version is not null)
                win.AddRange(["-Version", request.Version]);
            return (PwshExecutable(), win);
        }

        List<string> unix = ["scripts/bootstrap.sh"];
        if (request.ClientArchive is not null)
            unix.AddRange(["--client-archive", request.ClientArchive]);
        if (request.Version is not null)
            unix.AddRange(["--version", request.Version]);
        return ("bash", unix);
    }

    internal (string Exe, IReadOnlyList<string> Args) PackageCommand(BuildRequest request)
    {
        string output = request.OutputDirectory;
        switch (probe.Os)
        {
            case OsKind.Windows:
                List<string> win = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(request.RepoRoot, "package.ps1"), "-OutputDir", output];
                if (request.ClientArchive is not null) win.AddRange(["-ClientArchive", request.ClientArchive]);
                if (request.Version is not null) win.AddRange(["-Version", request.Version]);
                return (PwshExecutable(), win);
            case OsKind.MacOs:
                string arch = probe.Arch == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
                List<string> mac = ["scripts/package-macos.sh", "--output", output, "--arch", arch];
                if (request.ClientArchive is not null) mac.AddRange(["--client-archive", request.ClientArchive]);
                if (request.Version is not null) mac.AddRange(["--version", request.Version]);
                return ("bash", mac);
            default:
                List<string> linux = ["scripts/package-linux.sh", "--output", output];
                if (request.ClientArchive is not null) linux.AddRange(["--client-archive", request.ClientArchive]);
                if (request.Version is not null) linux.AddRange(["--version", request.Version]);
                return ("bash", linux);
        }
    }

    private static string ScriptPath(string repoRoot, string scriptName) =>
        Path.GetFullPath(Path.Combine(repoRoot, "scripts", scriptName));

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024d * 1024 * 1024):0.#} GiB"
            : $"{bytes / (1024d * 1024):0.#} MiB";

    private static string FormatAvailable(StorageSpace? space) =>
        space is null ? "unknown" : FormatBytes(space.AvailableBytes);

    internal bool HasMatchingExtractionCaches(BuildRequest request)
    {
        string? version = ResolveVersion(request);
        if (version is null)
            return false;

        string vanilla = Path.Combine(request.RepoRoot, ".vanilla", "win-x64", "vintagestory");
        bool bootstrapCache = probe.Os == OsKind.Windows
            ? HasVersionMarker(vanilla, version) && File.Exists(Path.Combine(vanilla, "Vintagestory.exe"))
            : HasVersionedExtraction(vanilla, version);
        if (!bootstrapCache)
            return false;

        if (probe.Os == OsKind.Windows)
            return true;

        string packageCache = probe.Os == OsKind.MacOs
            ? Path.Combine(request.RepoRoot, ".vanilla", $"osx-{(probe.Arch == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64")}", "Vintage Story.app")
            : Path.Combine(request.RepoRoot, ".vanilla", "linux-x64", "vintagestory");
        return HasVersionedExtraction(packageCache, version);
    }

    private static bool HasVersionedExtraction(string directory, string version) =>
        HasVersionMarker(directory, version)
        && File.Exists(Path.Combine(directory, "VintagestoryLib.vanilla.dll"));

    private static bool HasVersionMarker(string directory, string version) =>
        File.Exists(Path.Combine(directory, "assets", $"version-{version}.txt"));

    internal static bool HasLowSpace(StorageSpace? source, StorageSpace? temporary)
    {
        if (source is not null && temporary is not null
            && string.Equals(source.VolumeRoot, temporary.VolumeRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return source.AvailableBytes < EstimatedSourceWorkspaceBytes + EstimatedTemporaryWorkspaceBytes;
        }

        return (source is not null && source.AvailableBytes < EstimatedSourceWorkspaceBytes)
            || (temporary is not null && temporary.AvailableBytes < EstimatedTemporaryWorkspaceBytes);
    }

    private static string? ResolveVersion(BuildRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Version))
            return request.Version;

        try
        {
            using JsonDocument forks = JsonDocument.Parse(File.ReadAllText(Path.Combine(request.RepoRoot, "forks.json")));
            return forks.RootElement.GetProperty("vintageStoryVersion").GetString();
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private string DotnetExecutable() => DotnetSdkProbe.Find(probe) ?? "dotnet";

    /// <summary>
    /// The PowerShell interpreter for the bootstrap and packaging scripts:
    /// <c>pwsh</c> when present, Windows PowerShell 5.1 as the Windows fallback.
    /// </summary>
    private string PwshExecutable() =>
        PowerShellHost.Resolve(probe) ?? (probe.Os == OsKind.Windows ? "powershell" : "pwsh");

    /// <summary>
    /// The package artifact the platform's packaging script produces: a
    /// <c>Optimum-v*</c> directory on Windows and Linux, an <c>Optimum.app</c>
    /// bundle on macOS.
    /// </summary>
    private string? LocatePackage(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
            return null;

        if (probe.Os == OsKind.MacOs)
        {
            string app = Path.Combine(outputDirectory, "Optimum.app");
            return Directory.Exists(app) ? app : null;
        }

        return Directory.EnumerateDirectories(outputDirectory, "Optimum-v*")
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderBy(d => d, StringComparer.Ordinal)
            .LastOrDefault();
    }

    private async Task<StepOutcome> RunStep(
        (string Exe, IReadOnlyList<string> Args) command,
        string workingDirectory,
        ProgressPhase phase,
        int startPercent,
        int endPercent,
        IBuildObserver observer,
        bool clearPlatformEnv,
        CancellationToken forceful,
        CancellationToken graceful)
    {
        observer.Phase(phase, startPercent, $"{command.Exe} {string.Join(' ', command.Args)}");

        var collected = new StringBuilder();
        int exitCode = -1;
        int reported = startPercent;
        int linesSincePhase = 0;

        Command cmd = Cli.Wrap(command.Exe)
            .WithArguments(command.Args)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .WithEnvironmentVariables(env =>
            {
                if (clearPlatformEnv)
                {
                    env.Set("Platform", null).Set("PLATFORM", null);
                }

                char sep = probe.Os == OsKind.Windows ? ';' : ':';
                List<string> prependParts = [];

                string? dotnet = DotnetSdkProbe.Find(probe);
                if (dotnet is not null)
                {
                    string? dotnetDir = Path.GetDirectoryName(dotnet);
                    if (!string.IsNullOrEmpty(dotnetDir))
                    {
                        env.Set("DOTNET_ROOT", dotnetDir);
                        prependParts.Add(dotnetDir);
                        prependParts.Add(Path.Combine(probe.HomeDirectory, ".dotnet", "tools"));
                    }
                }

                if (probe.Os == OsKind.Windows)
                {
                    string? git = GitForWindows.FindExecutable(probe);
                    if (git is not null)
                    {
                        string? gitDir = Path.GetDirectoryName(git);
                        if (!string.IsNullOrEmpty(gitDir) && !prependParts.Contains(gitDir))
                            prependParts.Add(gitDir);
                    }
                }

                if (prependParts.Count > 0)
                {
                    string? currentPath = probe.GetEnvironmentVariable("PATH");
                    string prepended = string.Join(sep, prependParts);
                    string newPath = string.IsNullOrWhiteSpace(currentPath)
                        ? prepended
                        : prepended + sep + currentPath.Trim(sep);
                    env.Set("PATH", newPath);
                }
            });

        try
        {
            await foreach (CommandEvent commandEvent in
                cmd.ListenAsync(Encodings.Utf8, Encodings.Utf8, forceful, graceful))
            {
                switch (commandEvent)
                {
                    case StandardOutputCommandEvent stdout:
                        observer.RawOutput(false, stdout.Text);
                        collected.AppendLine(stdout.Text);
                        if (++linesSincePhase >= 25 && reported < endPercent - 1)
                        {
                            reported++;
                            linesSincePhase = 0;
                            observer.Phase(phase, reported, Trim(stdout.Text));
                        }
                        break;
                    case StandardErrorCommandEvent stderr:
                        observer.RawOutput(true, stderr.Text);
                        collected.AppendLine(stderr.Text);
                        break;
                    case ExitedCommandEvent exited:
                        exitCode = exited.ExitCode;
                        break;
                }
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // The executable is not on PATH / could not be spawned. Report it as
            // a failed step so RunAsync classifies it instead of the exception
            // escaping unobserved and hanging the wizard on the Progress screen.
            string message = $"could not start '{command.Exe}': {ex.Message}";
            observer.RawOutput(true, message);
            observer.Phase(phase, endPercent, message);
            return new StepOutcome(false, -1, message);
        }

        observer.Phase(phase, endPercent, exitCode == 0 ? "done" : $"exited {exitCode}");
        return new StepOutcome(exitCode == 0, exitCode, collected.ToString());
    }

    private static string Trim(string line) => line.Length <= 120 ? line : line[..120];

    /// <summary>
    /// Removes what the build wrote. The output guard guarantees the directory
    /// was empty or absent, so when it pre-existed only its new contents are
    /// removed and the directory itself is left in place.
    /// </summary>
    private static void CleanOutput(string directory, bool preexisted)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            if (!preexisted)
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private readonly record struct StepOutcome(bool Ok, int ExitCode, string Output);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
