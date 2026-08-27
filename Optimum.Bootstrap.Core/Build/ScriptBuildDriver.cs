using System.Text;
using CliWrap;
using CliWrap.EventStream;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;

namespace Optimum.Bootstrap.Core.Build;

/// <summary>
/// The real build pipeline: it drives <c>scripts/bootstrap.*</c>,
/// <c>dotnet build VintageStory.slnx</c>, and the platform packaging script
/// through CliWrap, the same sequence <c>.github/workflows/ci-platform-bootstrap.yml</c>
/// runs by hand. It never reimplements those scripts.
/// </summary>
public sealed class ScriptBuildDriver(ISystemProbe probe) : IBuildDriver
{
    public async Task<BuildResult> RunAsync(BuildRequest request, IBuildObserver observer, CancellationToken cancellationToken)
    {
        var scanner = new PrerequisiteScanner(probe, request.RepoRoot);
        string[] missing = scanner.Scan().Where(r => r.BlocksBuild).Select(r => r.Definition.DisplayName).ToArray();
        if (missing.Length > 0)
            return BuildResult.Failure(FailureReason.BadInput, "Required tools missing: " + string.Join(", ", missing));

        if (probe.DirectoryExists(request.OutputDirectory)
            && probe.EnumerateFiles(request.OutputDirectory, "*").Any())
        {
            return BuildResult.Failure(FailureReason.OutputExists,
                $"The output directory is not empty: {request.OutputDirectory}");
        }

        Directory.CreateDirectory(request.OutputDirectory);

        try
        {
            StepOutcome bootstrap = await RunStep(
                BootstrapCommand(request), request.RepoRoot, ProgressPhase.Decompile, 2, 50, observer,
                clearPlatformEnv: false, cancellationToken);
            if (!bootstrap.Ok)
            {
                return BuildResult.Failure(
                    BootstrapFailureClassifier.Classify(bootstrap.Output),
                    $"bootstrap exited {bootstrap.ExitCode}");
            }

            observer.Phase(ProgressPhase.Patch, 52, "patches applied");

            StepOutcome build = await RunStep(
                (DotnetExecutable(), ["build", "VintageStory.slnx", "-c", "Release", "--nologo"]),
                request.RepoRoot, ProgressPhase.Assemble, 55, 85, observer,
                clearPlatformEnv: true, cancellationToken);
            if (!build.Ok)
                return BuildResult.Failure(FailureReason.AssembleFailed, $"dotnet build exited {build.ExitCode}");

            StepOutcome package = await RunStep(
                PackageCommand(request), request.RepoRoot, ProgressPhase.Assemble, 85, 96, observer,
                clearPlatformEnv: false, cancellationToken);
            if (!package.Ok)
                return BuildResult.Failure(FailureReason.AssembleFailed, $"packaging exited {package.ExitCode}");

            string? produced = LocatePackage(request.OutputDirectory);
            if (produced is null)
                return BuildResult.Failure(FailureReason.AssembleFailed,
                    $"the packaging script produced no package directory under {request.OutputDirectory}");

            observer.Phase(ProgressPhase.Verify, 98, "package produced");
            return BuildResult.Success(produced);
        }
        catch (OperationCanceledException)
        {
            TryClean(request.OutputDirectory);
            return BuildResult.Failure(FailureReason.Cancelled, "the build was cancelled");
        }
    }

    private (string Exe, IReadOnlyList<string> Args) BootstrapCommand(BuildRequest request)
    {
        var args = new List<string>();
        if (probe.Os == OsKind.Windows)
        {
            args.AddRange(["-File", "scripts/bootstrap.ps1"]);
            args.AddRange(["-ClientArchive", request.ClientArchive ?? "__skip__"]);
            if (request.Version is not null)
                args.AddRange(["-Version", request.Version]);
            return ("pwsh", args);
        }

        args.Add("scripts/bootstrap.sh");
        if (request.ClientArchive is not null)
            args.AddRange(["--client-archive", request.ClientArchive]);
        if (request.Version is not null)
            args.AddRange(["--version", request.Version]);
        return ("bash", args);
    }

    private (string Exe, IReadOnlyList<string> Args) PackageCommand(BuildRequest request)
    {
        string output = request.OutputDirectory;
        switch (probe.Os)
        {
            case OsKind.Windows:
                return ("pwsh", ["-File", "scripts/package.ps1", "-OutputDir", output]);
            case OsKind.MacOs:
                string arch = probe.Arch == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
                List<string> mac = ["scripts/package-macos.sh", "--output", output, "--arch", arch];
                if (request.Version is not null) mac.AddRange(["--version", request.Version]);
                return ("bash", mac);
            default:
                List<string> linux = ["scripts/package-linux.sh", "--output", output];
                if (request.Version is not null) linux.AddRange(["--version", request.Version]);
                return ("bash", linux);
        }
    }

    private string DotnetExecutable() => DotnetSdkProbe.Find(probe) ?? "dotnet";

    private static string? LocatePackage(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
            return null;
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
        CancellationToken cancellationToken)
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

        await foreach (CommandEvent commandEvent in cmd.ListenAsync(cancellationToken))
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

    private static void TryClean(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private readonly record struct StepOutcome(bool Ok, int ExitCode, string Output);
}
