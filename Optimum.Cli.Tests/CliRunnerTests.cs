using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Tests;
using Optimum.Cli;
using Xunit;

namespace Optimum.Cli.Tests;

public class CliRunnerTests
{
    private static async Task<(int Code, string Stdout, string Stderr)> Run(
        string[] args, ISystemProbe? probe = null, IBuildDriver? driver = null, CancellationToken cancel = default,
        ISourceProvider? sourceProvider = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliRunner.RunAsync(
            args, stdout, stderr,
            probe ?? new FakeSystemProbe(),
            driver ?? new FakeBuildDriver(),
            cancel,
            sourceProvider);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private sealed class StubSourceProvider(SourceAcquisitionResult result) : ISourceProvider
    {
        public int Calls { get; private set; }

        public Task<SourceAcquisitionResult> EnsureAsync(
            SourceRequest request, IBuildObserver observer, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingSourceProvider : ISourceProvider
    {
        public Task<SourceAcquisitionResult> EnsureAsync(
            SourceRequest request, IBuildObserver observer, CancellationToken cancellationToken) =>
            throw new IOException("clone process could not start");
    }

    [Fact]
    public async Task VersionPrintsOnePlainLine()
    {
        var (code, stdout, stderr) = await Run(["--version"]);
        Assert.Equal(CliRunner.ExitOk, code);
        Assert.Equal(CoreInfo.Version, stdout.Trim());
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public async Task UnknownVerbExitsWithUsage()
    {
        var (code, stdout, stderr) = await Run(["frobnicate"]);
        Assert.Equal(CliRunner.ExitUsage, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("unknown verb", stderr);
    }

    [Fact]
    public async Task BuildWithoutTheAcknowledgeFlagDoesNoWork()
    {
        var driver = new FakeBuildDriver();
        var (code, _, stderr) = await Run(["build", "--output", "/tmp/out"], driver: driver);

        Assert.Equal(CliRunner.ExitUsage, code);
        Assert.False(driver.WasRun);
        Assert.Contains("acknowledge-decompile", stderr);
    }

    [Fact]
    public async Task BuildRejectsARelativeOutputPath()
    {
        var (code, _, stderr) = await Run(["build", "--acknowledge-decompile", "--output", "relative/out"]);
        Assert.Equal(CliRunner.ExitUsage, code);
        Assert.Contains("absolute", stderr);
    }

    [Fact]
    public async Task BuildRejectsAMissingClientArchive()
    {
        var probe = RepoProbe();
        var (code, _, stderr) = await Run(
            ["build", "--acknowledge-decompile", "--output", "/tmp/out", "--client-archive", "/no/such/archive.tar.gz", "--repo-root", "/repo"],
            probe);
        Assert.Equal(CliRunner.ExitUsage, code);
        Assert.Contains("does not exist", stderr);
    }

    [Fact]
    public async Task BuildJsonStreamMeetsTheContract()
    {
        var probe = RepoProbe();
        var (code, stdout, _) = await Run(
            ["build", "--acknowledge-decompile", "--json", "--output", "/tmp/out", "--repo-root", "/repo"],
            probe);

        Assert.Equal(CliRunner.ExitOk, code);
        NdjsonStream stream = NdjsonStream.Parse(stdout);
        stream.AssertContract();
        Assert.True(stream.Terminal.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BuildJsonFailurePropagatesTheKebabReasonAndExitsNonZero()
    {
        var probe = RepoProbe();
        var driver = new FakeBuildDriver
        {
            Behaviour = (observer, _) =>
            {
                observer.Phase(ProgressPhase.Patch, 40, "applying patches");
                return BuildResult.Failure(FailureReason.PatchConflict, "patches/vsapi/0007 did not apply");
            },
        };

        var (code, stdout, _) = await Run(
            ["build", "--acknowledge-decompile", "--json", "--output", "/tmp/out", "--repo-root", "/repo"],
            probe, driver);

        Assert.Equal(CliRunner.ExitError, code);
        NdjsonStream stream = NdjsonStream.Parse(stdout);
        stream.AssertContract();
        Assert.False(stream.Terminal.GetProperty("ok").GetBoolean());
        Assert.Equal("patch-conflict", stream.Terminal.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task BuildMapsACancelledTokenToTheCancelledResult()
    {
        var probe = RepoProbe();
        // A driver that reports whatever the token says, the way CliWrap does
        // when a signal trips mid-run.
        var driver = new FakeBuildDriver
        {
            Behaviour = (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return BuildResult.Success("/should/not/reach");
            },
        };

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var (code, stdout, _) = await Run(
            ["build", "--acknowledge-decompile", "--json", "--output", "/tmp/out", "--repo-root", "/repo"],
            probe, driver, cancelled.Token);

        Assert.Equal(CliRunner.ExitError, code);
        NdjsonStream stream = NdjsonStream.Parse(stdout);
        stream.AssertContract();
        Assert.Equal("cancelled", stream.Terminal.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task BuildJsonStreamHasNoProgressAnomaliesOnACleanRun()
    {
        var probe = RepoProbe();
        var (_, stdout, _) = await Run(
            ["build", "--acknowledge-decompile", "--json", "--output", "/tmp/out", "--repo-root", "/repo"],
            probe);

        bool anyClampWarning = NdjsonStream.Parse(stdout).Lines.Any(l =>
            l.GetProperty("type").GetString() == "log"
            && l.GetProperty("level").GetString() == "warn"
            && l.GetProperty("message").GetString()!.Contains("adjusted to"));
        Assert.False(anyClampWarning);
    }

    [Fact]
    public async Task PreflightJsonIsAnArrayOfPrerequisites()
    {
        var probe = RepoProbe();
        var (_, stdout, _) = await Run(["preflight", "--json", "--repo-root", "/repo"], probe);

        using var doc = System.Text.Json.JsonDocument.Parse(stdout.Trim());
        Assert.Equal(System.Text.Json.JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Contains(doc.RootElement.EnumerateArray(), e => e.GetProperty("id").GetString() == "Dotnet");
    }

    [Fact]
    public async Task CapabilitiesJsonNamesThePinnedVersion()
    {
        var probe = RepoProbe();
        probe.AddFile("/repo/forks.json", """{ "vintageStoryVersion": "1.22.7" }""");

        var (code, stdout, _) = await Run(["capabilities", "--json", "--repo-root", "/repo"], probe);

        Assert.Equal(CliRunner.ExitOk, code);
        using var doc = System.Text.Json.JsonDocument.Parse(stdout.Trim());
        Assert.Equal("1.22.7", doc.RootElement.GetProperty("pinnedVersion").GetString());
    }

    [Fact]
    public async Task InstallRejectsARelativePackagePath()
    {
        var (code, _, stderr) = await Run(["install", "--package", "rel/pkg", "--install-dir", "/tmp/i"]);
        Assert.Equal(CliRunner.ExitUsage, code);
        Assert.Contains("absolute", stderr);
    }

    [Fact]
    public async Task UninstallOnADirectoryWithNoManifestIsBadInput()
    {
        string dir = Directory.CreateTempSubdirectory("optimum-cli-test").FullName;
        try
        {
            var (code, _, stderr) = await Run(["uninstall", "--install-dir", dir]);
            Assert.Equal(CliRunner.ExitUsage, code);
            Assert.Contains("manifest", stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BuildWithoutACheckoutPointsAtAcquireSource()
    {
        var (code, _, stderr) = await Run(["build", "--acknowledge-decompile", "--output", "/tmp/out"]);
        Assert.Equal(CliRunner.ExitUsage, code);
        Assert.Contains("--acquire-source", stderr);
    }

    [Fact]
    public async Task BuildAcquiresTheSourceWhenAskedAndThereIsNoCheckout()
    {
        var probe = new FakeSystemProbe();
        probe.AddFile("/downloaded/forks.json", """{ "vintageStoryVersion": "1.22.7" }""");
        probe.AddFile("/downloaded/scripts/bootstrap.sh");
        var driver = new FakeBuildDriver();
        var provider = new StubSourceProvider(SourceAcquisitionResult.Success("/downloaded"));

        var (code, stdout, _) = await Run(
            ["build", "--acknowledge-decompile", "--output", "/tmp/out", "--acquire-source"],
            probe, driver, sourceProvider: provider);

        Assert.Equal(CliRunner.ExitOk, code);
        Assert.Equal(1, provider.Calls);
        Assert.True(driver.WasRun);
        Assert.Contains("Optimum-v0.3.14-linux-x64", stdout);
    }

    [Fact]
    public async Task BuildSurfacesASourceDownloadFailure()
    {
        var provider = new StubSourceProvider(
            SourceAcquisitionResult.Failure(FailureReason.SourceUnavailable, "git clone failed (exit 128)"));

        var (code, _, stderr) = await Run(
            ["build", "--acknowledge-decompile", "--output", "/tmp/out", "--acquire-source"],
            sourceProvider: provider);

        Assert.Equal(CliRunner.ExitError, code);
        Assert.Contains("source-unavailable", stderr);
    }

    [Fact]
    public async Task BuildRejectsAMalformedSuccessfulSourceResult()
    {
        var provider = new StubSourceProvider(new SourceAcquisitionResult(true, null, null, null));

        var (code, _, stderr) = await Run(
            ["build", "--acknowledge-decompile", "--output", "/tmp/out", "--acquire-source"],
            sourceProvider: provider);

        Assert.Equal(CliRunner.ExitError, code);
        Assert.Contains("source-unavailable", stderr);
    }

    [Fact]
    public async Task BuildConvertsAnUnexpectedSourceProviderFaultToAResult()
    {
        var (code, _, stderr) = await Run(
            ["build", "--acknowledge-decompile", "--output", "/tmp/out", "--acquire-source"],
            sourceProvider: new ThrowingSourceProvider());

        Assert.Equal(CliRunner.ExitError, code);
        Assert.Contains("source-unavailable", stderr);
        Assert.Contains("could not start", stderr);
    }

    [Fact]
    public async Task BuildRejectsARelativeSourceCachePath()
    {
        var (code, _, stderr) = await Run(
            ["build", "--acknowledge-decompile", "--output", "/tmp/out", "--acquire-source", "--source-cache", "rel/cache"]);
        Assert.Equal(CliRunner.ExitUsage, code);
        Assert.Contains("absolute", stderr);
    }

    private static FakeSystemProbe RepoProbe()
    {
        var probe = new FakeSystemProbe();
        probe.AddFile("/repo/forks.json", """{ "vintageStoryVersion": "1.22.7" }""");
        probe.AddFile("/repo/scripts/bootstrap.sh");
        return probe;
    }
}
