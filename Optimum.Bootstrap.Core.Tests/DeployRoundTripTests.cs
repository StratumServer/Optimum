using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Platform;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

/// <summary>
/// PackageDeployer and Uninstaller do real filesystem work, so these run against
/// temp directories with a real <see cref="SystemProbe"/>.
/// </summary>
public sealed class DeployRoundTripTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("optimum-deploy-test").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string StagePackage()
    {
        string package = Path.Combine(_root, "staged", "Optimum-v0.3.14-linux-x64");
        Directory.CreateDirectory(Path.Combine(package, ".optimum"));
        Directory.CreateDirectory(Path.Combine(package, "assets"));
        File.WriteAllText(Path.Combine(package, "run.sh"), "#!/bin/sh\nexec ./Optimum\n");
        File.WriteAllText(Path.Combine(package, "Optimum"), "binary");
        File.WriteAllText(Path.Combine(package, "assets", "gameicon.png"), "png");
        File.WriteAllText(Path.Combine(package, ".optimum", "version"), "0.3.14");
        return package;
    }

    [Fact]
    public void DeployThenUninstallLeavesNothingBehind()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();
        string installDir = Path.Combine(_root, "install", "optimum");
        string dataPath = Path.Combine(_root, "data");

        DeployResult deploy = new PackageDeployer(probe).Deploy(
            new DeployRequest(package, installDir, dataPath));

        Assert.True(deploy.Ok, deploy.Message);
        Assert.True(File.Exists(Path.Combine(installDir, "run.sh")));
        Assert.True(File.Exists(Path.Combine(installDir, "assets", "gameicon.png")));
        Assert.True(File.Exists(Path.Combine(installDir, InstallManifest.RelativePath)));
        Assert.Equal(dataPath, File.ReadAllText(Path.Combine(installDir, "datapath.cfg")));

        string launcherName = probe.Os == OsKind.Windows ? "optimum-launch.cmd" : "optimum-launch.sh";
        Assert.True(File.Exists(Path.Combine(installDir, launcherName)));

        InstallManifest manifest = InstallManifest.Deserialize(
            File.ReadAllText(Path.Combine(installDir, InstallManifest.RelativePath)))!;
        Assert.Equal("0.3.14", manifest.OptimumVersion);

        UninstallResult uninstall = new Uninstaller(probe).Uninstall(installDir);

        Assert.True(uninstall.Ok);
        Assert.False(Directory.Exists(installDir));
    }

    [Fact]
    public void DeployRefusesANonEmptyDirectoryWithNoManifest()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();
        string occupied = Path.Combine(_root, "occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "someone-elses-file"), "x");

        DeployResult result = new PackageDeployer(probe).Deploy(new DeployRequest(package, occupied));

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.OutputExists, result.Reason);
        Assert.True(File.Exists(Path.Combine(occupied, "someone-elses-file")));
    }

    [Fact]
    public void DeployRefusesToReplaceAnExistingOptimumInstall()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();
        string installDir = Path.Combine(_root, "install", "optimum");

        Assert.True(new PackageDeployer(probe).Deploy(new DeployRequest(package, installDir)).Ok);

        DeployResult second = new PackageDeployer(probe).Deploy(new DeployRequest(package, installDir));

        Assert.False(second.Ok);
        Assert.Equal(FailureReason.OutputExists, second.Reason);
        Assert.Contains("uninstall", second.Message);
    }

    [Fact]
    public void ManifestRecordsTheVersionFromThePackageDirectoryName()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();
        string installDir = Path.Combine(_root, "install", "optimum");

        Assert.True(new PackageDeployer(probe).Deploy(new DeployRequest(package, installDir)).Ok);

        InstallManifest manifest = InstallManifest.Deserialize(
            File.ReadAllText(Path.Combine(installDir, InstallManifest.RelativePath)))!;
        Assert.Equal("0.3.14", manifest.OptimumVersion);
    }

    [Fact]
    public void UninstallSkipsAManifestEntryThatEscapesTheInstallDirectory()
    {
        var probe = SystemProbe.Default;
        string installDir = Path.Combine(_root, "install", "optimum");
        Directory.CreateDirectory(Path.Combine(installDir, ".optimum"));
        string outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "do not touch");

        var manifest = new InstallManifest
        {
            OptimumVersion = "0.3.14",
            InstalledAtUtc = DateTimeOffset.UtcNow,
            InstallDirectory = installDir,
            Entries = ["../outside.txt", "run.sh"],
        };
        File.WriteAllText(Path.Combine(installDir, InstallManifest.RelativePath), manifest.Serialize());
        File.WriteAllText(Path.Combine(installDir, "run.sh"), "x");

        UninstallResult result = new Uninstaller(probe).Uninstall(installDir);

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void DeployRejectsAnUnsafeInstallPath()
    {
        var probe = SystemProbe.Default;
        string package = StagePackage();

        DeployResult deploy = new PackageDeployer(probe).Deploy(
            new DeployRequest(package, probe.HomeDirectory));

        Assert.False(deploy.Ok);
        Assert.Equal(FailureReason.BadInput, deploy.Reason);
    }

    [Fact]
    public void UninstallRefusesADirectoryWithNoManifest()
    {
        string bare = Path.Combine(_root, "bare");
        Directory.CreateDirectory(bare);
        File.WriteAllText(Path.Combine(bare, "important.txt"), "keep me");

        UninstallResult result = new Uninstaller(SystemProbe.Default).Uninstall(bare);

        Assert.False(result.Ok);
        Assert.Equal(FailureReason.BadInput, result.Reason);
        Assert.True(File.Exists(Path.Combine(bare, "important.txt")));
    }
}
