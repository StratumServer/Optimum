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
        // The Optimum mark: an oxidised-gold gear around a teal gem. The gem
        // colour is the accent the whole installer runs on; the gear gold is
        // the secondary.
        SukiTheme.GetInstance().ChangeColorTheme(new SukiColorTheme(
            "Optimum",
            primary: Color.Parse("#009E7F"),
            accent: Color.Parse("#C8A84B")));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new MainWindowViewModel(InstallerServices.CreateReal());
            shell.ExitRequested += () => desktop.Shutdown();
            desktop.MainWindow = new MainWindow { DataContext = shell };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
