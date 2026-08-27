using Avalonia;
using Velopack;

namespace Optimum.Installer;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run before anything else: Velopack's install, update, and
        // uninstall hooks handle their argument and exit the process here.
        VelopackApp.Build().Run();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
