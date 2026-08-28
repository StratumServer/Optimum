using Avalonia;
using Avalonia.Headless;
using Optimum.Installer;
using Optimum.Installer.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Optimum.Installer.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
