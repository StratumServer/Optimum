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
    public void PrerequisitesToOptionsToEula()
    {
        var vm = Wizard();

        Assert.True(vm.Prerequisites.CanContinue);
        vm.Prerequisites.ContinueCommand.Execute(null);
        Assert.Equal(WizardScreen.Options, vm.CurrentScreen);
        Assert.True(vm.CanGoBack);

        vm.Options.ContinueCommand.Execute(null);
        Assert.True(vm.IsEulaOpen);
        Assert.Equal(WizardScreen.Options, vm.CurrentScreen);
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
