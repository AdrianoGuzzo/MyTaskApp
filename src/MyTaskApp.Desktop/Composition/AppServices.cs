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

    public static ServiceProvider Build(
        IConfiguration configuration,
        SingleInstance instance,
        LaunchOptions launch) =>
        new ServiceCollection()
            .AddSingleton(configuration)

            // Registrado, e não estático: quem precisa dele é a App, depois da
            // janela existir, e o contêiner já é o caminho de tudo mais.
            .AddSingleton(instance)

            // Como o processo foi lançado (ADR-023). Quem lê é a App, para
            // decidir se a janela aparece ou se o app sobe direto na bandeja.
            .AddSingleton(launch)
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

            // Quem o app consegue identificar como autor das operacoes (§5, §8).
            // Registrado aqui, antes de AddApplication, porque o nome da conta
            // do sistema e conhecimento do host, nao da camada de aplicacao.
            .AddSingleton<ICurrentUser, CurrentWindowsUser>()

            // Perguntar antes de agir (§4, §7). Singleton sem estado: descobre
            // a janela dona a cada pergunta.
            .AddSingleton<IConfirmationDialog, ConfirmationDialog>()

            // Copiar o texto de uma linha (§12). Singleton sem estado, como o
            // dialogo: descobre a janela a cada escrita.
            .AddSingleton<IClipboardWriter, ClipboardWriter>()

            // Iniciar com o Windows (ADR-023). Registrado sem condicao, como o
            // WindowsSoundPlayer: a guarda de plataforma mora dentro dele, e
            // fora do Windows a resposta e "nao da" em vez de excecao.
            .AddSingleton<IStartupRegistration, WindowsStartupRegistration>()

            // Singletons: a janela de ajustes e a bandeja sao uma so por app.
            .AddSingleton<ReminderSettingsViewModel>()
            .AddSingleton<ReminderSettingsWindow>()

            // Arquivados, lixeira e retencao (§3, §5, §11). Tambem uma so:
            // reabrir a janela precisa mostrar o estado atual, nao uma segunda
            // copia com dados velhos.
            .AddSingleton<DataManagementViewModel>()
            .AddSingleton<DataManagementWindow>()

            // Abrir pasta, terminal e link fora do app (ADR-027). Sem estado,
            // como o clipboard: descobre a janela a cada pedido.
            .AddSingleton<IShellLauncher, ShellLauncher>()

            // Etiquetas (ADR-025): janela única, como a de gerenciamento de dados.
            .AddSingleton<TagsViewModel>()
            .AddSingleton<TagsWindow>()

            // Comandos globais (ADR-028): janela única, como a de etiquetas.
            .AddSingleton<DevelopmentCommandsViewModel>()
            .AddSingleton<DevelopmentCommandsWindow>()

            // A anotacao de um item (§12). Transient, e nao singleton como as
            // duas acima: sao duas telas diferentes para dois checklists
            // diferentes, e o truque do "X que esconde" so faz sentido para
            // quem tem uma instancia so.
            .AddTransient<TaskNotesViewModel>()

            // A aba Desenvolvimento de cada anotação (ADR-027): uma por janela,
            // como a própria anotação.
            .AddTransient<TaskDevelopmentViewModel>()

            // O card do agente de IA dentro dela (ADR-029): também um por janela.
            .AddTransient<AgentSessionViewModel>()
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .BuildServiceProvider(validateScopes: true);
}
