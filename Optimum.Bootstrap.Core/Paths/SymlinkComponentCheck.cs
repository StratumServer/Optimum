using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Paths;

/// <summary>
/// Ports RiftLauncher's <c>assertNoSymlinkComponents</c>: walk every existing
/// component of a path up to the root and return the first that is a symbolic
/// link. Use this for a path that is expected to stay within a trusted base
/// directory, where a symlinked component is an escape vector. The install and
/// data path guards do not use it: an arbitrary user-chosen directory legitimately
/// sits under a symlinked home or mount point, so <see cref="InstallPathGuard"/>
/// only rejects a symlinked leaf.
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
