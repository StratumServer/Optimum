using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Paths;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

/// <summary>
/// Every case INSTALLER-PLAN.md section 9 lists for the path guard, plus the
/// overlap and data-path rules from <c>Assert-SafeInstallerPaths</c>.
/// </summary>
public class InstallPathGuardTests
{
    private static FakeSystemProbe Linux()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Linux, HomeDirectory = "/home/tester" };
        return probe;
    }

    private static void AssertRejected(InstallPathVerdict verdict, string fragment)
    {
        Assert.False(verdict.Ok);
        Assert.Contains(fragment, verdict.Rejection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsTheFilesystemRoot() =>
        AssertRejected(InstallPathGuard.Check(Linux(), new InstallPathRequest("/")), "root");

    [Fact]
    public void RejectsTheHomeDirectory() =>
        AssertRejected(InstallPathGuard.Check(Linux(), new InstallPathRequest("/home/tester")), "home");

    [Fact]
    public void RejectsTheXdgDataHome()
    {
        FakeSystemProbe probe = Linux();
        probe.Environment["XDG_DATA_HOME"] = "/home/tester/.local/share";
        AssertRejected(InstallPathGuard.Check(probe, new InstallPathRequest("/home/tester/.local/share")), ".local/share");
    }

    [Fact]
    public void RejectsDotLocal() =>
        AssertRejected(InstallPathGuard.Check(Linux(), new InstallPathRequest("/home/tester/.local")), ".local");

    [Fact]
    public void RejectsAWindowsDriveRoot()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows, HomeDirectory = @"C:\Users\tester" };
        AssertRejected(InstallPathGuard.Check(probe, new InstallPathRequest(@"C:\")), "root");
    }

    [Fact]
    public void RejectsAPathInsideAVintageStoryInstall() =>
        AssertRejected(
            InstallPathGuard.Check(Linux(), new InstallPathRequest("/home/tester/.local/share/vintagestory/mods")),
            "Vintage Story");

    [Fact]
    public void RejectsADirectoryHoldingAVanillaGameWithNoOptimumMarker()
    {
        FakeSystemProbe probe = Linux();
        probe.AddFile("/opt/games/vs/Vintagestory");
        AssertRejected(InstallPathGuard.Check(probe, new InstallPathRequest("/opt/games/vs")), "vanilla Vintage Story");
    }

    [Fact]
    public void RejectsAnInstallDirectoryThatIsItselfASymlink()
    {
        FakeSystemProbe probe = Linux();
        probe.AddSymlink("/home/tester/games/optimum");
        AssertRejected(InstallPathGuard.Check(probe, new InstallPathRequest("/home/tester/games/optimum")), "symbolic link");
    }

    [Fact]
    public void AllowsAnInstallDirectoryUnderASymlinkedParent()
    {
        FakeSystemProbe probe = Linux();
        probe.AddSymlink("/home/tester/Games");   // a second drive mounted here
        Assert.True(InstallPathGuard.Check(probe, new InstallPathRequest("/home/tester/Games/optimum")).Ok);
    }

    [Fact]
    public void AllowsACleanSeparateDirectory()
    {
        InstallPathVerdict verdict = InstallPathGuard.Check(Linux(),
            new InstallPathRequest("/home/tester/games/optimum"));
        Assert.True(verdict.Ok);
        Assert.Null(verdict.Rejection);
    }

    [Fact]
    public void AllowsADirectoryHoldingAnExistingOptimumInstall()
    {
        FakeSystemProbe probe = Linux();
        probe.AddFile("/home/tester/games/optimum/Vintagestory");
        probe.AddFile("/home/tester/games/optimum/Optimum");
        Assert.True(InstallPathGuard.Check(probe, new InstallPathRequest("/home/tester/games/optimum")).Ok);
    }

    [Fact]
    public void RejectsAnInstallDirectoryThatOverlapsTheVintageStoryDirectory() =>
        AssertRejected(
            InstallPathGuard.Check(Linux(), new InstallPathRequest(
                "/home/tester/opt", VintageStoryDirectory: "/home/tester/opt/vs")),
            "overlap");

    [Fact]
    public void RejectsADataPathInsideTheInstallDirectory() =>
        AssertRejected(
            InstallPathGuard.Check(Linux(), new InstallPathRequest(
                "/home/tester/opt", DataPath: "/home/tester/opt/data")),
            "data path");
}
