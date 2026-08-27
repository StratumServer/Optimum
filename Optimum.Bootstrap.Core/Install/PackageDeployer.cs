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
/// Deploys a staged package to an empty or absent directory and records an
/// <see cref="InstallManifest"/>. Phase 2 does a straight copy after the path
/// guard clears and refuses to touch a directory that already holds anything;
/// replacing an existing install in place, with a backup and rollback, is Phase
/// 4. To reinstall now, run <c>uninstall</c> first.
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
            bool isOptimumInstall = File.Exists(Path.Combine(installDir, InstallManifest.RelativePath));
            return DeployResult.Failure(FailureReason.OutputExists, isOptimumInstall
                ? $"an Optimum install already exists at {installDir}. Run `optimum uninstall --install-dir {installDir}` first."
                : $"the install directory is not empty: {installDir}");
        }

        observer?.Log(LogLevel.Info, $"deploying {Path.GetFileName(request.PackageDirectory)} to {installDir}");
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

        string version = ResolveVersion(probe, request.PackageDirectory);

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

    /// <summary>
    /// The package version, from the <c>Optimum-v&lt;version&gt;-&lt;rid&gt;</c> directory
    /// name the packaging scripts produce, falling back to a <c>.optimum/version</c>
    /// file and then to <c>dev</c>.
    /// </summary>
    private static string ResolveVersion(ISystemProbe probe, string packageDirectory)
    {
        string name = Path.GetFileName(packageDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.StartsWith("Optimum-v", StringComparison.Ordinal))
        {
            string rest = name["Optimum-v".Length..];
            int dash = rest.IndexOf('-');
            string version = dash > 0 ? rest[..dash] : rest;
            if (version.Length > 0)
                return version;
        }

        return probe.ReadText(Path.Combine(packageDirectory, ".optimum", "version"))?.Trim() is { Length: > 0 } fromFile
            ? fromFile
            : "dev";
    }

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
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }
}
