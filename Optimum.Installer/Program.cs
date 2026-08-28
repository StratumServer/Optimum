using Avalonia;
using Avalonia.Media;
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
            .With(new FontManagerOptions
            {
                // Figtree is also the app-wide default so any text outside a
                // SukiUI template (tooltips, flyouts) matches.
                DefaultFamilyName = "avares://Optimum.Installer/Assets/Fonts/Figtree/Figtree-VF.ttf#Figtree",
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily("Segoe UI") }],
            })
            .LogToTrace();
}
