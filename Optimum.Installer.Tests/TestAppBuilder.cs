using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;
using Optimum.Installer;
using Optimum.Installer.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Optimum.Installer.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
