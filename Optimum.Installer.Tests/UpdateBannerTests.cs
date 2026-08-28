using Optimum.Installer.ViewModels;
using Xunit;

namespace Optimum.Installer.Tests;

public class UpdateBannerTests
{
    [Fact]
    public async Task NoUpdateServiceMeansNoBanner()
    {
        var vm = new MainWindowViewModel(TestServices.Build(updates: null));
        await vm.InstallCompletion; // nothing to wait on, but keeps the shape
        Assert.Null(vm.UpdateBanner);
    }

    [Fact]
    public async Task AnAvailableUpdateShowsTheBanner()
    {
        var updates = new FakeUpdateService { AvailableVersion = "0.4.0" };
        var vm = new MainWindowViewModel(TestServices.Build(updates: updates));

        await Task.Yield();
        Assert.NotNull(vm.UpdateBanner);
        Assert.Contains("0.4.0", vm.UpdateBanner!.Message);

        vm.UpdateBanner.UpdateCommand.Execute(null);
        Assert.True(updates.Applied);
    }

    [Fact]
    public async Task TheBannerIsHiddenOnceTheBuildStarts()
    {
        var vm = new MainWindowViewModel(TestServices.Build(updates: new FakeUpdateService { AvailableVersion = "0.4.0" }));
        await Task.Yield();
        Assert.True(vm.UpdatePromptVisible);

        vm.Prerequisites.ContinueCommand.Execute(null);
        Assert.True(vm.UpdatePromptVisible); // still on Options

        vm.Options.ContinueCommand.Execute(null);
        vm.Eula.ScrolledToEnd = true;
        vm.Eula.Accepted = true;
        vm.Eula.AcceptCommand.Execute(null);
        await vm.InstallCompletion;

        Assert.Equal(WizardScreen.Completion, vm.CurrentScreen);
        Assert.False(vm.UpdatePromptVisible);
    }

    [Fact]
    public async Task DismissingTheBannerHidesItButKeepsTheViewModel()
    {
        var vm = new MainWindowViewModel(TestServices.Build(updates: new FakeUpdateService { AvailableVersion = "0.4.0" }));
        await Task.Yield();
        Assert.True(vm.UpdatePromptVisible);

        vm.UpdateBanner!.DismissCommand.Execute(null);

        Assert.False(vm.UpdatePromptVisible);
        Assert.NotNull(vm.UpdateBanner);
    }

    [Fact]
    public async Task TheBannerIsNotShownOnTheReviewScreen()
    {
        var vm = new MainWindowViewModel(TestServices.Build(updates: new FakeUpdateService { AvailableVersion = "0.4.0" }));
        await Task.Yield();

        vm.Prerequisites.ContinueCommand.Execute(null);
        vm.Options.ContinueCommand.Execute(null);

        Assert.Equal(WizardScreen.Review, vm.CurrentScreen);
        Assert.False(vm.UpdatePromptVisible);
    }

    [Fact]
    public async Task NoAvailableUpdateLeavesTheBannerNull()
    {
        var vm = new MainWindowViewModel(TestServices.Build(updates: new FakeUpdateService { AvailableVersion = null }));
        await Task.Yield();
        Assert.Null(vm.UpdateBanner);
    }
}
