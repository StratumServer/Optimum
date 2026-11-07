using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Licensing;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;
using Optimum.Bootstrap.Core.Update;
using Optimum.Update;

namespace Optimum.Cli;

/// <summary>
/// Parses the verb and flags, dispatches to the engine, and writes the NDJSON or
/// plain output. The verbs and their contract are INSTALLER-PLAN.md section 4.
/// </summary>
public static class CliRunner
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitUsage = 2;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr) =>
        RunAsync(args, stdout, stderr, SystemProbe.Default, new ScriptBuildDriver(SystemProbe.Default));

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        ISystemProbe probe,
        IBuildDriver buildDriver,
        CancellationToken externalCancellation = default,
        ISourceProvider? sourceProvider = null,
        IGamePatcher? gamePatcher = null)
    {
        if (args.Count == 1 && args[0] == "--version")
        {
            stdout.WriteLine(CoreInfo.Version);
            return ExitOk;
        }

        if (args.Count == 0)
        {
            WriteUsage(stderr);
            return ExitUsage;
        }

        string verb = args[0];
        var rest = args.Skip(1).ToArray();
        bool json = rest.Contains("--json");

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using PosixSignalRegistration intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            cancellation.Cancel();
        }

        var output = new EngineOutput(stdout, stderr, json);

        return verb switch
        {
            "preflight" => Preflight(rest, probe, output),
            "capabilities" => Capabilities(rest, probe, stdout, stderr),
            "build" => await Build(rest, probe, buildDriver, sourceProvider ?? new GitSourceProvider(probe), output, cancellation.Token),
            "install" => Install(rest, probe, output),
            "patch" => await Patch(rest, probe, gamePatcher ?? new GamePatcher(probe), output, cancellation.Token),
            "apply-update" => await ApplyUpdate(rest, gamePatcher ?? new GamePatcher(probe), output, cancellation.Token),
            "validate" => Validate(rest, probe, output),
            "uninstall" => Uninstall(rest, probe, output),
            _ => Unknown(verb, stderr),
        };
    }

    private static int Preflight(IReadOnlyList<string> args, ISystemProbe probe, EngineOutput output)
    {
        var parsed = new CliArgs(args, new HashSet<string> { "--repo-root" });
        if (parsed.Errors.Count > 0)
            return output.Failure(FailureReason.BadInput, string.Join("; ", parsed.Errors));

        string? repoRoot = ResolveRepoRoot(probe, parsed.Get("--repo-root"));
        if (repoRoot is null)
            return output.Failure(FailureReason.BadInput, "run this from inside an Optimum checkout, or pass --repo-root");

        IReadOnlyList<PrerequisiteResult> results = new PrerequisiteScanner(probe, repoRoot).Scan();

        var jsonArray = results.Select(r => new
        {
            id = r.Definition.Id.ToString(),
            command = r.Definition.Command,
            level = r.Definition.Level.ToString(),
            state = r.State.ToString(),
            label = r.Label,
            blocksBuild = r.BlocksBuild,
            acquisition = r.Acquisition.ToString(),
            acquisitionCommand = r.AcquisitionCommand,
            downloadUrl = r.DownloadUrl,
        });

        var human = new StringBuilder();
        foreach (PrerequisiteResult r in results)
            human.AppendLine($"{r.State,-15} {r.Definition.Command,-14} {r.Label}");

        output.Answer(JsonSerializer.Serialize(jsonArray, Json), human.ToString().TrimEnd());
        return results.Any(r => r.BlocksBuild) ? ExitError : ExitOk;
    }

    private static int Capabilities(IReadOnlyList<string> args, ISystemProbe probe, TextWriter stdout, TextWriter stderr)
    {
        var parsed = new CliArgs(args, new HashSet<string> { "--repo-root" });
        if (parsed.Errors.Count > 0)
        {
            stderr.WriteLine(string.Join("; ", parsed.Errors));
            return ExitUsage;
        }

        string? repoRoot = ResolveRepoRoot(probe, parsed.Get("--repo-root"));
        if (repoRoot is null)
        {
            stderr.WriteLine("run this from inside an Optimum checkout, or pass --repo-root");
            return ExitUsage;
        }

        EngineCapabilities caps = Bootstrap.Core.Build.Capabilities.Read(probe, repoRoot);
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            optimumVersion = CoreInfo.Version,
            pinnedVersion = caps.PinnedVersion,
            supportedVersions = caps.SupportedVersions,
            patchSets = caps.PatchSets,
        }, Json));
        return ExitOk;
    }

    private static async Task<int> Build(
        IReadOnlyList<string> args,
        ISystemProbe probe,
        IBuildDriver driver,
        ISourceProvider sourceProvider,
        EngineOutput output,
        CancellationToken cancellationToken)
    {
        var parsed = new CliArgs(args, new HashSet<string>
        {
            "--output", "--client-archive", "--version", "--repo-root", "--source-cache",
        });
        if (parsed.Errors.Count > 0)
            return output.Failure(FailureReason.BadInput, string.Join("; ", parsed.Errors));

        if (!parsed.Has(ConsentNotice.AcknowledgeFlag))
        {
            return output.Failure(FailureReason.BadInput,
                $"build decompiles Vintage Story on this machine. Pass {ConsentNotice.AcknowledgeFlag} to confirm you accept that and the terms in the consent notice.");
        }

        string? outputDir = parsed.Get("--output");
        if (outputDir is null)
            return output.Failure(FailureReason.BadInput, "--output is required");
        if (!Path.IsPathRooted(outputDir))
            return output.Failure(FailureReason.BadInput, $"--output must be an absolute path: {outputDir}");

        string? clientArchive = parsed.Get("--client-archive");
        if (clientArchive is not null)
        {
            if (!Path.IsPathRooted(clientArchive))
                return output.Failure(FailureReason.BadInput, $"--client-archive must be an absolute path: {clientArchive}");
            if (!probe.FileExists(clientArchive))
                return output.Failure(FailureReason.BadInput, $"--client-archive does not exist: {clientArchive}");
        }

        string? sourceCache = parsed.Get("--source-cache");
        if (sourceCache is not null && !Path.IsPathRooted(sourceCache))
            return output.Failure(FailureReason.BadInput, $"--source-cache must be an absolute path: {sourceCache}");

        string? repoRoot = ResolveRepoRoot(probe, parsed.Get("--repo-root"));
        if (repoRoot is null)
        {
            if (!parsed.Has("--acquire-source"))
            {
                return output.Failure(FailureReason.BadInput,
                    "run this from inside an Optimum checkout, pass --repo-root, or pass --acquire-source to download it");
            }

            SourceAcquisitionResult acquired;
            try
            {
                acquired = await sourceProvider.EnsureAsync(
                    new SourceRequest(CoreInfo.Version, sourceCache), output, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return output.Failure(FailureReason.Cancelled, "the source download was cancelled");
            }
            catch (Exception ex)
            {
                return output.Failure(FailureReason.SourceUnavailable,
                    $"could not obtain the Optimum source: {ex.Message}");
            }

            if (!acquired.Ok || string.IsNullOrWhiteSpace(acquired.RepoRoot))
            {
                return output.Failure(
                    acquired.Reason ?? FailureReason.SourceUnavailable,
                    acquired.Message ?? "could not obtain the Optimum source");
            }
            repoRoot = acquired.RepoRoot;
        }

        var request = new BuildRequest(repoRoot!, Path.GetFullPath(outputDir), clientArchive, parsed.Get("--version"));

        BuildResult result;
        try
        {
            result = await driver.RunAsync(request, output, cancellationToken);  // single token: SIGTERM is a straight stop for the CLI
        }
        catch (OperationCanceledException)
        {
            result = BuildResult.Failure(FailureReason.Cancelled, "the build was cancelled");
        }

        return result.Ok
            ? output.Success(result.RuntimePath!)
            : output.Failure(result.Reason ?? FailureReason.EngineInternal, result.Message ?? "unknown failure");
    }

    private static int Install(IReadOnlyList<string> args, ISystemProbe probe, EngineOutput output)
    {
        var parsed = new CliArgs(args, new HashSet<string>
        {
            "--package", "--install-dir", "--data-path", "--shortcuts",
        });
        if (parsed.Errors.Count > 0)
            return output.Failure(FailureReason.BadInput, string.Join("; ", parsed.Errors));

        string? package = RequireAbsolute(parsed.Get("--package"), "--package", output, out int packageError);
        if (package is null)
            return packageError;
        string? installDir = RequireAbsolute(parsed.Get("--install-dir"), "--install-dir", output, out int installError);
        if (installDir is null)
            return installError;

        ShortcutKinds shortcuts = ParseShortcuts(parsed.Get("--shortcuts"));
        bool clean = parsed.Has("--clean") || parsed.Has("--force");

        DeployResult result = new PackageDeployer(probe).Deploy(
            new DeployRequest(package, installDir, parsed.Get("--data-path"), shortcuts, clean), output);

        return result.Ok
            ? output.Success(result.InstallDirectory!)
            : output.Failure(result.Reason ?? FailureReason.EngineInternal, result.Message ?? "install failed");
    }

    private static async Task<int> Patch(
        IReadOnlyList<string> args,
        ISystemProbe probe,
        IGamePatcher patcher,
        EngineOutput output,
        CancellationToken cancellationToken)
    {
        var parsed = new CliArgs(args, new HashSet<string> { "--game-dir", "--overlay" });
        if (parsed.Errors.Count > 0)
            return output.Failure(FailureReason.BadInput, string.Join("; ", parsed.Errors));

        string? gameDir = RequireAbsolute(parsed.Get("--game-dir"), "--game-dir", output, out int gameDirError);
        if (gameDir is null)
            return gameDirError;

        string? overlayDir = null;
        string? rawOverlay = parsed.Get("--overlay");
        if (rawOverlay is not null)
        {
            overlayDir = RequireAbsolute(rawOverlay, "--overlay", output, out int overlayError);
            if (overlayDir is null)
                return overlayError;
        }

        bool backup = !parsed.Has("--no-backup");
        bool rollback = parsed.Has("--rollback");

        var request = new PatchRequest(gameDir, overlayDir, backup, rollback);

        PatchResult result;
        try
        {
            result = await patcher.PatchAsync(request, output, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = PatchResult.Failure(FailureReason.Cancelled, "the patch operation was cancelled");
        }
        catch (Exception ex)
        {
            result = PatchResult.Failure(FailureReason.EngineInternal, $"patch failed: {ex.Message}");
        }

        return result.Ok
            ? output.Success(result.GameDirectory!)
            : output.Failure(result.Reason ?? FailureReason.EngineInternal, result.Message ?? "patch failed");
    }

    /// <summary>
    /// Applies an update the game staged under .optimum/pending, after the game has exited, and brings the
    /// previous version back if it fails. See <see cref="UpdateApplier"/>. When <c>--restart</c> is given the
    /// game is started again whatever the outcome, so a failed update does not leave the player without a game.
    /// </summary>
    private static async Task<int> ApplyUpdate(
        IReadOnlyList<string> args,
        IGamePatcher patcher,
        EngineOutput output,
        CancellationToken cancellationToken)
    {
        var parsed = new CliArgs(args, new HashSet<string> { "--game-dir", "--overlay", "--wait-pid", "--wait-seconds", "--manifest-sha256", "--restart", "--restart-args" });
        if (parsed.Errors.Count > 0)
            return output.Failure(FailureReason.BadInput, string.Join("; ", parsed.Errors));

        // The game runs this before it closes, so a release whose CLI cannot apply an update is found out while
        // the player still has a window to read the explanation in. A CLI without the verb answers "unknown verb".
        if (parsed.Has("--probe"))
            return output.Success("apply-update");

        string? gameDir = RequireAbsolute(parsed.Get("--game-dir"), "--game-dir", output, out int gameDirError);
        if (gameDir is null)
            return gameDirError;
        string? overlayDir = RequireAbsolute(parsed.Get("--overlay"), "--overlay", output, out int overlayError);
        if (overlayDir is null)
            return overlayError;

        string? manifestSha256 = parsed.Get("--manifest-sha256");
        if (!OverlayStager.IsValidSha256Digest(manifestSha256 ?? ""))
            return output.Failure(FailureReason.BadInput, "--manifest-sha256 must be a 64-character SHA-256 digest");

        int? waitPid = null;
        if (parsed.Get("--wait-pid") is string rawPid)
        {
            if (!int.TryParse(rawPid, out int pid) || pid <= 0)
                return output.Failure(FailureReason.BadInput, "--wait-pid must be a process id");
            waitPid = pid;
        }
        TimeSpan? wait = null;
        if (parsed.Get("--wait-seconds") is string rawWait)
        {
            if (!int.TryParse(rawWait, out int seconds) || seconds <= 0 || seconds > 3600)
                return output.Failure(FailureReason.BadInput, "--wait-seconds must be between 1 and 3600");
            wait = TimeSpan.FromSeconds(seconds);
        }
        string? restart = parsed.Get("--restart");
        if (restart is not null && !Path.IsPathRooted(restart))
            return output.Failure(FailureReason.BadInput, "--restart must be an absolute path");

        ApplyOutcome outcome;
        try
        {
            outcome = await new UpdateApplier(patcher).ApplyAsync(
                new ApplyRequest(gameDir, overlayDir, waitPid, wait, manifestSha256), output, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            outcome = new ApplyOutcome(false, "unknown", "the update was cancelled", false);
        }

        // Only start the game again when the old one is really gone; a game that never exited is still running.
        bool gameStillRunning = outcome.Reason.StartsWith("the game did not close", StringComparison.Ordinal);
        if (restart is not null && !gameStillRunning)
            TryStart(restart, parsed.Get("--restart-args"), output);

        return outcome.Ok
            ? output.Success(gameDir)
            : output.Failure(outcome.RolledBack ? FailureReason.PatchConflict : FailureReason.VerificationFailed, outcome.Reason);
    }

    private static void TryStart(string executable, string? arguments, EngineOutput output)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            };
            if (!string.IsNullOrEmpty(arguments)) start.Arguments = arguments;
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            output.Log(LogLevel.Warn, $"could not start {executable}: {ex.Message}");
        }
    }

    private static int Validate(IReadOnlyList<string> args, ISystemProbe probe, EngineOutput output)
    {
        var parsed = new CliArgs(args, new HashSet<string> { "--package" });
        if (parsed.Errors.Count > 0)
            return output.Failure(FailureReason.BadInput, string.Join("; ", parsed.Errors));

        string? package = RequireAbsolute(parsed.Get("--package"), "--package", output, out int packageError);
        if (package is null)
            return packageError;

        RuntimeValidationResult result = new RuntimeValidator(probe).Validate(package);
        return result.Ok
            ? output.Success(package)
            : output.Failure(FailureReason.VerificationFailed, result.Detail ?? "runtime validation failed");
    }

    private static int Uninstall(IReadOnlyList<string> args, ISystemProbe probe, EngineOutput output)
    {
        var parsed = new CliArgs(args, new HashSet<string> { "--install-dir" });
        if (parsed.Errors.Count > 0)
            return output.Failure(FailureReason.BadInput, string.Join("; ", parsed.Errors));

        string? installDir = RequireAbsolute(parsed.Get("--install-dir"), "--install-dir", output, out int installError);
        if (installDir is null)
            return installError;

        UninstallResult result = new Uninstaller(probe).Uninstall(installDir);
        return result.Ok
            ? output.Success(installDir)
            : output.Failure(result.Reason ?? FailureReason.EngineInternal, result.Message ?? "uninstall failed");
    }

    private static string? RequireAbsolute(string? value, string name, EngineOutput output, out int errorCode)
    {
        if (value is null)
        {
            errorCode = output.Failure(FailureReason.BadInput, $"{name} is required");
            return null;
        }
        if (!Path.IsPathRooted(value))
        {
            errorCode = output.Failure(FailureReason.BadInput, $"{name} must be an absolute path: {value}");
            return null;
        }
        errorCode = ExitOk;
        // The value is already validated as absolute; trim a trailing separator
        // but do not run it through Path.GetFullPath, which on Windows rewrites a
        // POSIX path against the host drive (e.g. /abs/game -> C:\abs\game).
        return value.Length > 1 ? value.TrimEnd('/', '\\') : value;
    }

    private static ShortcutKinds ParseShortcuts(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return ShortcutKinds.None;
        ShortcutKinds result = ShortcutKinds.None;
        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("menu", StringComparison.OrdinalIgnoreCase)) result |= ShortcutKinds.Menu;
            if (part.Equals("desktop", StringComparison.OrdinalIgnoreCase)) result |= ShortcutKinds.Desktop;
            if (part.Equals("uri", StringComparison.OrdinalIgnoreCase)
                || part.Equals("uri-handler", StringComparison.OrdinalIgnoreCase))
            {
                result |= ShortcutKinds.UriHandler;
            }
        }
        return result;
    }

    internal static string? ResolveRepoRoot(ISystemProbe probe, string? explicitRoot) =>
        RepoRoot.Discover(probe, explicitRoot);

    private static int Unknown(string verb, TextWriter stderr)
    {
        stderr.WriteLine($"unknown verb: {verb}");
        WriteUsage(stderr);
        return ExitUsage;
    }

    private static void WriteUsage(TextWriter stderr)
    {
        stderr.WriteLine("usage: optimum <verb> [--json] [flags]");
        stderr.WriteLine("verbs:");
        stderr.WriteLine("  preflight     [--repo-root <dir>]");
        stderr.WriteLine($"  build         {ConsentNotice.AcknowledgeFlag} --output <abs> [--client-archive <abs>] [--version <v>] [--acquire-source [--source-cache <abs>]]");
        stderr.WriteLine("  install       --package <abs> --install-dir <abs> [--data-path <abs>] [--shortcuts menu,desktop,uri]");
        stderr.WriteLine("  patch         --game-dir <abs> [--overlay <abs>] [--backup|--no-backup] [--rollback]");
        stderr.WriteLine("  apply-update  --game-dir <abs> --overlay <abs> --manifest-sha256 <hex> [--wait-pid <id>] [--wait-seconds <n>] [--restart <abs> [--restart-args <text>]]");
        stderr.WriteLine("  validate      --package <abs>");
        stderr.WriteLine("  uninstall     --install-dir <abs>");
        stderr.WriteLine("  capabilities  [--repo-root <dir>]");
        stderr.WriteLine("  --version");
    }
}
