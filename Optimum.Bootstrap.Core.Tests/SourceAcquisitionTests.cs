using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Platform;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public class SourceCacheTests
{
    [Fact]
    public void WindowsCacheWithoutScriptsIsNotUsable()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows };
        probe.AddFile("/cache/forks.json");

        Assert.False(SourceCache.IsUsableCheckout(probe, "/cache"));
    }

    [Fact]
    public void WindowsCacheWithOnlyUnixBootstrapScriptIsNotUsable()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows };
        probe.AddFile("/cache/forks.json");
        probe.AddFile("/cache/scripts/bootstrap.sh");

        Assert.False(SourceCache.IsUsableCheckout(probe, "/cache"));
    }

    [Fact]
    public void WindowsCacheMissingPackagingScriptIsNotUsable()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows };
        probe.AddFile("/cache/forks.json");
        probe.AddFile("/cache/scripts/bootstrap.ps1");

        Assert.False(SourceCache.IsUsableCheckout(probe, "/cache"));
    }

    [Fact]
    public void WindowsCacheWithBothRequiredScriptsIsUsable()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows };
        probe.AddFile("/cache/forks.json");
        probe.AddFile("/cache/scripts/bootstrap.ps1");
        probe.AddFile("/cache/scripts/package.ps1");

        Assert.True(SourceCache.IsUsableCheckout(probe, "/cache"));
    }

    [Theory]
    [InlineData("0.3.14", "v0.3.14")]
    [InlineData("1.0.0", "v1.0.0")]
    [InlineData("0.3.14+abc123", "v0.3.14")]
    [InlineData("dev", "dev")]
    [InlineData("", "dev")]
    [InlineData("../evil", "evil")]
    public void SanitizeVersionIsFilesystemSafeAndTagShaped(string version, string expected)
    {
        Assert.Equal(expected, SourceCache.SanitizeVersion(version));
    }

    [Theory]
    [InlineData("0.3.14", "v0.3.14")]
    [InlineData("1.2.3", "v1.2.3")]
    public void TagRefIsTheReleaseTagForARealVersion(string version, string expected)
    {
        Assert.Equal(expected, SourceCache.TagRef(version));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("")]
    public void TagRefIsNullForADevBuild(string version)
    {
        Assert.Null(SourceCache.TagRef(version));
    }

    [Fact]
    public void DirectoryHonoursXdgCacheHome()
    {
        var probe = new FakeSystemProbe();
        probe.Environment["XDG_CACHE_HOME"] = "/xdg/cache";
        Assert.Equal("/xdg/cache/optimum/src-v0.3.14", SourceCache.Directory(probe, "0.3.14"));
    }

    [Fact]
    public void DirectoryFallsBackToDotCache()
    {
        var probe = new FakeSystemProbe { HomeDirectory = "/home/tester" };
        Assert.Equal("/home/tester/.cache/optimum/src-v0.3.14", SourceCache.Directory(probe, "0.3.14"));
    }

    [Fact]
    public void DirectoryHonoursAnExplicitOverride()
    {
        var probe = new FakeSystemProbe();
        Assert.Equal("/custom/optimum/src-dev", SourceCache.Directory(probe, "dev", "/custom"));
    }

    [Fact]
    public void CloneArgumentsAreShallowSingleBranchAndPinnedToTheTag()
    {
        var args = SourceCache.CloneArguments("v0.3.14", "/dest");
        Assert.Equal(
            new[] { "clone", "--depth", "1", "--single-branch", "--branch", "v0.3.14", SourceRequest.RepositoryUrl, "/dest" },
            args);
    }

    [Fact]
    public void CloneArgumentsOmitTheBranchWhenThereIsNoTag()
    {
        var args = SourceCache.CloneArguments(null, "/dest");
        Assert.DoesNotContain("--branch", args);
        Assert.Equal(new[] { "clone", "--depth", "1", "--single-branch", SourceRequest.RepositoryUrl, "/dest" }, args);
    }
}

public class RepoRootTests
{
    [Fact]
    public void WindowsDiscoveryRejectsAUnixOnlyCheckout()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows };
        probe.AddFile("/repo/forks.json");
        probe.AddFile("/repo/scripts/bootstrap.sh");

        Assert.Null(RepoRoot.Discover(probe, "/repo"));
    }

    [Fact]
    public void WindowsDiscoveryReturnsTheCheckoutWithBothPowerShellScripts()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows };
        probe.AddFile("/repo/forks.json");
        probe.AddFile("/repo/scripts/bootstrap.ps1");
        probe.AddFile("/repo/scripts/package.ps1");

        Assert.Equal("/repo", RepoRoot.Discover(probe, "/repo"));
    }

    [Fact]
    public void UnixDiscoveryKeepsItsExistingBootstrapShellContract()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Linux };
        probe.AddFile("/repo/forks.json");
        probe.AddFile("/repo/scripts/bootstrap.sh");

        Assert.Equal("/repo", RepoRoot.Discover(probe, "/repo"));
    }
}

public class GitSourceProviderTests
{
    [Fact]
    public void PromotionReplacesTheOldCheckoutAndRemovesItsBackup()
    {
        string root = System.IO.Directory.CreateTempSubdirectory("optimum-source-promote").FullName;
        string staging = Path.Combine(root, "source.partial");
        string target = Path.Combine(root, "source");
        try
        {
            System.IO.Directory.CreateDirectory(staging);
            System.IO.Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(staging, "new.txt"), "new");
            File.WriteAllText(Path.Combine(target, "old.txt"), "old");

            GitSourceProvider.Promote(staging, target);

            Assert.True(File.Exists(Path.Combine(target, "new.txt")));
            Assert.False(File.Exists(Path.Combine(target, "old.txt")));
            Assert.Empty(System.IO.Directory.EnumerateDirectories(root, "source.previous-*"));
        }
        finally
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReusesACachedCheckoutWithoutTouchingGit()
    {
        var probe = new FakeSystemProbe { HomeDirectory = "/home/tester" };
        string cached = "/home/tester/.cache/optimum/src-v0.3.14";
        probe.AddFile($"{cached}/forks.json");
        probe.AddFile($"{cached}/scripts/bootstrap.sh");
        // no git on PATH: proves the cached path never shells out.

        var result = await new GitSourceProvider(probe)
            .EnsureAsync(new SourceRequest("0.3.14"), NullBuildObserver.Instance, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(cached, result.RepoRoot);
    }

    [Fact]
    public async Task DoesNotReuseAnIncompleteWindowsCheckout()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows };
        probe.Environment["LOCALAPPDATA"] = "/cache";
        string cached = "/cache/optimum/src-v0.3.14";
        probe.AddFile($"{cached}/forks.json");
        probe.AddFile($"{cached}/scripts/bootstrap.sh");
        // No Git on PATH: an incomplete cache must be rejected, not reused.

        var result = await new GitSourceProvider(probe)
            .EnsureAsync(new SourceRequest("0.3.14"), NullBuildObserver.Instance, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.SourceUnavailable, result.Reason);
    }

    [Fact]
    public async Task ReusesACompleteWindowsCheckoutFromTheSourceCache()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows };
        probe.Environment["LOCALAPPDATA"] = "/cache";
        string cached = "/cache/optimum/src-v0.3.14";
        probe.AddFile($"{cached}/forks.json");
        probe.AddFile($"{cached}/scripts/bootstrap.ps1");
        probe.AddFile($"{cached}/scripts/package.ps1");
        // No Git on PATH: proves the complete cached source is the one used.

        var result = await new GitSourceProvider(probe)
            .EnsureAsync(new SourceRequest("0.3.14"), NullBuildObserver.Instance, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(cached, result.RepoRoot);
    }

    [Fact]
    public async Task FailsWithSourceUnavailableWhenGitIsMissing()
    {
        var probe = new FakeSystemProbe { HomeDirectory = "/home/tester" };

        var result = await new GitSourceProvider(probe)
            .EnsureAsync(new SourceRequest("0.3.14"), NullBuildObserver.Instance, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.SourceUnavailable, result.Reason);
        Assert.Contains("git", result.Message);
    }
}
