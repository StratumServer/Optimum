using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Build;
using Optimum.Installer.ViewModels;
using Xunit;

namespace Optimum.Installer.Tests;

public class WizardStateMachineTests
{
    private static MainWindowViewModel Wizard(FakeBuildDriver? driver = null, FakePackageInstaller? installer = null) =>
        new(TestServices.Build(driver: driver, installer: installer));

    [Fact]
    public void StartsOnPrerequisites()
    {
        Assert.Equal(WizardScreen.Prerequisites, Wizard().CurrentScreen);
    }

    [Fact]
    public void PrerequisitesContinueIsBlockedWhenARequiredToolIsMissing()
    {
        var vm = new MainWindowViewModel(TestServices.Build(dotnetPresent: false));

        Assert.False(vm.Prerequisites.CanContinue);
        vm.Prerequisites.ContinueCommand.Execute(null);
        Assert.Equal(WizardScreen.Prerequisites, vm.CurrentScreen);
    }

    [Fact]
    public void PrerequisitesToOptionsToReview()
    {
        var vm = Wizard();

        Assert.True(vm.Prerequisites.CanContinue);
        vm.Prerequisites.ContinueCommand.Execute(null);
        Assert.Equal(WizardScreen.Options, vm.CurrentScreen);
        Assert.True(vm.CanGoBack);

        vm.Options.ContinueCommand.Execute(null);
        Assert.True(vm.IsEulaOpen);
        Assert.Equal(WizardScreen.Review, vm.CurrentScreen);
        Assert.Equal(3, vm.CurrentStepNumber);
        Assert.Equal("Step 3 of 4", vm.CurrentStepLabel);
        Assert.Equal(vm.Options.InstallDirectory, vm.Eula.InstallDirectory);
    }

    [Fact]
    public void BackFromOptionsReturnsToPrerequisites()
    {
        var vm = Wizard();
        vm.Prerequisites.ContinueCommand.Execute(null);
        vm.Options.BackCommand.Execute(null);
        Assert.Equal(WizardScreen.Prerequisites, vm.CurrentScreen);
        Assert.False(vm.CanGoBack);
    }

    [Fact]
    public void SteppingBackToPrerequisitesAndForwardKeepsTheOptionsChoices()
    {
        var vm = Wizard();
        vm.Prerequisites.ContinueCommand.Execute(null);
        vm.Options.InstallDirectory = "/home/tester/custom/optimum";
        vm.Options.CreateDesktopShortcut = true;

        vm.Options.BackCommand.Execute(null);
        vm.Prerequisites.ContinueCommand.Execute(null);

        Assert.Equal(WizardScreen.Options, vm.CurrentScreen);
        Assert.Equal("/home/tester/custom/optimum", vm.Options.InstallDirectory);
        Assert.True(vm.Options.CreateDesktopShortcut);
    }

    [Fact]
    public void DecliningTheEulaKeepsTheUserOnOptions()
    {
        var vm = Wizard();
        vm.Prerequisites.ContinueCommand.Execute(null);
        vm.Options.ContinueCommand.Execute(null);

        vm.Eula.DeclineCommand.Execute(null);

        Assert.False(vm.IsEulaOpen);
        Assert.Equal(WizardScreen.Options, vm.CurrentScreen);
    }

    [Fact]
    public void WizardHeaderExplainsEachStep()
    {
        var vm = Wizard();
        Assert.Equal("Check your system", vm.CurrentStepTitle);
        Assert.Equal(25, vm.CurrentStepProgress);
        Assert.Equal(StepState.Current, vm.Steps[0].State);
        Assert.Equal(StepState.Upcoming, vm.Steps[1].State);

        vm.Prerequisites.ContinueCommand.Execute(null);
        Assert.Equal("Set up the install", vm.CurrentStepTitle);
        Assert.Equal(50, vm.CurrentStepProgress);
        Assert.Equal(StepState.Done, vm.Steps[0].State);
        Assert.Equal(StepState.Current, vm.Steps[1].State);

        vm.Options.ContinueCommand.Execute(null);
        Assert.Equal("Review and consent", vm.CurrentStepTitle);
        Assert.Equal(75, vm.CurrentStepProgress);
    }

    [Fact]
    public async Task EveryRailStepReadsAsDoneOnceTheInstallFinishes()
    {
        var vm = Wizard();
        vm.Prerequisites.ContinueCommand.Execute(null);
        vm.Options.ContinueCommand.Execute(null);
        vm.Eula.ScrolledToEnd = true;
        vm.Eula.Accepted = true;
        vm.Eula.AcceptCommand.Execute(null);
        await vm.InstallCompletion;

        Assert.False(vm.HeaderVisible);
        Assert.All(vm.Steps, step => Assert.Equal(StepState.Done, step.State));
    }

    [Fact]
    public void ReviewSummarizesDataVersionAndShortcuts()
    {
        var vm = Wizard();
        vm.Prerequisites.ContinueCommand.Execute(null);
        vm.Options.UseSeparateDataFolder = true;
        vm.Options.DataPath = "/home/tester/vs-data";
        vm.Options.CreateMenuEntry = false;
        vm.Options.CreateDesktopShortcut = true;

        vm.Options.ContinueCommand.Execute(null);

        Assert.Equal("/home/tester/vs-data", vm.Eula.DataPathSummary);
        Assert.Equal("Desktop shortcut", vm.Eula.ShortcutSummary);
        Assert.NotEmpty(vm.Eula.VersionSummary);
    }

    [Fact]
    public void AcceptingTheEulaIsBlockedUntilScrolledAndTicked()
    {
        var vm = Wizard();
        vm.Prerequisites.ContinueCommand.Execute(null);
        vm.Options.ContinueCommand.Execute(null);

        Assert.False(vm.Eula.CanAccept);
        vm.Eula.Accepted = true;
        Assert.False(vm.Eula.CanAccept);
        vm.Eula.ScrolledToEnd = true;
        Assert.True(vm.Eula.CanAccept);
    }

    [Fact]
    public async Task AcceptingTheEulaRunsTheBuildAndLandsOnCompletionOk()
    {
        var vm = Wizard();
        vm.Prerequisites.ContinueCommand.Execute(null);
        vm.Options.ContinueCommand.Execute(null);
        vm.Eula.ScrolledToEnd = true;
        vm.Eula.Accepted = true;

        vm.Eula.AcceptCommand.Execute(null);
        await vm.InstallCompletion;

        Assert.Equal(WizardScreen.Completion, vm.CurrentScreen);
        Assert.NotNull(vm.Completion);
        Assert.True(vm.Completion!.Succeeded);
        Assert.False(vm.IsEulaOpen);
    }

    [Fact]
    public async Task AcquiredSourceRootFlowsThroughTheWizardIntoTheBuild()
    {
        var probe = new Optimum.Bootstrap.Core.Tests.FakeSystemProbe();
        probe.AddFile("/downloaded-repo/forks.json", """{ "vintageStoryVersion": "1.22.7" }""");
        probe.AddFile("/downloaded-repo/scripts/bootstrap.sh");
        var driver = new FakeBuildDriver();
        var sourceProvider = new FakeSourceProvider();
        var vm = new MainWindowViewModel(TestServices.Build(
            repoRoot: null, probe: probe, driver: driver, sourceProvider: sourceProvider));

        await vm.Prerequisites.AcquireSourceCommand.ExecuteAsync(null);
        vm.Prerequisites.ContinueCommand.Execute(null);
        vm.Options.ContinueCommand.Execute(null);
        vm.Eula.ScrolledToEnd = true;
        vm.Eula.Accepted = true;
        vm.Eula.AcceptCommand.Execute(null);
        await vm.InstallCompletion;

        Assert.Equal(1, sourceProvider.Calls);
        Assert.Equal("/downloaded-repo", driver.LastRepoRoot);
        Assert.True(vm.Completion!.Succeeded);
    }

    [Fact]
    public async Task AFailedBuildLandsOnCompletionWithRetry()
    {
        var driver = new FakeBuildDriver
        {
            Behaviour = (_, _) => BuildResult.Failure(FailureReason.PatchConflict, "a patch did not apply"),
        };
        var vm = Wizard(driver);
        vm.Prerequisites.ContinueCommand.Execute(null);
        vm.Options.ContinueCommand.Execute(null);
        vm.Eula.ScrolledToEnd = true;
        vm.Eula.Accepted = true;
        vm.Eula.AcceptCommand.Execute(null);
        await vm.InstallCompletion;

        Assert.Equal(WizardScreen.Completion, vm.CurrentScreen);
        Assert.True(vm.Completion!.CanRetry);
        Assert.False(vm.Completion.Succeeded);

        vm.Completion!.RetryCommand.Execute(null);
        Assert.Equal(WizardScreen.Prerequisites, vm.CurrentScreen);
        Assert.Null(vm.Completion);
    }

    [Fact]
    public async Task AcceptingTheEulaTwiceRunsTheBuildOnlyOnce()
    {
        var driver = new FakeBuildDriver();
        var vm = Wizard(driver);
        vm.Prerequisites.ContinueCommand.Execute(null);
        vm.Options.ContinueCommand.Execute(null);
        vm.Eula.ScrolledToEnd = true;
        vm.Eula.Accepted = true;

        vm.Eula.AcceptCommand.Execute(null);
        vm.Eula.AcceptCommand.Execute(null);
        await vm.InstallCompletion;

        Assert.Equal(1, driver.RunCount);
    }

    [Fact]
    public async Task ACancelledBuildCanBeRetried()
    {
        var driver = new FakeBuildDriver
        {
            Behaviour = (_, _) => BuildResult.Failure(FailureReason.Cancelled, "the build was cancelled"),
        };
        var vm = Wizard(driver);
        vm.Prerequisites.ContinueCommand.Execute(null);
        vm.Options.ContinueCommand.Execute(null);
        vm.Eula.ScrolledToEnd = true;
        vm.Eula.Accepted = true;
        vm.Eula.AcceptCommand.Execute(null);
        await vm.InstallCompletion;

        Assert.True(vm.Completion!.Cancelled);
        Assert.True(vm.Completion.CanRetry);
        vm.Completion.RetryCommand.Execute(null);
        Assert.Equal(WizardScreen.Prerequisites, vm.CurrentScreen);
    }
}
