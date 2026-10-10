using Avalonia;
using Avalonia.Headless;
using MyTaskApp.Desktop;

[assembly: AvaloniaTestApplication(typeof(MyTaskApp.Desktop.Tests.TestAppBuilder))]

// O headless recria o Dispatcher.UIThread antes de cada [AvaloniaFact], na
// thread da sessão. Um [Fact] comum rodando ao mesmo tempo em outra thread que
// encoste no Dispatcher (direto ou por um ViewModel) fica dono dele, e o
// [AvaloniaFact] da vez cai no setup com "a different thread owns it" —
// intermitente no CI. Em série, ninguém disputa o Dispatcher.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MyTaskApp.Desktop.Tests;

internal static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
