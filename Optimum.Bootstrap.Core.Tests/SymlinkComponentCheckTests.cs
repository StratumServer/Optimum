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
}
