using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Optimum.Installer.ViewModels;
using Optimum.Installer.Views;
using Xunit;

namespace Optimum.Installer.Tests;

public class MainWindowRenderTests
{
    [AvaloniaFact]
    public void TheWindowShowsAndRendersThePrerequisitesView()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel(TestServices.Build()) };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(window.GetVisualDescendants().OfType<PrerequisitesView>());
    }

    [AvaloniaFact]
    public void ContinuingToOptionsSwapsTheRenderedView()
    {
        var vm = new MainWindowViewModel(TestServices.Build());
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        vm.Prerequisites.ContinueCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Empty(window.GetVisualDescendants().OfType<PrerequisitesView>());
        Assert.NotEmpty(window.GetVisualDescendants().OfType<OptionsView>());
    }

}
