using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Paths;

/// <summary>
/// Ports RiftLauncher's <c>assertNoSymlinkComponents</c>: walk every existing
/// component of a path up to the root and reject the path if any component is a
/// symbolic link. A symlink anywhere in an install or data path is a way for a
/// later step to write outside the directory the user chose.
/// </summary>
public static class SymlinkComponentCheck
{
    /// <summary>
    /// Returns the first path component that is a symbolic link, or null when the
    /// path is clean. Components that do not exist yet are skipped unless
    /// <paramref name="requireExists"/> is set.
    /// </summary>
    public static string? FirstSymlinkComponent(ISystemProbe probe, string path, bool requireExists = false)
    {
        string full = Path.GetFullPath(path);
        string? current = full;

        while (!string.IsNullOrEmpty(current))
        {
            if (probe.PathExists(current))
            {
                if (probe.IsSymbolicLink(current))
                    return current;
            }
            else if (requireExists)
            {
                throw new DirectoryNotFoundException($"Path component does not exist: {current}");
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current)
                break;
            current = parent;
        }

        return null;
    }

    public static bool IsClean(ISystemProbe probe, string path) => FirstSymlinkComponent(probe, path) is null;
}
