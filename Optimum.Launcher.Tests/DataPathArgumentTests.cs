using System;
using System.IO;
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

    [Fact]
    public void DefaultDataPathUsesInstallDirectoryForDevelopmentBuilds()
    {
        string gameDir = Path.Combine(Path.GetTempPath(), "optimum-data-path-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(gameDir, Program.ResolveDefaultDataPath(gameDir));
        }
        finally
        {
            if (Directory.Exists(gameDir)) Directory.Delete(gameDir, recursive: true);
        }
    }

    [Fact]
    public void DefaultDataPathMatchesPackagedGamePath()
    {
        string gameDir = Path.Combine(Path.GetTempPath(), "optimum-data-path-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(gameDir, "assets"));
        try
        {
            string applicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            string expected = string.IsNullOrEmpty(applicationData)
                ? gameDir
                : Path.Combine(applicationData, "VintagestoryData");
            Assert.Equal(expected, Program.ResolveDefaultDataPath(gameDir));
        }
        finally
        {
            if (Directory.Exists(gameDir)) Directory.Delete(gameDir, recursive: true);
        }
    }
}
