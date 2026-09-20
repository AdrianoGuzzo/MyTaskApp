using Avalonia;
using Avalonia.Headless;
using MyTaskApp.Desktop;

[assembly: AvaloniaTestApplication(typeof(MyTaskApp.Desktop.Tests.TestAppBuilder))]

namespace MyTaskApp.Desktop.Tests;

internal static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
