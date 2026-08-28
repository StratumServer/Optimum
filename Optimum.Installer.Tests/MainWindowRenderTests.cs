using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Optimum.Bootstrap.Core.Tests;
using Optimum.Installer.ViewModels;
using Optimum.Installer.Views;
using Xunit;

namespace Optimum.Installer.Tests;

public class MainWindowRenderTests
{
    [AvaloniaFact]
    public void TheContinueButtonStaysOnScreenWhenTheToolListOverflows()
    {
        // A bare machine: every prerequisite is missing, so the row list is far
        // taller than the window. The list must scroll inside its own region and
        // leave the Continue button visible.
        var probe = new FakeSystemProbe();
        probe.AddFile("/repo/forks.json", """{ "vintageStoryVersion": "1.22.7" }""");
        var services = TestServices.Build(repoRoot: "/repo", probe: probe, dotnetPresent: false);
        var window = new MainWindow { DataContext = new MainWindowViewModel(services) };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Button continueButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Content as string == "Continue");
        Point topLeft = continueButton.TranslatePoint(new Point(0, 0), window)!.Value;

        Assert.True(topLeft.Y + continueButton.Bounds.Height <= window.Bounds.Height,
            $"Continue button bottom at {topLeft.Y + continueButton.Bounds.Height}, window height {window.Bounds.Height}");
    }

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
