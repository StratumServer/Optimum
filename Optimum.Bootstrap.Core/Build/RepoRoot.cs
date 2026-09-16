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
    public static string? Discover(ISystemProbe probe, string? explicitRoot = null)
    {
        string start = explicitRoot is not null
            ? Path.GetFullPath(explicitRoot)
            : Directory.GetCurrentDirectory();

        for (string? dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (SourceCache.IsUsableCheckout(probe, dir))
                return dir;
        }

        return null;
    }
}
