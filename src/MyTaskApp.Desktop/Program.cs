using System.Reflection;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Infrastructure.Persistence;
using Serilog;

namespace MyTaskApp.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Antes de tudo, e antes até do log: dois processos no mesmo SQLite é o
        // tipo de problema que não dá para consertar depois de acontecer. O
        // segundo lançamento não abre nada — pede para o primeiro aparecer e sai.
        using var instance = SingleInstance.Acquire();

        if (!instance.IsOwner)
        {
            instance.SignalOwner();
            return 0;
        }

        LoggingSetup.ConfigureBootstrap();

        try
        {
            // Dentro do try de propósito: appsettings.json é obrigatório, e uma
            // instalação incompleta precisa deixar rastro em vez de sumir.
            var configuration = AppServices.BuildConfiguration();

            LoggingSetup.Configure(configuration);

            using var services = AppServices.Build(configuration, instance);

            PrepareDatabase(services);

            Log.Information("ApplicationStarted {Version}", Version);

            App.Services = services;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            // Nada de stack trace na cara do usuário (§25): vai inteiro para o log.
            Log.Fatal(exception, "UnhandledException");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// A versão que o instalador gravou, vinda do assembly — a mesma que aparece
    /// em "Aplicativos Instalados". Primeira pergunta de todo diagnóstico.
    /// </summary>
    public static string Version =>
        typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "desconhecida";

    /// <summary>
    /// O banco nasce (ou evolui) antes da primeira tela aparecer, para que
    /// nenhuma consulta encontre um schema desatualizado. É aqui que uma
    /// atualização aplica as migrations novas — o instalador nunca toca no banco.
    /// </summary>
    private static void PrepareDatabase(IServiceProvider services)
    {
        using var scope = services.CreateScope();

        scope.ServiceProvider
            .GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync()
            .GetAwaiter()
            .GetResult();
    }

    // Usado pelo designer da Avalonia e pelo host de testes headless.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
