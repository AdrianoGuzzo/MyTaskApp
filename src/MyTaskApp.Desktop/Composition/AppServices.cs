using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Desktop.Reminders;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Desktop.Widget;
using MyTaskApp.Infrastructure;
using MyTaskApp.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Serilog;

namespace MyTaskApp.Desktop.Composition;

/// <summary>
/// Composition root: o único lugar que conhece implementações concretas.
/// </summary>
internal static class AppServices
{
    /// <summary>
    /// Duas camadas, e a ordem é a decisão: o arquivo da instalação é
    /// sobrescrito a cada atualização, então o que o usuário ajustou à mão tem
    /// de morar fora do diretório de instalação para sobreviver (ADR-018).
    /// </summary>
    public static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile(
                Path.Combine(UserDataLocation.Current.State, "appsettings.user.json"),
                optional: true,
                reloadOnChange: false)
            .Build();

    public static ServiceProvider Build(IConfiguration configuration, SingleInstance instance) =>
        new ServiceCollection()
            .AddSingleton(configuration)

            // Registrado, e não estático: quem precisa dele é a App, depois da
            // janela existir, e o contêiner já é o caminho de tudo mais.
            .AddSingleton(instance)
            .AddLogging(builder => builder.AddSerilog(Log.Logger))
            .AddSingleton<IUseCaseRunner, ScopedUseCaseRunner>()
            .AddSingleton<TodayViewModel>()

            // O Desktop tambem e camada adaptadora: implementa portas da
            // Application, como o ScopedUseCaseRunner ja fazia.
            .AddSingleton<AlertPresenter>()
            .AddSingleton<IAlertPresenter>(services => services.GetRequiredService<AlertPresenter>())
            .AddSingleton<ISoundPlayer, WindowsSoundPlayer>()
            .AddTransient<ReminderAlertViewModel>()
            .AddSingleton<TrayIconHost>()

            // Posicao e tamanho do painel: arquivo proprio, sem migracao.
            .AddSingleton<IWidgetStateStore, WidgetStateStore>()

            // Singletons: a janela de ajustes e a bandeja sao uma so por app.
            .AddSingleton<ReminderSettingsViewModel>()
            .AddSingleton<ReminderSettingsWindow>()
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .BuildServiceProvider(validateScopes: true);
}
