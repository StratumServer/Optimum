using Optimum.Launcher;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class DataPathArgumentTests
{
    [Theory]
    [InlineData("--dataPath", "/managed")]
    [InlineData("-d", "/managed")]
    public void SpaceSeparatedValueIsExtracted(string flag, string value)
    {
        Assert.Equal(value, Program.ResolveDataPath([flag, value]));
    }

    [Theory]
    [InlineData("--dataPath=/managed")]
    [InlineData("-d=/managed")]
    public void EqualsSeparatedValueIsExtracted(string arg)
    {
        Assert.Equal("/managed", Program.ResolveDataPath([arg]));
    }

    [Fact]
    public void MissingFlagReturnsNull()
    {
        Assert.Null(Program.ResolveDataPath(["--other", "x"]));
    }

    [Fact]
    public void FlagWithoutValueReturnsNull()
    {
        Assert.Null(Program.ResolveDataPath(["--dataPath"]));
    }

    [Fact]
    public void FirstMatchWins()
    {
        Assert.Equal("/first", Program.ResolveDataPath(["--dataPath=/first", "--dataPath", "/second"]));
    }
}
