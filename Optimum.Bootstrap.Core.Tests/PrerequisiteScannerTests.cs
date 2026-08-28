using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public class PrerequisiteScannerTests
{
    private static FakeSystemProbe LinuxWithCoreTools()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Linux };
        probe.Path.Add("/usr/bin");
        foreach (string tool in new[] { "git", "perl", "python3", "curl", "tar", "chmod", "pwsh", "apt-get" })
            probe.AddFile($"/usr/bin/{tool}");
        probe.AddFile("/lib64/ld-linux-x86-64.so.2");
        return probe;
    }

    [Fact]
    public void OnlyTheSdkBlocksTheBuildWhenTheDecompilerIsAlsoMissing()
    {
        FakeSystemProbe probe = LinuxWithCoreTools();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";

        IReadOnlyList<PrerequisiteResult> results = new PrerequisiteScanner(probe, "/repo").Scan();

        PrerequisiteId[] blocking = results.Where(r => r.BlocksBuild).Select(r => r.Definition.Id).ToArray();
        Assert.Equal([PrerequisiteId.Dotnet], blocking);

        PrerequisiteResult ilspy = results.Single(r => r.Definition.Id == PrerequisiteId.Ilspycmd);
        Assert.Equal(PrerequisiteState.OptionalMissing, ilspy.State);
        Assert.Equal(AcquisitionKind.Automatic, ilspy.Acquisition);
    }

    [Fact]
    public void PowerShellMissingDoesNotBlockTheBuild()
    {
        FakeSystemProbe probe = LinuxWithCoreTools();
        probe.Files.Remove("/usr/bin/pwsh");
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";

        PrerequisiteResult pwsh = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Pwsh);

        Assert.Equal(RequirementLevel.RequiredForPackaging, pwsh.Definition.Level);
        Assert.Equal(PrerequisiteState.Missing, pwsh.State);
        Assert.False(pwsh.BlocksBuild);
    }

    [Fact]
    public void AppimagetoolInThePrivateToolDirectoryMustBeExecutable()
    {
        FakeSystemProbe probe = LinuxWithCoreTools();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";
        probe.AddNonExecutableFile("/repo/.tools/appimagetool");

        PrerequisiteResult appimagetool = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Appimagetool);

        Assert.Equal(PrerequisiteState.OptionalMissing, appimagetool.State);
        Assert.Equal(AcquisitionKind.Automatic, appimagetool.Acquisition);
    }

    [Fact]
    public void AllRequiredPresentWhenTheSdkAndAnInRangeDecompilerAreThere()
    {
        FakeSystemProbe probe = LinuxWithCoreTools();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/home/tester/.dotnet/dotnet";
        probe.AddFile("/home/tester/.dotnet/dotnet");
        probe.OnCommand("/home/tester/.dotnet/dotnet", "--list-sdks", "10.0.100 [/user/sdk]\n");
        probe.OnCommand("/home/tester/.dotnet/dotnet", "--version", "10.0.100\n");
        probe.AddFile("/home/tester/.dotnet/tools/ilspycmd");
        probe.OnCommand("/home/tester/.dotnet/tools/ilspycmd", "--version", "ilspycmd: 10.1.1.8388\n");

        var scanner = new PrerequisiteScanner(probe, "/repo");
        Assert.True(scanner.AllRequiredPresent());

        PrerequisiteResult ilspy = scanner.Scan().Single(r => r.Definition.Id == PrerequisiteId.Ilspycmd);
        Assert.Equal(PrerequisiteState.Ok, ilspy.State);
        Assert.Equal("10.1.1.8388", ilspy.DetectedVersion);
    }

    [Fact]
    public void AnOutOfRangeDecompilerIsReportedOutdatedWithTheUpdateCommand()
    {
        FakeSystemProbe probe = LinuxWithCoreTools();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";
        probe.AddFile("/home/tester/.dotnet/tools/ilspycmd");
        probe.OnCommand("/home/tester/.dotnet/tools/ilspycmd", "--version", "ilspycmd: 10.2.0.1\n");

        PrerequisiteResult ilspy = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Ilspycmd);

        Assert.Equal(PrerequisiteState.Outdated, ilspy.State);
        Assert.Equal(
            "dotnet tool update -g ilspycmd --version 10.1.1.8388 --allow-downgrade",
            ilspy.AcquisitionCommand);
    }

    [Fact]
    public void InnoextractBelowElevenIsOutdated()
    {
        FakeSystemProbe probe = LinuxWithCoreTools();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "/absent/dotnet";
        probe.AddFile("/usr/bin/innoextract");
        probe.OnCommand("/usr/bin/innoextract", "--version", "innoextract 1.9\n");

        PrerequisiteResult inno = new PrerequisiteScanner(probe, "/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Innoextract);

        Assert.Equal(PrerequisiteState.Outdated, inno.State);
    }

    [Theory]
    [InlineData("innoextract 1.11\n", 1, 11)]
    [InlineData("innoextract 1.9-gcc\n", 1, 9)]
    [InlineData("innoextract 2.0.1\n", 2, 0)]
    public void InnoextractVersionParse(string output, int major, int minor)
    {
        Assert.Equal((major, minor), PrerequisiteScanner.ParseInnoextractVersion(output));
    }

    // Forward-slash paths: FakeSystemProbe matches literal strings and the code
    // under test joins with Path.Combine, which uses the host separator when the
    // tests run on Linux. .NET on Windows accepts forward slashes anyway.
    private static FakeSystemProbe WindowsHost()
    {
        var probe = new FakeSystemProbe { Os = OsKind.Windows, HomeDirectory = "C:/Users/tester" };
        probe.Path.Add("C:/Windows/System32/WindowsPowerShell/v1.0");
        probe.AddFile("C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe");
        return probe;
    }

    private static void AddWindowsDotnet(FakeSystemProbe probe)
    {
        probe.Path.Add("C:/Program Files/dotnet");
        probe.AddFile("C:/Program Files/dotnet/dotnet.exe");
        probe.OnCommand("C:/Program Files/dotnet/dotnet.exe", "--list-sdks", "10.0.100 [C:\\sdk]\n");
        probe.OnCommand("C:/Program Files/dotnet/dotnet.exe", "--version", "10.0.100\n");
    }

    [Fact]
    public void WindowsDoesNotDemandUnixToolsAndClearsWithJustDotnetAndGit()
    {
        FakeSystemProbe probe = WindowsHost();
        probe.Path.Add("C:/Program Files/Git/cmd");
        probe.AddFile("C:/Program Files/Git/cmd/git.exe");
        AddWindowsDotnet(probe);

        IReadOnlyList<PrerequisiteResult> results = new PrerequisiteScanner(probe, @"C:\repo").Scan();

        Assert.DoesNotContain(results, r => r.Definition.Id is PrerequisiteId.Perl
            or PrerequisiteId.Python3 or PrerequisiteId.Chmod or PrerequisiteId.Tar
            or PrerequisiteId.Curl or PrerequisiteId.Appimagetool);
        Assert.Empty(results.Where(r => r.BlocksBuild));
        Assert.Equal(PrerequisiteState.Ok, results.Single(r => r.Definition.Id == PrerequisiteId.Pwsh).State);
    }

    [Fact]
    public void WindowsWithoutTheSdkOffersAnAutomaticInstallNotANonFhsRefusal()
    {
        FakeSystemProbe probe = WindowsHost();
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "C:/absent/dotnet.exe";

        PrerequisiteResult dotnet = new PrerequisiteScanner(probe, "C:/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Dotnet);

        Assert.Equal(PrerequisiteState.Missing, dotnet.State);
        Assert.Equal(AcquisitionKind.Automatic, dotnet.Acquisition);
        Assert.DoesNotContain("non-FHS", dotnet.Label);
    }

    [Fact]
    public void WindowsWithoutGitPointsAtGitForWindows()
    {
        FakeSystemProbe probe = WindowsHost();
        AddWindowsDotnet(probe);

        PrerequisiteResult git = new PrerequisiteScanner(probe, "C:/repo").Scan()
            .Single(r => r.Definition.Id == PrerequisiteId.Git);

        Assert.Equal(PrerequisiteState.Missing, git.State);
        Assert.Equal(AcquisitionKind.DownloadPage, git.Acquisition);
        Assert.Equal(PrerequisiteScanner.GitForWindowsUrl, git.DownloadUrl);
        Assert.True(git.BlocksBuild);
    }
}
