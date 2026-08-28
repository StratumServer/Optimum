using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Optimum.Installer.Services;
using Optimum.Installer.ViewModels;
using Optimum.Installer.Views;
using SukiUI;
using SukiUI.Models;

namespace Optimum.Installer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Cyan throughout: a deep cyan primary (buttons, step markers, links,
        // progress) and a brighter cyan accent. The window surfaces are set to
        // matching cyan-slate tones in App.axaml so nothing reads green or grey.
        SukiTheme.GetInstance().ChangeColorTheme(new SukiColorTheme(
            "Optimum",
            primary: Color.Parse("#0B7C97"),
            accent: Color.Parse("#22A5C2")));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new MainWindowViewModel(InstallerServices.CreateReal());
            shell.ExitRequested += () => desktop.Shutdown();
            desktop.MainWindow = new MainWindow { DataContext = shell };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
