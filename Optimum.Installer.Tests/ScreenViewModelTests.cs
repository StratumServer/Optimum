using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Tests;
using Optimum.Installer.Services;
using Optimum.Installer.ViewModels;
using Xunit;

namespace Optimum.Installer.Tests;

public class EulaViewModelTests
{
    [Fact]
    public void TheNoticeTextIsTheCoreConsentNotice()
    {
        var vm = new EulaViewModel();
        Assert.Contains("decompil", vm.ConsentText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptNeedsBothScrollAndTheCheckbox()
    {
        var vm = new EulaViewModel();
        bool accepted = false;
        vm.AcceptRequested += () => accepted = true;

        vm.AcceptCommand.Execute(null);
        Assert.False(accepted);

        vm.Accepted = true;
        vm.ScrolledToEnd = true;
        vm.AcceptCommand.Execute(null);
        Assert.True(accepted);
    }

    [Fact]
    public void ResetClearsBothGates()
    {
        var vm = new EulaViewModel { Accepted = true, ScrolledToEnd = true };
        vm.Reset();
        Assert.False(vm.CanAccept);
    }
}

public class OptionsViewModelTests
{
    private static FakeSystemProbe RepoProbe()
    {
        var probe = new FakeSystemProbe();
        probe.AddFile("/repo/forks.json", """{ "vintageStoryVersion": "1.22.7" }""");
        return probe;
    }

    [Fact]
    public void DefaultsToAPlatformInstallDirectoryAndValidates()
    {
        var vm = new OptionsViewModel(RepoProbe(), "/repo");
        Assert.NotEqual(string.Empty, vm.InstallDirectory);
        Assert.Null(vm.ValidationError);
        Assert.True(vm.CanContinue);
    }

    [Fact]
    public void AnUnsafeInstallDirectoryProducesAnInlineError()
    {
        var probe = RepoProbe();
        var vm = new OptionsViewModel(probe, "/repo") { InstallDirectory = probe.HomeDirectory };

        Assert.NotNull(vm.ValidationError);
        Assert.False(vm.CanContinue);
    }

    [Fact]
    public void PicksUpADetectedDataFolderWithASession()
    {
        var probe = RepoProbe();
        probe.AddDirectory("/home/tester/.config/VintagestoryData");
        probe.AddFile("/home/tester/.config/VintagestoryData/clientsettings.json", """{ "playeruid": "x" }""");

        var vm = new OptionsViewModel(probe, "/repo");

        Assert.True(vm.UseSeparateDataFolder);
        Assert.Equal("/home/tester/.config/VintagestoryData", vm.DataPath);
        Assert.Contains("session", vm.DataPathHint!);
    }

    [Fact]
    public void ShowsAVersionChoiceOnlyWhenABridgeSetExists()
    {
        var probe = RepoProbe();
        Assert.False(new OptionsViewModel(probe, "/repo").ShowVersionChoice);

        probe.AddDirectory("/repo/patches-1.22.6-bridge");
        Assert.True(new OptionsViewModel(probe, "/repo").ShowVersionChoice);
    }
}

public class PrerequisitesViewModelTests
{
    [Fact]
    public void ReportsRepoRootMissingWhenThereIsNoCheckout()
    {
        var vm = new PrerequisitesViewModel(new FakeSystemProbe(), repoRoot: null);
        Assert.True(vm.RepoRootMissing);
        Assert.False(vm.CanContinue);
    }
}

public class InstallerLogFilterTests
{
    [Theory]
    [InlineData("error: patch failed", true)]
    [InlineData("[Optimum] Applying patches", true)]
    [InlineData("Restored /home/x", true)]
    [InlineData("  at System.String.Format (Exception)", true)]
    [InlineData("Determining projects to restore...", false)]
    [InlineData("  copying 1834 files", false)]
    public void KeepsAlarmingAndWhitelistedLinesOnly(string line, bool kept)
    {
        Assert.Equal(kept, InstallerLogFilter.IsInteresting(line));
    }
}

public class ProgressViewModelTests
{
    [Fact]
    public async Task ACleanRunReportsSuccessAndAHundredPercent()
    {
        var services = TestServices.Build();
        var session = new InstallSession("/repo", "/home/tester/games/optimum", null, null,
            Bootstrap.Core.Install.ShortcutKinds.None);
        var vm = new ProgressViewModel(services, session, action => action());

        InstallOutcome? outcome = null;
        vm.Finished += o => outcome = o;
        await vm.RunAsync();

        Assert.NotNull(outcome);
        Assert.True(outcome!.Succeeded);
        Assert.Equal(100, vm.Percent);
    }

    [Fact]
    public async Task AFailedDeployReportsTheMessage()
    {
        var installer = new FakePackageInstaller
        {
            Behaviour = _ => Bootstrap.Core.Install.DeployResult.Failure(FailureReason.OutputExists, "already there"),
        };
        var services = TestServices.Build(installer: installer);
        var vm = new ProgressViewModel(services,
            new InstallSession("/repo", "/home/tester/games/optimum", null, null, Bootstrap.Core.Install.ShortcutKinds.None),
            action => action());

        InstallOutcome? outcome = null;
        vm.Finished += o => outcome = o;
        await vm.RunAsync();

        Assert.False(outcome!.Succeeded);
        Assert.Contains("already there", outcome.Message);
    }

    [Fact]
    public async Task InterestingRawOutputReachesTheLogPaneButNoiseDoesNot()
    {
        var driver = new FakeBuildDriver
        {
            Behaviour = (observer, _) =>
            {
                observer.RawOutput(false, "Determining projects to restore...");
                observer.RawOutput(false, "[Optimum] Applying patches: vsapi");
                observer.RawOutput(true, "error: something broke");
                return BuildResult.Success("/tmp/pkg/Optimum-v0.3.14-linux-x64");
            },
        };
        var services = TestServices.Build(driver: driver);
        var vm = new ProgressViewModel(services,
            new InstallSession("/repo", "/home/tester/games/optimum", null, null, Bootstrap.Core.Install.ShortcutKinds.None),
            action => action());
        await vm.RunAsync();

        Assert.Contains(vm.Log, l => l.Text.Contains("Applying patches"));
        Assert.Contains(vm.Log, l => l.Text.Contains("something broke"));
        Assert.DoesNotContain(vm.Log, l => l.Text.Contains("Determining projects"));
    }
}
