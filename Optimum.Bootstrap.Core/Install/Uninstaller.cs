using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Install;

public sealed record UninstallResult(bool Ok, FailureReason? Reason, string? Message, int RemovedEntries)
{
    public static UninstallResult Failure(FailureReason reason, string message) => new(false, reason, message, 0);

    public static UninstallResult Success(int removed) => new(true, null, null, removed);
}

/// <summary>
/// Removes an install by its <see cref="InstallManifest"/>. It refuses a
/// directory with no manifest, and it removes only manifest entries that resolve
/// inside the install directory, so a tampered manifest cannot make it delete
/// something elsewhere.
/// </summary>
public sealed class Uninstaller(ISystemProbe probe)
{
    public UninstallResult Uninstall(string installDirectory)
    {
        string installDir = Path.GetFullPath(installDirectory);
        string manifestPath = Path.Combine(installDir, InstallManifest.RelativePath);

        string? json = probe.ReadText(manifestPath);
        if (json is null)
            return UninstallResult.Failure(FailureReason.BadInput,
                $"no Optimum install manifest at {manifestPath}");

        InstallManifest? manifest = InstallManifest.Deserialize(json);
        if (manifest is null)
            return UninstallResult.Failure(FailureReason.BadInput, $"the install manifest is unreadable: {manifestPath}");

        string prefix = installDir + Path.DirectorySeparatorChar;
        int removed = 0;
        var skipped = new List<string>();
        foreach (string entry in manifest.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(installDir, entry));
            if (target != installDir && !target.StartsWith(prefix, StringComparison.Ordinal))
            {
                // A manifest entry that resolves outside the install directory
                // (a rooted path, a `..` walk) is never removed. The deployer
                // only ever writes leaf names, so this guards against a tampered
                // or malformed manifest.
                skipped.Add(entry);
                continue;
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
                removed++;
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
                removed++;
            }
        }

        if (skipped.Count > 0)
        {
            return new UninstallResult(false, FailureReason.BadInput,
                "the manifest names entries outside the install directory: " + string.Join(", ", skipped), removed);
        }

        string optimumDir = Path.Combine(installDir, ".optimum");
        if (Directory.Exists(optimumDir))
        {
            Directory.Delete(optimumDir, recursive: true);
            removed++;
        }

        if (Directory.Exists(installDir) && !Directory.EnumerateFileSystemEntries(installDir).Any())
            Directory.Delete(installDir);

        return UninstallResult.Success(removed);
    }
}
