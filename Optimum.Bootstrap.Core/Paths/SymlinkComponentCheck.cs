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
        // Walk the components as given rather than through Path.GetFullPath /
        // Path.GetDirectoryName, which on Windows rewrite a POSIX path against the
        // host drive and split on '\'. The probe already holds absolute paths; in
        // production probe.Os matches the host so behaviour is unchanged.
        string? current = path;

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

            string? parent = ParentOf(current);
            if (parent is null || parent == current)
                break;
            current = parent;
        }

        return null;
    }

    /// <summary>Parent of a path, honouring both separators; null at the root.</summary>
    private static string? ParentOf(string dir)
    {
        int cut = dir.TrimEnd('/', '\\').LastIndexOfAny(['/', '\\']);
        if (cut < 0)
            return null;
        return cut == 0 ? dir[..1] : dir[..cut];
    }

    public static bool IsClean(ISystemProbe probe, string path) => FirstSymlinkComponent(probe, path) is null;

    /// <summary>
    /// Returns the first path component within <paramref name="rootDir"/> (walking upwards from
    /// <paramref name="path"/> up to <paramref name="rootDir"/>) that is a symbolic link.
    /// Ancestor directories above <paramref name="rootDir"/> are not flagged.
    /// </summary>
    public static string? FirstSymlinkUnderRoot(ISystemProbe probe, string path, string rootDir)
    {
        string canonicalRoot = InstallPathGuard.Canonical(probe, rootDir);
        string? current = InstallPathGuard.Canonical(probe, path);

        while (!string.IsNullOrEmpty(current) && !InstallPathGuard.PathEquals(probe, current, canonicalRoot))
        {
            if (probe.PathExists(current) && probe.IsSymbolicLink(current))
                return current;

            string? parent = ParentOf(current);
            if (parent is null || parent == current)
                break;
            current = parent;
        }

        return null;
    }

    /// <summary>
    /// Verifies whether writing to <paramref name="destinationPath"/> remains strictly contained
    /// within <paramref name="gameRootDir"/> without following escaping symbolic links.
    /// </summary>
    public static bool IsContainedWithinRoot(ISystemProbe probe, string destinationPath, string gameRootDir, out string? escapingComponent)
    {
        escapingComponent = null;
        string canonicalRoot = InstallPathGuard.Canonical(probe, gameRootDir);
        string canonicalDest = InstallPathGuard.Canonical(probe, destinationPath);

        if (!InstallPathGuard.IsWithinOrEqual(probe, canonicalDest, canonicalRoot))
        {
            escapingComponent = destinationPath;
            return false;
        }

        string? symlink = FirstSymlinkUnderRoot(probe, destinationPath, gameRootDir);
        if (symlink is not null)
        {
            escapingComponent = symlink;
            return false;
        }

        return true;
    }
}
