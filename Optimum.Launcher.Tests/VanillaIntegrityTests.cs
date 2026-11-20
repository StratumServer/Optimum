using System;
using System.IO;
using System.Security.Cryptography;
using Optimum.Launcher;
using Xunit;

namespace Optimum.Launcher.Tests;

public sealed class VanillaIntegrityTests : IDisposable
{
    private static readonly byte[] Api = [1, 2, 3];
    private static readonly byte[] Lib = [4, 5, 6];

    private readonly string _gameDir = Path.Combine(Path.GetTempPath(), "optimum-integrity-" + Guid.NewGuid().ToString("N"));

    public VanillaIntegrityTests() => Directory.CreateDirectory(_gameDir);

    public void Dispose() => Directory.Delete(_gameDir, recursive: true);

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Forks(byte[] api, byte[] lib) =>
        "{\"vintageStoryVersion\":\"1.22.7\",\"vintageStoryPins\":{\"1.22.7\":{\"archives\":{},\"extractedFiles\":{" +
        "\"VintagestoryAPI.dll\":\"" + Sha(api) + "\",\"VintagestoryLib.vanilla.dll\":\"" + Sha(lib) + "\"}}}}";

    [Fact]
    public void TheOfficialFilesPass()
    {
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryAPI.dll"), Api);
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryLib.dll"), Lib);

        Assert.Null(VanillaIntegrity.FindProblem(_gameDir, Forks(Api, Lib)));
    }

    [Fact]
    public void AnApiDllThatIsNotTheOfficialBuildIsReportedByName()
    {
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryAPI.dll"), [9, 9, 9]);
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryLib.dll"), Lib);

        string? problem = VanillaIntegrity.FindProblem(_gameDir, Forks(Api, Lib));

        Assert.NotNull(problem);
        Assert.Contains("VintagestoryAPI.dll", problem);
        Assert.Contains("1.22.7", problem);
        Assert.Contains("installer", problem);
    }

    [Fact]
    public void ALibraryThatIsNotTheOfficialBuildIsReportedByName()
    {
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryAPI.dll"), Api);
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryLib.dll"), [9, 9, 9]);

        Assert.Contains("VintagestoryLib.dll", VanillaIntegrity.FindProblem(_gameDir, Forks(Api, Lib)));
    }

    [Fact]
    public void AMissingFileIsLeftToThePatchLoop()
    {
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryAPI.dll"), Api);

        Assert.Null(VanillaIntegrity.FindProblem(_gameDir, Forks(Api, Lib)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"vintageStoryVersion\":\"1.22.7\"}")]
    [InlineData("{\"vintageStoryVersion\":\"1.22.7\",\"vintageStoryPins\":{\"1.99.0\":{\"extractedFiles\":{}}}}")]
    [InlineData("not json")]
    public void WithoutPinsForTheVersionNothingIsRejected(string forks)
    {
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryAPI.dll"), [9, 9, 9]);

        Assert.Null(VanillaIntegrity.FindProblem(_gameDir, forks));
    }

    private void WriteVersionMarker(string version)
    {
        Directory.CreateDirectory(Path.Combine(_gameDir, "assets"));
        File.WriteAllText(Path.Combine(_gameDir, "assets", "version-" + version + ".txt"), "");
    }

    [Fact]
    public void AGameOfAVersionWithoutPinsIsNotRefused()
    {
        // A package built against another version has its own game files and no pins to compare them with.
        WriteVersionMarker("1.22.6");
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryAPI.dll"), [9, 9, 9]);
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryLib.dll"), [9, 9, 9]);

        Assert.Null(VanillaIntegrity.FindProblem(_gameDir, Forks(Api, Lib)));
    }

    [Fact]
    public void TheVersionOfTheGameSelectsThePinsToCheck()
    {
        WriteVersionMarker("1.22.7");
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryAPI.dll"), [9, 9, 9]);

        string? problem = VanillaIntegrity.FindProblem(_gameDir, Forks(Api, Lib));

        Assert.Contains("1.22.7", problem);
    }

    [Fact]
    public void TheSkipVariableTurnsTheCheckOffAndTheMessageNamesIt()
    {
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryAPI.dll"), [9, 9, 9]);
        string? problem = VanillaIntegrity.FindProblem(_gameDir);
        Assert.Contains(VanillaIntegrity.SkipVariable, problem);

        string? previous = Environment.GetEnvironmentVariable(VanillaIntegrity.SkipVariable);
        try
        {
            Environment.SetEnvironmentVariable(VanillaIntegrity.SkipVariable, "1");
            Assert.Null(VanillaIntegrity.FindProblem(_gameDir));
        }
        finally
        {
            Environment.SetEnvironmentVariable(VanillaIntegrity.SkipVariable, previous);
        }
    }

    [Fact]
    public void AMalformedPinBlockDoesNotCrashTheLauncher()
    {
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryAPI.dll"), [9, 9, 9]);

        Assert.Null(VanillaIntegrity.FindProblem(
            _gameDir, "{\"vintageStoryVersion\":\"1.22.7\",\"vintageStoryPins\":{\"1.22.7\":{\"archives\":{\"a\":{\"bytes\":1}}}}}"));
    }

    [Fact]
    public void ThePinsTravelInsideTheLauncherAndAreTheRepositorysOwn()
    {
        File.WriteAllBytes(Path.Combine(_gameDir, "VintagestoryAPI.dll"), [9, 9, 9]);

        Assert.Contains("is not the official Vintage Story", VanillaIntegrity.FindProblem(_gameDir));
    }
}
