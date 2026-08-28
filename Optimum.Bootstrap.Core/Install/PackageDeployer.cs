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
/// Deploys a staged package transactionally, ported from <c>Install-StagedPackage</c>
/// in <c>scripts/install-windows.ps1</c>: build the whole new tree next to the
/// target, move an existing install aside, swap the new tree in with one rename,
/// then delete the backup. Any failure rolls back to the previous install. An
/// existing Optimum install (one with a manifest) is replaced this way; a
/// non-empty directory that is not an Optimum install is refused.
/// </summary>
public sealed class PackageDeployer(ISystemProbe probe) : IPackageInstaller
{
    /// <summary>Test hook: throws for the named step to exercise rollback.</summary>
    internal Action<string>? FailAtStep { get; set; }

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
        string? parent = Path.GetDirectoryName(installDir);
        if (parent is null)
            return DeployResult.Failure(FailureReason.BadInput, $"the install directory has no parent: {installDir}");

        bool hasExisting = Directory.Exists(installDir) && Directory.EnumerateFileSystemEntries(installDir).Any();
        if (hasExisting && !File.Exists(Path.Combine(installDir, InstallManifest.RelativePath)))
            return DeployResult.Failure(FailureReason.OutputExists, $"the install directory is not empty: {installDir}");

        Directory.CreateDirectory(parent);
        string token = Guid.NewGuid().ToString("N")[..12];
        string stageDir = Path.Combine(parent, $".optimum-stage-{token}");
        string backupDir = Path.Combine(parent, $".optimum-backup-{token}");
        bool backedUp = false;
        bool committed = false;
        string? launcher = null;

        try
        {
            Checkpoint("stage");
            observer?.Log(LogLevel.Info, $"staging {Path.GetFileName(request.PackageDirectory)} beside {installDir}");
            CopyDirectory(request.PackageDirectory, stageDir);

            MakeExecutable(Path.Combine(stageDir, "Optimum"));
            MakeExecutable(Path.Combine(stageDir, "run.sh"));

            var entries = Directory.EnumerateFileSystemEntries(stageDir)
                .Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList();
            launcher = WriteLauncher(stageDir, installDir, request.DataPath);
            if (launcher is not null)
                entries.Add(Path.GetFileName(launcher));
            if (request.DataPath is not null)
                entries.Add("datapath.cfg");

            WriteManifest(stageDir, installDir, request, launcher, entries);

            Checkpoint("backup");
            if (hasExisting || Directory.Exists(installDir))
            {
                MoveDirectory(installDir, backupDir);
                backedUp = true;
            }

            try
            {
                Checkpoint("swap");
                MoveDirectory(stageDir, installDir);
                committed = true;
            }
            catch
            {
                // The swap itself failed: the target is still absent (or the
                // move is all-or-nothing), so restore the backup and give up.
                RestoreBackup(installDir, backupDir, backedUp);
                throw;
            }

            // Past this point the install is complete and correct. Deleting the
            // backup and registering shortcuts are cleanup, not the transaction.
            Checkpoint("commit");
            string? finalLauncher = launcher is null ? null : Path.Combine(installDir, Path.GetFileName(launcher));
            try
            {
                if (backedUp && Directory.Exists(backupDir))
                    Directory.Delete(backupDir, recursive: true);
                RegisterInstall(installDir, request, finalLauncher, observer);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                observer?.Log(LogLevel.Warn,
                    $"the install is complete but a post-install step did not finish: {cleanup.Message}");
            }

            return DeployResult.Success(installDir, finalLauncher);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (committed)
                return DeployResult.Success(installDir, launcher is null ? null : Path.Combine(installDir, Path.GetFileName(launcher)));

            (bool restored, string? note) = RestoreBackup(installDir, backupDir, backedUp);
            string message = restored || !backedUp
                ? $"install failed and was rolled back: {ex.Message}"
                : $"install failed and the previous install could not be restored: {ex.Message}. {note}";
            return DeployResult.Failure(FailureReason.EngineInternal, message);
        }
        finally
        {
            TryDelete(stageDir);
            if (committed && Directory.Exists(backupDir))
                TryDelete(backupDir);
        }
    }

    private void Checkpoint(string step) => FailAtStep?.Invoke(step);

    /// <summary>
    /// After the swap: write shortcuts, register the Windows uninstall entry, and
    /// fold both into the manifest so <see cref="Uninstaller"/> can undo them. If
    /// the manifest cannot be updated the shortcuts and registry entry are undone
    /// so nothing is left that the manifest does not record.
    /// </summary>
    private void RegisterInstall(string installDir, DeployRequest request, string? launcher, IBuildObserver? observer)
    {
        string manifestPath = Path.Combine(installDir, InstallManifest.RelativePath);
        string? manifestJson;
        try { manifestJson = File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : null; }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        if (manifestJson is null || InstallManifest.Deserialize(manifestJson) is not { } manifest)
            return;

        IReadOnlyList<string> shortcuts = launcher is not null && request.Shortcuts != ShortcutKinds.None
            ? new ShortcutWriter(probe).Create(installDir, launcher, request.Shortcuts)
            : [];
        if (shortcuts.Count > 0)
            observer?.Log(LogLevel.Info, $"created {shortcuts.Count} shortcut(s)");

        string? registryKey = UninstallRegistration.Register(installDir, manifest.OptimumVersion);

        try
        {
            File.WriteAllText(manifestPath, (manifest with
            {
                Shortcuts = shortcuts,
                UninstallRegistryKey = registryKey,
            }).Serialize());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The manifest is the only record the uninstaller reads, so undo
            // what it will not know about.
            new ShortcutWriter(probe).Remove(shortcuts);
            UninstallRegistration.Unregister(registryKey);
            observer?.Log(LogLevel.Warn, "could not record shortcuts in the manifest; they were removed");
        }
    }

    /// <summary>
    /// Restores the pre-install tree. Returns whether it is now back in place and,
    /// if not, a note telling the user where the backup is.
    /// </summary>
    private static (bool Restored, string? Note) RestoreBackup(string installDir, string backupDir, bool backedUp)
    {
        if (!backedUp)
        {
            TryDelete(installDir);
            return (true, null);
        }

        if (!Directory.Exists(backupDir))
            return (!Directory.Exists(installDir) || IsOptimumInstall(installDir), null);

        if (Directory.Exists(installDir))
            TryDelete(installDir);

        if (Directory.Exists(installDir))
            return (false, $"the previous install is at {backupDir}");

        try
        {
            Directory.Move(backupDir, installDir);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"the previous install is at {backupDir}: {ex.Message}");
        }
    }

    private static bool IsOptimumInstall(string directory) =>
        File.Exists(Path.Combine(directory, InstallManifest.RelativePath));

    /// <summary>
    /// <see cref="Directory.Move"/> that turns a cross-filesystem failure into a
    /// clear message. This bites only when the install directory is itself a
    /// mount point, since the stage and backup always share its parent.
    /// </summary>
    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException ex) when (ex.Message.Contains("different", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("cross-device", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "the install directory is on a different filesystem from its parent; choose a directory whose parent is on the same filesystem", ex);
        }
    }

    private void WriteManifest(
        string stageDir, string installDir, DeployRequest request, string? launcher, IEnumerable<string> entries)
    {
        var manifest = new InstallManifest
        {
            OptimumVersion = ResolveVersion(probe, request.PackageDirectory),
            InstalledAtUtc = DateTimeOffset.UtcNow,
            InstallDirectory = installDir,
            DataPath = request.DataPath,
            Launcher = launcher is null ? null : Path.Combine(installDir, Path.GetFileName(launcher)),
            Entries = entries.Distinct().OrderBy(e => e, StringComparer.Ordinal).ToArray(),
        };
        Directory.CreateDirectory(Path.Combine(stageDir, ".optimum"));
        File.WriteAllText(Path.Combine(stageDir, InstallManifest.RelativePath), manifest.Serialize());
    }

    private string? WriteLauncher(string stageDir, string finalInstallDir, string? dataPath)
    {
        if (dataPath is not null)
            Directory.CreateDirectory(dataPath);

        if (probe.Os == OsKind.Windows)
        {
            string cmd = Path.Combine(stageDir, "optimum-launch.cmd");
            File.WriteAllText(cmd, dataPath is not null
                ? $"@echo off\r\ncd /d \"%~dp0\"\r\nOptimum.exe --dataPath \"{dataPath}\" %*\r\n"
                : "@echo off\r\ncd /d \"%~dp0\"\r\nOptimum.exe %*\r\n");
            if (dataPath is not null)
                File.WriteAllText(Path.Combine(stageDir, "datapath.cfg"), dataPath);
            return cmd;
        }

        string sh = Path.Combine(stageDir, "optimum-launch.sh");
        File.WriteAllText(sh, dataPath is not null
            ? $"#!/usr/bin/env bash\nset -euo pipefail\ncd \"$(dirname \"${{BASH_SOURCE[0]}}\")\"\nexec ./run.sh --dataPath {ShellQuote(dataPath)} \"$@\"\n"
            : "#!/usr/bin/env bash\nset -euo pipefail\ncd \"$(dirname \"${BASH_SOURCE[0]}\")\"\nexec ./run.sh \"$@\"\n");
        MakeExecutable(sh);
        if (dataPath is not null)
            File.WriteAllText(Path.Combine(stageDir, "datapath.cfg"), dataPath);
        _ = finalInstallDir;
        return sh;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

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

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
