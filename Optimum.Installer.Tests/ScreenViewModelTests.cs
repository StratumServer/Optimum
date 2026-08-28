using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Acquisition;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Tests;
using Optimum.Installer.Services;
using Optimum.Installer.ViewModels;
using Xunit;

namespace Optimum.Installer.Tests;

public class ScrollReadGateTests
{
    [Theory]
    [InlineData(200, 300, 0, true)]    // fits, no scroll needed
    [InlineData(1000, 300, 0, false)]  // long, at the top
    [InlineData(1000, 300, 400, false)] // long, mid-scroll
    [InlineData(1000, 300, 700, true)] // long, scrolled to the bottom
    [InlineData(0, 300, 0, false)]     // extent not measured yet
    public void ReadToEnd(double extent, double viewport, double offset, bool expected)
    {
        Assert.Equal(expected, ScrollReadGate.ReadToEnd(extent, viewport, offset));
    }
}

public class EulaViewModelTests
{
    [Fact]
    public void TheNoticeTextIsTheCoreConsentNotice()
    {
        var vm = new EulaViewModel();
        Assert.Contains("decompil", vm.ConsentText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("# Before", vm.ConsentText, StringComparison.Ordinal);
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

        // Prefilled and hinted, but not opted into: the default shares the folder.
        Assert.False(vm.UseSeparateDataFolder);
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
    public async Task AppimagetoolInstallActionRunsAndRescansTheTool()
    {
        var probe = new FakeSystemProbe();
        var acquisition = new FakeAppimagetoolAcquisition
        {
            Behaviour = repoRoot =>
            {
                string path = AppimagetoolAcquisition.TargetPath(repoRoot);
                probe.AddFile(path);
                return ToolAcquisitionResult.Success(path);
            },
        };
        InstallerServices services = TestServices.Build(probe: probe, appimagetool: acquisition);
        var vm = new PrerequisitesViewModel(
            services.Probe, services.RepoRoot, services.SourceProvider, services.Appimagetool, services.UiPost);

        PrerequisiteRowViewModel before = vm.Rows.Single(
            row => row.Result.Definition.Id == Bootstrap.Core.Prerequisites.PrerequisiteId.Appimagetool);
        Assert.Equal("Install", before.ActionLabel);
        Assert.NotNull(before.ActionCommand);

        await before.ActionCommand!.ExecuteAsync(null);

        Assert.Equal(1, acquisition.Calls);
        PrerequisiteRowViewModel after = vm.Rows.Single(
            row => row.Result.Definition.Id == Bootstrap.Core.Prerequisites.PrerequisiteId.Appimagetool);
        Assert.Equal(Bootstrap.Core.Prerequisites.PrerequisiteState.Ok, after.Result.State);
        Assert.Null(after.ActionCommand);
        Assert.Null(after.ActionLabel);
    }

    [Fact]
    public void AutomaticRowsWithoutAWiredInstallerAndManualOrNoneRowsHaveNoButton()
    {
        InstallerServices services = TestServices.Build(appimagetool: new FakeAppimagetoolAcquisition());
        var vm = new PrerequisitesViewModel(
            services.Probe, services.RepoRoot, services.SourceProvider, services.Appimagetool, services.UiPost);

        foreach (var row in vm.Rows)
        {
            bool wiredAutomatic = row.Result.Acquisition == Bootstrap.Core.Prerequisites.AcquisitionKind.Automatic
                && row.Result.Definition.Id == Bootstrap.Core.Prerequisites.PrerequisiteId.Appimagetool;
            bool downloadPage = row.Result.Acquisition == Bootstrap.Core.Prerequisites.AcquisitionKind.DownloadPage
                && !string.IsNullOrEmpty(row.Result.DownloadUrl);
            if (wiredAutomatic || downloadPage)
                Assert.NotNull(row.ActionCommand);
            else
                Assert.Null(row.ActionCommand);
        }
    }

    [Fact]
    public void ADownloadPageRowOffersAGetItButton()
    {
        InstallerServices services = TestServices.Build();
        var vm = new PrerequisitesViewModel(
            services.Probe, services.RepoRoot, services.SourceProvider, services.Appimagetool, services.UiPost);

        PrerequisiteRowViewModel inno = vm.Rows.Single(
            r => r.Result.Definition.Id == Bootstrap.Core.Prerequisites.PrerequisiteId.Innoextract);
        Assert.Equal("Get it", inno.ActionLabel);
        Assert.NotNull(inno.ActionCommand);
    }

    [Fact]
    public async Task WindowsWithoutTheSdkRunsTheWiredSdkInstaller()
    {
        var probe = new FakeSystemProbe { Os = Bootstrap.Core.Platform.OsKind.Windows, HomeDirectory = "C:/Users/tester" };
        probe.Path.Add("C:/ps");
        probe.AddFile("C:/ps/powershell.exe");
        probe.Path.Add("C:/git");
        probe.AddFile("C:/git/git.exe");
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "C:/absent/dotnet.exe";
        probe.AddFile("C:/repo/forks.json", """{ "vintageStoryVersion": "1.22.7" }""");

        var sdk = new FakeSdkAcquisition();
        var vm = new PrerequisitesViewModel(probe, "C:/repo", uiPost: a => a(), sdk: sdk);

        PrerequisiteRowViewModel dotnet = vm.Rows.Single(
            r => r.Result.Definition.Id == Bootstrap.Core.Prerequisites.PrerequisiteId.Dotnet);
        Assert.Equal("Install", dotnet.ActionLabel);

        await dotnet.ActionCommand!.ExecuteAsync(null);
        Assert.Equal(1, sdk.Calls);
    }

    [Fact]
    public void WindowsWithoutGitOffersItsDownloadPage()
    {
        var probe = new FakeSystemProbe { Os = Bootstrap.Core.Platform.OsKind.Windows, HomeDirectory = "C:/Users/tester" };
        probe.Path.Add("C:/ps");
        probe.AddFile("C:/ps/powershell.exe");
        probe.Path.Add("C:/dotnet");
        probe.AddFile("C:/dotnet/dotnet.exe");
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = "C:/dotnet/dotnet.exe";
        probe.OnCommand("C:/dotnet/dotnet.exe", "--list-sdks", "10.0.100 [C:/sdk]\n");
        probe.OnCommand("C:/dotnet/dotnet.exe", "--version", "10.0.100\n");
        probe.AddFile("C:/repo/forks.json", """{ "vintageStoryVersion": "1.22.7" }""");

        var vm = new PrerequisitesViewModel(probe, "C:/repo", uiPost: a => a());

        PrerequisiteRowViewModel git = vm.Rows.Single(
            r => r.Result.Definition.Id == Bootstrap.Core.Prerequisites.PrerequisiteId.Git);
        Assert.Equal("Get it", git.ActionLabel);
        Assert.NotNull(git.ActionCommand);
    }

    [Fact]
    public async Task AppimagetoolInstallFailureIsVisibleInTheRow()
    {
        var acquisition = new FakeAppimagetoolAcquisition
        {
            Behaviour = _ => ToolAcquisitionResult.Failure(
                FailureReason.SourceUnavailable, "curl exit 22"),
        };
        InstallerServices services = TestServices.Build(appimagetool: acquisition);
        var vm = new PrerequisitesViewModel(
            services.Probe, services.RepoRoot, services.SourceProvider, services.Appimagetool, services.UiPost);
        PrerequisiteRowViewModel row = vm.Rows.Single(
            item => item.Result.Definition.Id == Bootstrap.Core.Prerequisites.PrerequisiteId.Appimagetool);

        await row.ActionCommand!.ExecuteAsync(null);

        Assert.Contains("curl exit 22", row.Detail);
        Assert.Equal("Install", row.ActionLabel);
    }

    [Fact]
    public void ReportsRepoRootMissingWhenThereIsNoCheckout()
    {
        var vm = new PrerequisitesViewModel(new FakeSystemProbe(), repoRoot: null);
        Assert.True(vm.RepoRootMissing);
        Assert.False(vm.CanContinue);
        Assert.False(vm.CanAcquireSource);
        Assert.Contains("inside an Optimum checkout", vm.Summary);
    }

    [Fact]
    public void OffersToDownloadTheSourceWhenGivenAProvider()
    {
        var vm = new PrerequisitesViewModel(
            new FakeSystemProbe(), repoRoot: null, new FakeSourceProvider(), uiPost: a => a());

        Assert.True(vm.CanAcquireSource);
        Assert.Contains("downloaded from GitHub", vm.Summary);
    }

    [Fact]
    public async Task DownloadingTheSourceClearsTheMissingStateAndScans()
    {
        var probe = new FakeSystemProbe();
        probe.Path.Add("/usr/bin");
        foreach (string tool in new[] { "dotnet", "git", "perl", "python3", "curl", "tar", "chmod", "pwsh", "bash" })
            probe.AddFile($"/usr/bin/{tool}");
        probe.OnCommand("/usr/bin/dotnet", "--list-sdks", "10.0.100 [/x]\n");
        probe.OnCommand("/usr/bin/dotnet", "--version", "10.0.100\n");
        probe.AddFile("/downloaded-repo/forks.json", """{ "vintageStoryVersion": "1.22.7" }""");
        probe.AddFile("/downloaded-repo/scripts/bootstrap.sh");
        var provider = new FakeSourceProvider();

        string? continuedWith = null;
        var vm = new PrerequisitesViewModel(probe, repoRoot: null, provider, uiPost: a => a());
        vm.ContinueRequested += root => continuedWith = root;

        await vm.AcquireSourceCommand.ExecuteAsync(null);

        Assert.Equal(1, provider.Calls);
        Assert.False(vm.RepoRootMissing);
        Assert.NotEmpty(vm.Rows);

        vm.ContinueCommand.Execute(null);
        Assert.Equal("/downloaded-repo", continuedWith);
    }

    [Fact]
    public async Task AFailedDownloadShowsTheError()
    {
        var provider = new FakeSourceProvider
        {
            Behaviour = _ => SourceAcquisitionResult.Failure(FailureReason.SourceUnavailable, "git clone failed (exit 128)"),
        };
        var vm = new PrerequisitesViewModel(new FakeSystemProbe(), repoRoot: null, provider, uiPost: a => a());

        await vm.AcquireSourceCommand.ExecuteAsync(null);

        Assert.True(vm.RepoRootMissing);
        Assert.False(vm.CanContinue);
        Assert.Contains("git clone failed", vm.Summary);
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

    [Theory]
    [InlineData("Compilação com êxito.", "info")]
    [InlineData("Decompiling exact VSEssentials runtime donor...", "info")]
    [InlineData("You are not using the latest version of the tool, please update.", "warn")]
    [InlineData("Latest version is '11.0.0.9375' (yours is '10.1.0.8386')", "warn")]
    [InlineData("warning CS0168: variable declared but never used", "warn")]
    [InlineData("error: patch failed", "error")]
    [InlineData("hunk #3 FAILED at 210", "error")]
    [InlineData("System.IO.IOException: disk full", "error")]
    public void ClassifyPicksSeverityFromContentNotStream(string line, string expected)
    {
        Assert.Equal(expected, InstallerLogFilter.Classify(line, fromStdErr: true));
    }
}

public class ProgressViewModelTests
{
    [Fact]
    public void CancellingRequiresAnExplicitConfirmation()
    {
        var vm = new ProgressViewModel(
            TestServices.Build(),
            new InstallSession("/repo", "/home/tester/games/optimum", null, null,
                Bootstrap.Core.Install.ShortcutKinds.None),
            action => action());

        vm.RequestCancelCommand.Execute(null);
        Assert.True(vm.ConfirmCancel);
        Assert.False(vm.CancelRequested);

        vm.KeepInstallingCommand.Execute(null);
        Assert.False(vm.ConfirmCancel);
        Assert.False(vm.CancelRequested);
    }

    private static InstallSession Session() =>
        new("/repo", "/home/tester/games/optimum", null, null, Bootstrap.Core.Install.ShortcutKinds.None);

    [Fact]
    public async Task ClickingCancelAfterTheRunFinishesIsANoOpNotACrash()
    {
        // The Progress view can still be on screen for a beat after RunAsync
        // returns; a late click on Cancel must not touch a disposed CTS.
        var vm = new ProgressViewModel(TestServices.Build(), Session(), action => action());
        await vm.RunAsync();

        Exception? thrown = Record.Exception(() =>
        {
            vm.RequestCancelCommand.Execute(null);
            vm.CancelCommand.Execute(null);
        });

        Assert.Null(thrown);
    }

    [Fact]
    public async Task CancellingMidRunEndsTheBuildWithoutAnUnobservedException()
    {
        var unobserved = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) => { unobserved.Add(e.Exception); e.SetObserved(); };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var driver = new GatedBuildDriver();
            var vm = new ProgressViewModel(TestServices.Build(driver: driver), Session(), action => action());

            Task run = vm.RunAsync();
            vm.RequestCancelCommand.Execute(null);
            vm.CancelCommand.Execute(null);
            await run;

            Assert.True(vm.CancelRequested);

            await Task.Delay(50, TestContext.Current.CancellationToken);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }

        Assert.Empty(unobserved);
    }

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
    public async Task TheTemporaryBuildDirectoryIsDeletedAfterwards()
    {
        string? capturedOutput = null;
        var driver = new FakeBuildDriver
        {
            Behaviour = (_, _) => BuildResult.Success("/tmp/pkg/Optimum-v0.3.14-linux-x64"),
        };
        var services = TestServices.Build(driver: driver);
        var vm = new ProgressViewModel(services,
            new InstallSession("/repo", "/home/tester/games/optimum", null, null, Bootstrap.Core.Install.ShortcutKinds.None),
            action => action());

        // Have the driver create the directory the way the real one does.
        driver.Behaviour = (_, _) =>
        {
            capturedOutput = driver.LastOutputDirectory;
            Directory.CreateDirectory(capturedOutput!);
            File.WriteAllText(Path.Combine(capturedOutput!, "marker"), "x");
            return BuildResult.Success("/tmp/pkg/Optimum-v0.3.14-linux-x64");
        };

        await vm.RunAsync();

        Assert.NotNull(capturedOutput);
        Assert.False(Directory.Exists(capturedOutput!));
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

