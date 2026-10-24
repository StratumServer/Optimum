using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;
using Optimum.Bootstrap.Core.Tests;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public sealed class DistroPackageHintsTests
{
    [Theory]
    [InlineData("apt-get", "python3", "apt-get", "python3", "install -y python3")]
    [InlineData("apt-get", "git", "apt-get", "git", "install -y git")]
    [InlineData("dnf", "git", "dnf", "git", "install -y git")]
    [InlineData("dnf", "chmod", "dnf", "coreutils", "install -y coreutils")]
    [InlineData("pacman", "git", "pacman", "git", "-S --needed --noconfirm git")]
    [InlineData("pacman", "python3", "pacman", "python", "-S --needed --noconfirm python")]
    [InlineData("pacman", "mkisofs", "pacman", "cdrtools", "-S --needed --noconfirm cdrtools")]
    [InlineData("zypper", "git", "zypper", "git", "--non-interactive install git")]
    [InlineData("zypper", "mkisofs", "zypper", "genisoimage", "--non-interactive install genisoimage")]
    public void CreatesExactPlansForSupportedPackageManagers(
        string manager, string command, string expectedManager, string package, string expectedArguments)
    {
        FakeSystemProbe probe = ProbeWith(manager);

        DistroPackagePlan plan = Assert.IsType<DistroPackagePlan>(DistroPackageHints.Plan(probe, command));

        Assert.Equal(package, plan.Package);
        Assert.Equal(expectedManager, Path.GetFileName(plan.ManagerPath));
        Assert.Equal($"/usr/bin/{expectedManager}", plan.Arguments[0]);
        Assert.Equal(expectedArguments, string.Join(' ', plan.Arguments.Skip(1)));
        Assert.Contains(package, plan.ManualCommand);
    }

    [Fact]
    public void OffersCopyableManualCommandWhenPkexecIsMissingButDoesNotOfferExecution()
    {
        FakeSystemProbe probe = ProbeWith("apt-get");
        probe.Files.Remove("/usr/bin/pkexec");

        Assert.Null(DistroPackageHints.Plan(probe, "git"));
        Assert.Equal("sudo apt-get install -y git", DistroPackageHints.InstallCommand(probe, "git"));
    }

    [Fact]
    public void GivesManualDirectionsWhenNoSupportedPackageManagerExists()
    {
        FakeSystemProbe probe = ProbeWith("apt-get");
        probe.Files.Remove("/usr/bin/apt-get");
        probe.Files.Remove("/usr/bin/pkexec");

        Assert.Null(DistroPackageHints.InstallCommand(probe, "perl"));
        Assert.Contains("No supported apt, dnf, pacman, or zypper manager was detected",
            DistroPackageHints.ManualInstruction(probe, "perl"));
    }

    [Fact]
    public void DoesNotInventPackagesForUnsupportedToolsOrNonLinuxHosts()
    {
        FakeSystemProbe probe = ProbeWith("apt-get");
        Assert.Null(DistroPackageHints.Plan(probe, "pwsh"));
        probe.Os = OsKind.MacOs;
        Assert.Null(DistroPackageHints.Plan(probe, "git"));
        Assert.Null(DistroPackageHints.InstallCommand(probe, "git"));
    }

    private static FakeSystemProbe ProbeWith(string manager)
    {
        var probe = new FakeSystemProbe { Os = OsKind.Linux };
        probe.Path.Add("/usr/bin");
        probe.AddFile($"/usr/bin/{manager}");
        probe.AddFile("/usr/bin/pkexec");
        return probe;
    }
}
