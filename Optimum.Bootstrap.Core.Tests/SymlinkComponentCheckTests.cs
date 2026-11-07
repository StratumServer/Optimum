using Optimum.Bootstrap.Core.Paths;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public class SymlinkComponentCheckTests
{
    [Fact]
    public void CleanPathHasNoSymlinkComponent()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/home/tester/games");
        Assert.Null(SymlinkComponentCheck.FirstSymlinkComponent(probe, "/home/tester/games/optimum"));
    }

    [Fact]
    public void ReturnsTheSymlinkedComponentWhenOneIsInThePath()
    {
        var probe = new FakeSystemProbe();
        probe.AddSymlink("/home/tester/games");

        Assert.Equal("/home/tester/games",
            SymlinkComponentCheck.FirstSymlinkComponent(probe, "/home/tester/games/optimum/bin"));
    }

    [Fact]
    public void RequireExistsThrowsWhenAComponentIsMissing()
    {
        var probe = new FakeSystemProbe();
        Assert.Throws<DirectoryNotFoundException>(() =>
            SymlinkComponentCheck.FirstSymlinkComponent(probe, "/nowhere/at/all", requireExists: true));
    }

    [Fact]
    public void FirstSymlinkUnderRoot_IgnoresSymlinkAboveRoot()
    {
        var probe = new FakeSystemProbe();
        probe.AddSymlink("/home/tester/mount");
        probe.AddDirectory("/home/tester/mount/vintagestory");
        probe.AddDirectory("/home/tester/mount/vintagestory/assets");

        // Symlink is above root, so FirstSymlinkUnderRoot should be null
        string root = "/home/tester/mount/vintagestory";
        string path = "/home/tester/mount/vintagestory/assets/game/shaders/test.vsh";
        Assert.Null(SymlinkComponentCheck.FirstSymlinkUnderRoot(probe, path, root));
        Assert.True(SymlinkComponentCheck.IsContainedWithinRoot(probe, path, root, out _));
    }

    [Fact]
    public void FirstSymlinkUnderRoot_DetectsSymlinkedSubdirectoryUnderRoot()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/home/tester/vintagestory");
        probe.AddSymlink("/home/tester/vintagestory/assets");

        string root = "/home/tester/vintagestory";
        string path = "/home/tester/vintagestory/assets/game/shaders/test.vsh";
        Assert.Equal("/home/tester/vintagestory/assets",
            SymlinkComponentCheck.FirstSymlinkUnderRoot(probe, path, root));
        Assert.False(SymlinkComponentCheck.IsContainedWithinRoot(probe, path, root, out string? escaping));
        Assert.Equal("/home/tester/vintagestory/assets", escaping);
    }

    [Fact]
    public void IsContainedWithinRoot_RefusesPathTraversalOutsideRoot()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/home/tester/vintagestory");

        string root = "/home/tester/vintagestory";
        string path = "/home/tester/vintagestory/assets/../../etc/passwd";
        Assert.False(SymlinkComponentCheck.IsContainedWithinRoot(probe, path, root, out string? escaping));
        Assert.NotNull(escaping);

        // Also verify on macOS probe
        var macProbe = new FakeSystemProbe { Os = Optimum.Bootstrap.Core.Platform.OsKind.MacOs };
        macProbe.AddDirectory("/Users/tester/vintagestory");
        Assert.False(SymlinkComponentCheck.IsContainedWithinRoot(macProbe, "/Users/tester/vintagestory/assets/../../etc/passwd", "/Users/tester/vintagestory", out _));

        // And Windows probe
        var winProbe = new FakeSystemProbe { Os = Optimum.Bootstrap.Core.Platform.OsKind.Windows };
        winProbe.AddDirectory(@"C:\Games\vintagestory");
        Assert.False(SymlinkComponentCheck.IsContainedWithinRoot(winProbe, @"C:\Games\vintagestory\assets\..\..\Windows\System32", @"C:\Games\vintagestory", out _));
    }
}
