using System.Text;
using CliWrap;
using CliWrap.EventStream;
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
public sealed class ScriptBuildDriver(ISystemProbe probe) : IBuildDriver
{
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

        Directory.CreateDirectory(request.OutputDirectory);

        try
        {
            StepOutcome bootstrap = await RunStep(
                BootstrapCommand(request), request.RepoRoot, ProgressPhase.Decompile, 2, 48, observer,
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

            StepOutcome checkPatches = await RunStep(
                ("bash", ["scripts/check-patches.sh", "--strict-unavailable"]),
                request.RepoRoot, ProgressPhase.Patch, 82, 86, observer,
                clearPlatformEnv: false, forceful, graceful);
            if (!checkPatches.Ok)
                return BuildResult.Failure(FailureReason.PatchConflict,
                    $"check-patches.sh exited {checkPatches.ExitCode}: a patch did not survive the decompile round trip");

            StepOutcome package = await RunStep(
                PackageCommand(request), request.RepoRoot, ProgressPhase.Assemble, 86, 95, observer,
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
    }

    private (string Exe, IReadOnlyList<string> Args) BootstrapCommand(BuildRequest request)
    {
        if (probe.Os == OsKind.Windows)
        {
            List<string> win = ["-File", "scripts/bootstrap.ps1"];
            if (request.ClientArchive is not null)
                win.AddRange(["-ClientArchive", request.ClientArchive]);
            if (request.Version is not null)
                win.AddRange(["-Version", request.Version]);
            return ("pwsh", win);
        }

        List<string> unix = ["scripts/bootstrap.sh"];
        if (request.ClientArchive is not null)
            unix.AddRange(["--client-archive", request.ClientArchive]);
        if (request.Version is not null)
            unix.AddRange(["--version", request.Version]);
        return ("bash", unix);
    }

    private (string Exe, IReadOnlyList<string> Args) PackageCommand(BuildRequest request)
    {
        string output = request.OutputDirectory;
        switch (probe.Os)
        {
            case OsKind.Windows:
                List<string> win = ["-File", "scripts/package.ps1", "-OutputDir", output];
                if (request.ClientArchive is not null) win.AddRange(["-ClientArchive", request.ClientArchive]);
                return ("pwsh", win);
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

    private string DotnetExecutable() => DotnetSdkProbe.Find(probe) ?? "dotnet";

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
            .WithValidation(CommandResultValidation.None);
        if (clearPlatformEnv)
            cmd = cmd.WithEnvironmentVariables(env => env.Set("Platform", null).Set("PLATFORM", null));

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
}
