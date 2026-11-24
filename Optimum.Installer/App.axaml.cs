using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Optimum.Installer.Services;
using Optimum.Installer.ViewModels;
using Optimum.Installer.Views;

namespace Optimum.Installer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Native Fluent theme; the OS supplies the accent colour and the
        // light/dark variant. No runtime colour setup is needed.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = InstallerServices.CreateReal();
            desktop.Exit += (_, _) => services.SessionLog?.Dispose();

            var shell = new MainWindowViewModel(services);
            shell.ExitRequested += () => desktop.Shutdown();
            desktop.MainWindow = new MainWindow { DataContext = shell };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
