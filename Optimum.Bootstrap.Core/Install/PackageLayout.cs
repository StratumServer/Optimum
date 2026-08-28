using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Install;

public sealed record PackageLayoutResult(bool Ok, IReadOnlyList<string> Problems)
{
    public static readonly PackageLayoutResult Good = new(true, []);
}

/// <summary>
/// A shallow check that a directory is a staged Optimum package rather than an
/// arbitrary folder: it must carry a launcher entry point and the <c>.optimum</c>
/// marker directory the packaging scripts write.
/// </summary>
public static class PackageLayout
{
    public static PackageLayoutResult Validate(ISystemProbe probe, string packageDirectory)
    {
        var problems = new List<string>();

        if (!probe.DirectoryExists(packageDirectory))
        {
            problems.Add($"the package directory does not exist: {packageDirectory}");
            return new PackageLayoutResult(false, problems);
        }

        bool hasLauncher =
            probe.FileExists(Path.Combine(packageDirectory, "run.sh"))
            || probe.FileExists(Path.Combine(packageDirectory, "Optimum"))
            || probe.FileExists(Path.Combine(packageDirectory, "Optimum.exe"));
        if (!hasLauncher)
            problems.Add("no launcher entry point (run.sh, Optimum, or Optimum.exe)");

        if (!probe.DirectoryExists(Path.Combine(packageDirectory, ".optimum")))
            problems.Add("no .optimum marker directory");

        return problems.Count == 0 ? PackageLayoutResult.Good : new PackageLayoutResult(false, problems);
    }
}
