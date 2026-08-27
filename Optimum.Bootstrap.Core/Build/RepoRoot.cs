using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Build;

/// <summary>
/// Finds the Optimum checkout the engine has to drive: the nearest directory at
/// or above a starting point that holds <c>forks.json</c> next to
/// <c>scripts/bootstrap.sh</c>. Both front ends need this because the build
/// pipeline is still the shell scripts (INSTALLER-PLAN.md section 2).
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
            if (probe.FileExists(Path.Combine(dir, "forks.json"))
                && probe.FileExists(Path.Combine(dir, "scripts", "bootstrap.sh")))
                return dir;
        }

        return null;
    }
}
