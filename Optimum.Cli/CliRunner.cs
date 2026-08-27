using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Licensing;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;

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
        CancellationToken externalCancellation = default)
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
            "build" => await Build(rest, probe, buildDriver, output, cancellation.Token),
            "install" => Install(rest, probe, output),
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
        EngineOutput output,
        CancellationToken cancellationToken)
    {
        var parsed = new CliArgs(args, new HashSet<string>
        {
            "--output", "--client-archive", "--version", "--repo-root",
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

        string? repoRoot = ResolveRepoRoot(probe, parsed.Get("--repo-root"));
        if (repoRoot is null)
            return output.Failure(FailureReason.BadInput, "run this from inside an Optimum checkout, or pass --repo-root");

        var request = new BuildRequest(repoRoot, Path.GetFullPath(outputDir), clientArchive, parsed.Get("--version"));

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

        DeployResult result = new PackageDeployer(probe).Deploy(
            new DeployRequest(package, installDir, parsed.Get("--data-path"), shortcuts), output);

        return result.Ok
            ? output.Success(result.InstallDirectory!)
            : output.Failure(result.Reason ?? FailureReason.EngineInternal, result.Message ?? "install failed");
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
        return Path.GetFullPath(value);
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
        stderr.WriteLine($"  build         {ConsentNotice.AcknowledgeFlag} --output <abs> [--client-archive <abs>] [--version <v>]");
        stderr.WriteLine("  install       --package <abs> --install-dir <abs> [--data-path <abs>] [--shortcuts menu,desktop]");
        stderr.WriteLine("  validate      --package <abs>");
        stderr.WriteLine("  uninstall     --install-dir <abs>");
        stderr.WriteLine("  capabilities  [--repo-root <dir>]");
        stderr.WriteLine("  --version");
    }
}
