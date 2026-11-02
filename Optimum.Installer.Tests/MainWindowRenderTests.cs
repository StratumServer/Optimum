using Avalonia;
using Avalonia.Automation;
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

    [AvaloniaFact]
    public void PathEntryAndBrowseControlsFitInANarrowOptionsView()
    {
        var vm = new MainWindowViewModel(TestServices.Build());
        var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 720, Height = 800 };
        window.Show();
        vm.Prerequisites.ContinueCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        TextBox installPath = window.GetVisualDescendants().OfType<TextBox>()
            .Single(control => AutomationProperties.GetName(control) == "Install directory");
        Button browse = window.GetVisualDescendants().OfType<Button>()
            .Single(control => AutomationProperties.GetName(control) == "Browse for an install directory");
        installPath.Text = @"C:\Users\tester\AppData\Local\Optimum";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Point pathRight = installPath.TranslatePoint(new Point(installPath.Bounds.Width, 0), window)!.Value;
        Point browseLeft = browse.TranslatePoint(new Point(0, 0), window)!.Value;

        Assert.Equal(installPath.Text, vm.Options.InstallDirectory);
        Assert.True(installPath.Bounds.Width > 0);
        Assert.True(browse.Bounds.Width > 0);
        Assert.True(pathRight.X <= browseLeft.X);
    }

    [AvaloniaFact]
    public void BuildNoticeScrollsWithinTheReviewViewWhenTheWindowIsResized()
    {
        var vm = new MainWindowViewModel(TestServices.Build());
        var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 960, Height = 800 };
        try
        {
            window.Show();
            vm.CurrentScreen = WizardScreen.Review;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            window.Width = 720;
            window.Height = 620;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            ScrollViewer notice = window.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(control => AutomationProperties.GetName(control) == "Optimum build notice");
            CheckBox accept = window.GetVisualDescendants().OfType<CheckBox>()
                .Single(control => control.Content as string == "I have read and accept this notice");
            Point acceptBottom = accept.TranslatePoint(new Point(0, accept.Bounds.Height), window)!.Value;

            Assert.True(notice.Bounds.Height > 0);
            Assert.True(notice.Extent.Height > notice.Viewport.Height,
                $"Notice extent {notice.Extent.Height} should scroll within viewport {notice.Viewport.Height}.");
            Assert.True(acceptBottom.Y <= window.Bounds.Height,
                $"Acceptance checkbox bottom at {acceptBottom.Y}, window height {window.Bounds.Height}");
        }
        finally
        {
            window.Close();
        }
    }
}
