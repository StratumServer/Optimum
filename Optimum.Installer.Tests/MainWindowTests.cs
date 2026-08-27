using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Optimum.Bootstrap.Core;
using Optimum.Installer.ViewModels;
using Optimum.Installer.Views;
using Xunit;

namespace Optimum.Installer.Tests;

public class MainWindowViewModelTests
{
    [Fact]
    public void TitleCarriesTheCoreVersion()
    {
        var vm = new MainWindowViewModel();
        Assert.Contains(CoreInfo.Version, vm.Title);
    }
}

public class MainWindowRenderTests
{
    [AvaloniaFact]
    public void WindowShowsAndBindsTheTitle()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel() };
        window.Show();

        var title = Assert.IsType<TextBlock>(
            ((StackPanel)window.Content!).Children[0]);
        Assert.Equal("Optimum installer " + CoreInfo.Version, title.Text);
    }
}
