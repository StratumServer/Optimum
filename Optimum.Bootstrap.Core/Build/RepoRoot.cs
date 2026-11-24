using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Build;

/// <summary>
/// Finds the Optimum checkout the engine has to drive: the nearest directory at
/// or above a starting point that holds the manifest and the scripts required
/// by the probed platform. Both front ends need this because the build pipeline
/// is still the platform scripts (INSTALLER-PLAN.md section 2).
/// </summary>
public static class RepoRoot
{
    public static string? Discover(ISystemProbe probe, string? explicitRoot = null) =>
        WalkUp(probe, explicitRoot ?? Directory.GetCurrentDirectory());

    /// <summary>
    /// Discovers an Optimum checkout at or above <paramref name="workingDirectory"/>,
    /// falling back to the version-matched user source cache when none is found.
    /// </summary>
    public static string? DiscoverOrCached(
        ISystemProbe probe, string? version = null, string? workingDirectory = null, string? cacheRoot = null) =>
        WalkUp(probe, workingDirectory ?? Directory.GetCurrentDirectory())
        ?? FindCached(probe, version, cacheRoot);

    /// <summary>
    /// Returns the version-matched cached Optimum checkout if it exists and is usable.
    /// </summary>
    public static string? FindCached(ISystemProbe probe, string? version = null, string? cacheRoot = null)
    {
        string cached = SourceCache.Directory(probe, version ?? CoreInfo.Version, cacheRoot);
        return SourceCache.IsUsableCheckout(probe, cached) ? cached : null;
    }

    private static string? WalkUp(ISystemProbe probe, string start)
    {
        // An explicit root is already an absolute path in the probe's world, so
        // walk it as given. Only the implicit case needs the real working
        // directory. Resolving an explicit path through Path.GetFullPath would
        // rewrite a POSIX path against the host drive on Windows, which is wrong
        // whenever the probe models a different platform (and in tests).
        for (string? dir = start; dir is not null; dir = ParentOf(dir))
        {
            if (SourceCache.IsUsableCheckout(probe, dir))
                return dir;
        }

        return null;
    }

    /// <summary>
    /// The parent of a path, honouring both separators so a POSIX path resolves
    /// the same way regardless of the host the binary runs on. Returns null at
    /// the root so the walk terminates.
    /// </summary>
    private static string? ParentOf(string dir)
    {
        int cut = dir.TrimEnd('/', '\\').LastIndexOfAny(['/', '\\']);
        if (cut < 0)
            return null;
        return cut == 0 ? dir[..1] : dir[..cut];
    }
}
