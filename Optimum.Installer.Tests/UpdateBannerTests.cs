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
    public async Task NoAvailableUpdateLeavesTheBannerNull()
    {
        var vm = new MainWindowViewModel(TestServices.Build(updates: new FakeUpdateService { AvailableVersion = null }));
        await Task.Yield();
        Assert.Null(vm.UpdateBanner);
    }
}
