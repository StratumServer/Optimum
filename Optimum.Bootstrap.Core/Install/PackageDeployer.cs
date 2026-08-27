using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Paths;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Install;

[Flags]
public enum ShortcutKinds
{
    None = 0,
    Menu = 1,
    Desktop = 2,
}

public sealed record DeployRequest(
    string PackageDirectory,
    string InstallDirectory,
    string? DataPath = null,
    ShortcutKinds Shortcuts = ShortcutKinds.None);

public sealed record DeployResult(bool Ok, FailureReason? Reason, string? Message, string? InstallDirectory, string? Launcher)
{
    public static DeployResult Failure(FailureReason reason, string message) => new(false, reason, message, null, null);

    public static DeployResult Success(string installDirectory, string? launcher) =>
        new(true, null, null, installDirectory, launcher);
}

/// <summary>
/// Deploys a staged package to a chosen directory and records an
/// <see cref="InstallManifest"/>. Phase 2 does a straight copy after the path
/// guard clears; the stage, backup, and rollback dance from
/// <c>Install-StagedPackage</c> lands in Phase 4.
/// </summary>
public sealed class PackageDeployer(ISystemProbe probe)
{
    public DeployResult Deploy(DeployRequest request, IBuildObserver? observer = null)
    {
        InstallPathVerdict guard = InstallPathGuard.Check(probe, new InstallPathRequest(
            request.InstallDirectory, request.DataPath));
        if (!guard.Ok)
            return DeployResult.Failure(FailureReason.BadInput, guard.Rejection!);

        PackageLayoutResult layout = PackageLayout.Validate(probe, request.PackageDirectory);
        if (!layout.Ok)
            return DeployResult.Failure(FailureReason.BadInput,
                "the package directory is not a staged Optimum package: " + string.Join("; ", layout.Problems));

        string installDir = Path.GetFullPath(request.InstallDirectory);

        if (Directory.Exists(installDir) && Directory.EnumerateFileSystemEntries(installDir).Any())
        {
            string manifestPath = Path.Combine(installDir, InstallManifest.RelativePath);
            if (!File.Exists(manifestPath))
                return DeployResult.Failure(FailureReason.OutputExists,
                    $"the install directory is not empty and carries no Optimum manifest: {installDir}");
            observer?.Log(LogLevel.Info, "replacing an existing Optimum install");
            Directory.Delete(installDir, recursive: true);
        }

        Directory.CreateDirectory(installDir);
        var entries = new List<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(request.PackageDirectory))
        {
            string name = Path.GetFileName(entry);
            entries.Add(name);
            string target = Path.Combine(installDir, name);
            if (Directory.Exists(entry))
                CopyDirectory(entry, target);
            else
                File.Copy(entry, target, overwrite: true);
        }

        MakeExecutable(Path.Combine(installDir, "Optimum"));
        MakeExecutable(Path.Combine(installDir, "run.sh"));

        string? launcher = WriteLauncher(installDir, request.DataPath);
        if (launcher is not null && !entries.Contains(Path.GetFileName(launcher)))
            entries.Add(Path.GetFileName(launcher));
        if (request.DataPath is not null)
            entries.Add("datapath.cfg");

        string version = probe.ReadText(Path.Combine(request.PackageDirectory, ".optimum", "version"))?.Trim()
            ?? "dev";

        var manifest = new InstallManifest
        {
            OptimumVersion = version,
            InstalledAtUtc = DateTimeOffset.UtcNow,
            InstallDirectory = installDir,
            DataPath = request.DataPath,
            Launcher = launcher,
            Entries = entries.Distinct().OrderBy(e => e, StringComparer.Ordinal).ToArray(),
        };
        Directory.CreateDirectory(Path.Combine(installDir, ".optimum"));
        File.WriteAllText(Path.Combine(installDir, InstallManifest.RelativePath), manifest.Serialize());

        return DeployResult.Success(installDir, launcher);
    }

    private string? WriteLauncher(string installDir, string? dataPath)
    {
        if (probe.Os == OsKind.Windows)
        {
            string cmd = Path.Combine(installDir, "optimum-launch.cmd");
            string body = dataPath is not null
                ? $"@echo off\r\ncd /d \"%~dp0\"\r\nOptimum.exe --dataPath \"{dataPath}\" %*\r\n"
                : "@echo off\r\ncd /d \"%~dp0\"\r\nOptimum.exe %*\r\n";
            File.WriteAllText(cmd, body);
            if (dataPath is not null)
            {
                Directory.CreateDirectory(dataPath);
                File.WriteAllText(Path.Combine(installDir, "datapath.cfg"), dataPath);
            }
            return cmd;
        }

        string sh = Path.Combine(installDir, "optimum-launch.sh");
        string script = dataPath is not null
            ? $"#!/usr/bin/env bash\nset -euo pipefail\ncd \"$(dirname \"${{BASH_SOURCE[0]}}\")\"\nexec ./run.sh --dataPath {ShellQuote(dataPath)} \"$@\"\n"
            : "#!/usr/bin/env bash\nset -euo pipefail\ncd \"$(dirname \"${BASH_SOURCE[0]}\")\"\nexec ./run.sh \"$@\"\n";
        File.WriteAllText(sh, script);
        MakeExecutable(sh);
        if (dataPath is not null)
        {
            Directory.CreateDirectory(dataPath);
            File.WriteAllText(Path.Combine(installDir, "datapath.cfg"), dataPath);
        }
        return sh;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;
        try
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, destination, StringComparison.Ordinal));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, destination, StringComparison.Ordinal), overwrite: true);
    }
}
