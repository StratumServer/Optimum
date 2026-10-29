using Optimum.Bootstrap.Core.Platform;
using System.Runtime.InteropServices;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public class MacOsPathTests
{
    [Fact]
    public void AddsExistingMacInstallerAndPackageManagerDirectoriesWithoutDuplicates()
    {
        string[] existing =
        [
            "/usr/bin",
            "/usr/local/bin",
            "/usr/local/share/dotnet",
            "/Users/tester/.dotnet/tools",
            "/Library/Developer/CommandLineTools/usr/bin",
        ];

        IReadOnlyList<string> path = MacOsPath.Build(
            "/usr/bin:/usr/local/bin", "/Users/tester", OsKind.MacOs, Architecture.X64,
            directory => existing.Contains(directory, StringComparer.Ordinal));

        Assert.Equal(
            ["/usr/bin", "/usr/local/bin", "/usr/local/share/dotnet", "/Users/tester/.dotnet/tools", "/Library/Developer/CommandLineTools/usr/bin"],
            path);
    }

    [Fact]
    public void LeavesNonMacOsPathUnchanged()
    {
        string inheritedPath = string.Join(Path.PathSeparator, "/usr/bin", "/opt/tools");
        IReadOnlyList<string> path = MacOsPath.Build(
            inheritedPath, "/Users/tester", OsKind.Linux, Architecture.X64,
            _ => true);

        Assert.Equal(["/usr/bin", "/opt/tools"], path);
    }

    [Fact]
    public void AppleSiliconPrefersNativeHomebrewPrefix()
    {
        IReadOnlyList<string> path = MacOsPath.Build(
            "/usr/bin", "/Users/tester", OsKind.MacOs, Architecture.Arm64, _ => true);

        Assert.Equal("/opt/homebrew/bin", path[1]);
        Assert.Equal("/opt/homebrew/sbin", path[2]);
    }
}
