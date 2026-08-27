using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Install;

public sealed record UninstallResult(bool Ok, FailureReason? Reason, string? Message, int RemovedEntries)
{
    public static UninstallResult Failure(FailureReason reason, string message) => new(false, reason, message, 0);

    public static UninstallResult Success(int removed) => new(true, null, null, removed);
}

/// <summary>
/// Removes an install by its <see cref="InstallManifest"/>. It refuses a
/// directory with no manifest, removes only manifest entries that resolve inside
/// the install directory, and always attempts the shortcut, registry, and
/// <c>.optimum</c> cleanup even when an individual file will not delete, so a
/// locked file cannot strand the "Apps and features" entry or a menu shortcut.
/// </summary>
public sealed class Uninstaller(ISystemProbe probe)
{
    public UninstallResult Uninstall(string installDirectory)
    {
        string installDir = Path.GetFullPath(installDirectory);
        string manifestPath = Path.Combine(installDir, InstallManifest.RelativePath);

        string? json = probe.ReadText(manifestPath);
        if (json is null)
            return UninstallResult.Failure(FailureReason.BadInput, $"no Optimum install manifest at {manifestPath}");

        if (InstallManifest.Deserialize(json) is not { } manifest)
            return UninstallResult.Failure(FailureReason.BadInput, $"the install manifest is unreadable: {manifestPath}");

        string prefix = installDir + Path.DirectorySeparatorChar;

        string[] escaping = manifest.Entries
            .Where(e =>
            {
                string t = Path.GetFullPath(Path.Combine(installDir, e));
                return t != installDir && !t.StartsWith(prefix, StringComparison.Ordinal);
            })
            .ToArray();
        if (escaping.Length > 0)
            return UninstallResult.Failure(FailureReason.BadInput,
                "the manifest names entries outside the install directory: " + string.Join(", ", escaping));

        int removed = 0;
        var problems = new List<string>();

        foreach (string entry in manifest.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(installDir, entry));
            if (TryRemove(target))
                removed++;
            else if (Directory.Exists(target) || File.Exists(target))
                problems.Add($"could not remove {entry}");
        }

        // The rest runs regardless of a locked entry above.
        if (manifest.Shortcuts.Count > 0)
        {
            new ShortcutWriter(probe).Remove(manifest.Shortcuts);
            removed += manifest.Shortcuts.Count;
        }

        UninstallRegistration.Unregister(manifest.UninstallRegistryKey);

        if (TryRemove(Path.Combine(installDir, ".optimum")))
            removed++;
        else if (Directory.Exists(Path.Combine(installDir, ".optimum")))
            problems.Add("could not remove .optimum");

        if (Directory.Exists(installDir) && !Directory.EnumerateFileSystemEntries(installDir).Any())
        {
            try { Directory.Delete(installDir); }
            catch (IOException) { /* a non-empty leftover is reported below */ }
        }

        return problems.Count == 0
            ? UninstallResult.Success(removed)
            : new UninstallResult(false, FailureReason.EngineInternal,
                "uninstall removed the shortcuts and registry entry but could not remove everything: "
                + string.Join("; ", problems), removed);
    }

    private static bool TryRemove(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        return false;
    }
}
