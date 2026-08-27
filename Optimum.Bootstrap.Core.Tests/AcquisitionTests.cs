using Optimum.Bootstrap.Core.Acquisition;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Platform;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests;

public class AcquisitionTests
{
    [Fact]
    public void AppimagetoolDownloadUsesRedirectsAndFailsOnHttpErrors()
    {
        var acquisition = new AppimagetoolAcquisition(new FakeSystemProbe(), "https://example.test/tool.AppImage");

        Assert.Equal(
            ["--location", "--fail", "--show-error", "--output", "/tmp/tool.partial", "https://example.test/tool.AppImage"],
            acquisition.DownloadArguments("/tmp/tool.partial"));
    }

    [Fact]
    public async Task AppimagetoolDownloadProducesAnExecutableTool()
    {
        if (!OperatingSystem.IsLinux()
            || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                != System.Runtime.InteropServices.Architecture.X64)
            return;

        string testRoot = Path.Combine(Path.GetTempPath(), "optimum-appimagetool-test-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(testRoot, "source.AppImage");
        string repoRoot = Path.Combine(testRoot, "repo");

        try
        {
            Directory.CreateDirectory(repoRoot);
            await File.WriteAllTextAsync(source, "appimagetool test payload");
            var acquisition = new AppimagetoolAcquisition(SystemProbe.Default, new Uri(source).AbsoluteUri);

            ToolAcquisitionResult result = await acquisition.InstallAsync(
                repoRoot, NullBuildObserver.Instance, CancellationToken.None);

            string target = AppimagetoolAcquisition.TargetPath(repoRoot);
            Assert.True(result.Ok, result.Message);
            Assert.Equal(target, result.InstalledPath);
            Assert.Equal("appimagetool test payload", await File.ReadAllTextAsync(target));
            Assert.True(SystemProbe.Default.IsExecutable(target));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(target)!, "*.partial-*"));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void IlspycmdToolArgumentsMatchTheLoggedInvocation()
    {
        // scripts/tests/install-linux-prerequisites.sh asserts exactly this line.
        Assert.Equal(
            "tool update -g ilspycmd --version 10.1.1.8388 --allow-downgrade",
            string.Join(' ', IlspycmdAcquisition.ToolArguments("10.1.1.8388")));
    }

    [Fact]
    public void SdkPlanHonoursGlobalJsonWhenItIsPresent()
    {
        var probe = new FakeSystemProbe();
        probe.AddFile("/lib64/ld-linux-x86-64.so.2");
        probe.AddFile("/repo/global.json");

        SdkAcquisition.Decision decision = SdkAcquisition.Evaluate(probe, "/repo");

        Assert.True(decision.CanRunScript);
        Assert.NotNull(decision.Plan);
        Assert.Contains("--jsonfile", decision.Plan!.Arguments);
        Assert.Contains("/repo/global.json", decision.Plan.Arguments);
        Assert.Contains("--no-path", decision.Plan.Arguments);
        Assert.EndsWith("dotnet-install.sh", decision.Plan.ScriptUrl);
    }

    [Fact]
    public void SdkPlanFallsBackToTheChannelWhenGlobalJsonIsAbsent()
    {
        var probe = new FakeSystemProbe();
        probe.AddFile("/lib64/ld-linux-x86-64.so.2");

        SdkAcquisition.Decision decision = SdkAcquisition.Evaluate(probe, "/repo");

        Assert.NotNull(decision.Plan);
        Assert.Contains("--channel", decision.Plan!.Arguments);
        Assert.Contains("10.0", decision.Plan.Arguments);
    }
}
