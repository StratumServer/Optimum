using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Install;

public sealed record UninstallResult(bool Ok, FailureReason? Reason, string? Message, int RemovedEntries)
{
    public static UninstallResult Failure(FailureReason reason, string message) => new(false, reason, message, 0);

    public static UninstallResult Success(int removed) => new(true, null, null, removed);
}

/// <summary>
/// Removes an install by its <see cref="InstallManifest"/>. It never touches a
/// directory that has no manifest, so it cannot delete a directory it did not
/// create.
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

        int removed = 0;
        foreach (string entry in manifest.Entries)
        {
            string target = Path.Combine(installDir, entry);
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
