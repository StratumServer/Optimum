using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Install;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public class CapabilitiesTests
{
    [Fact]
    public void ReportsThePinnedVersionBridgeVersionsAndPatchSets()
    {
        var probe = new FakeSystemProbe();
        probe.AddFile("/repo/forks.json", """{ "vintageStoryVersion": "1.22.7" }""");
        probe.AddDirectory("/repo/patches-1.22.6-bridge");
        probe.AddDirectory("/repo/patches/vsapi");
        probe.AddDirectory("/repo/patches/runtime");

        EngineCapabilities caps = Capabilities.Read(probe, "/repo");

        Assert.Equal("1.22.7", caps.PinnedVersion);
        Assert.Equal(["1.22.7", "1.22.6"], caps.SupportedVersions);
        Assert.Equal(["runtime", "vsapi"], caps.PatchSets);
    }

    [Fact]
    public void FallsBackWhenForksJsonIsMissing()
    {
        Assert.Equal("1.22.7", Capabilities.Read(new FakeSystemProbe(), "/repo").PinnedVersion);
    }
}

public class PackageLayoutTests
{
    [Fact]
    public void AcceptsADirectoryWithALauncherAndTheOptimumMarker()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/pkg");
        probe.AddFile("/pkg/run.sh");
        probe.AddDirectory("/pkg/.optimum");

        Assert.True(PackageLayout.Validate(probe, "/pkg").Ok);
    }

    [Fact]
    public void FlagsAMissingMarkerDirectory()
    {
        var probe = new FakeSystemProbe();
        probe.AddDirectory("/pkg");
        probe.AddFile("/pkg/Optimum");

        PackageLayoutResult result = PackageLayout.Validate(probe, "/pkg");
        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains(".optimum"));
    }

    [Fact]
    public void FlagsAMissingDirectory()
    {
        Assert.False(PackageLayout.Validate(new FakeSystemProbe(), "/nowhere").Ok);
    }
}

public class InstallManifestTests
{
    [Fact]
    public void RoundTrips()
    {
        var manifest = new InstallManifest
        {
            OptimumVersion = "0.3.14",
            InstalledAtUtc = DateTimeOffset.Parse("2026-08-27T12:00:00Z"),
            InstallDirectory = "/home/tester/games/optimum",
            DataPath = "/home/tester/.config/VintagestoryData",
            Launcher = "/home/tester/games/optimum/optimum-launch.sh",
            Entries = ["run.sh", "assets", ".optimum"],
        };

        InstallManifest? back = InstallManifest.Deserialize(manifest.Serialize());

        Assert.NotNull(back);
        Assert.Equal(manifest.OptimumVersion, back!.OptimumVersion);
        Assert.Equal(manifest.Entries, back.Entries);
        Assert.Equal(manifest.DataPath, back.DataPath);
    }

    [Fact]
    public void DeserializeReturnsNullOnGarbage()
    {
        Assert.Null(InstallManifest.Deserialize("{ not json"));
    }
}

public class BootstrapFailureClassifierTests
{
    [Theory]
    [InlineData("error: patch failed: build/Vintagestory/foo.cs:12")]
    [InlineData("Checking patch ...\nhunk #3 FAILED at 210")]
    [InlineData("Saved rejects in patches/vsapi/0007-x.patch.rej")]
    [InlineData("error: patches/vssurvivalmod/0002-thing.patch: No such file")]
    public void PatchDiagnosticsClassifyAsPatchConflict(string output)
    {
        Assert.Equal(FailureReason.PatchConflict, BootstrapFailureClassifier.Classify(output));
    }

    [Theory]
    [InlineData("curl: (22) The requested URL returned error: 404")]
    [InlineData("ilspycmd: could not decompile VintagestoryLib.dll")]
    [InlineData("")]
    public void EverythingElseClassifiesAsDecompileFailed(string output)
    {
        Assert.Equal(FailureReason.DecompileFailed, BootstrapFailureClassifier.Classify(output));
    }
}

public class ScriptBuildDriverPreconditionTests
{
    [Fact]
    public async Task RefusesWithBadInputWhenRequiredToolsAreMissing()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";
        probe.AddFile("/repo/forks.json", """{ "vintageStoryVersion": "1.22.7" }""");

        BuildResult result = await new ScriptBuildDriver(probe).RunAsync(
            new BuildRequest("/repo", "/tmp/does-not-run"), NullBuildObserver.Instance, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.Contains(".NET SDK", result.Message);
    }
}
