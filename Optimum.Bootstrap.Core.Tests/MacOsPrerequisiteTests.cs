using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public sealed class MacOsPrerequisiteTests
{
    [Theory]
    [InlineData(PrerequisiteId.Git, "xcode-select --install", "developer.apple.com")]
    [InlineData(PrerequisiteId.Python3, "universal2 Python 3", "python.org/downloads/macos")]
    [InlineData(PrerequisiteId.Pwsh, "PowerShell 7", "learn.microsoft.com/en-us/powershell")]
    public void MissingToolsHaveMacOsManualInstructionsAndNoLinuxPackageCommands(
        PrerequisiteId id, string instruction, string urlFragment)
    {
        var probe = new FakeSystemProbe { Os = OsKind.MacOs };
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/missing/dotnet";

        PrerequisiteResult result = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(item => item.Definition.Id == id);

        Assert.Equal(AcquisitionKind.Manual, result.Acquisition);
        Assert.Contains(instruction, result.AcquisitionCommand);
        Assert.Contains(urlFragment, result.DownloadUrl);
        Assert.DoesNotContain("apt-get", result.AcquisitionCommand);
        Assert.DoesNotContain("snap", result.AcquisitionCommand);
    }

    [Fact]
    public void MissingDotnetShowsTheOfficialMacOsSdkInstaller()
    {
        var probe = new FakeSystemProbe { Os = OsKind.MacOs };
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/missing/dotnet";

        PrerequisiteResult result = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(item => item.Definition.Id == PrerequisiteId.Dotnet);

        Assert.Equal(AcquisitionKind.Manual, result.Acquisition);
        Assert.Contains(".NET 10 SDK", result.AcquisitionCommand);
        Assert.Contains("aka.ms/dotnet/10.0/dotnet-sdk-osx-x64.pkg", result.DownloadUrl);
    }
}
