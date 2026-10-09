using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Desktop.Reminders;
using MyTaskApp.Desktop.SpellChecking;
using MyTaskApp.Desktop.StickyNotes;
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

            // O relógio do cronômetro (ADR-052): um para o app, que a lista, a
            // faixa do HUD e a aba "Tempo" de cada janela leem.
            .AddSingleton<ActiveTimerViewModel>()
            .AddSingleton<TodayViewModel>()

            // O Desktop tambem e camada adaptadora: implementa portas da
            // Application, como o ScopedUseCaseRunner ja fazia.
            .AddSingleton<AlertPresenter>()
            .AddSingleton<IAlertPresenter>(services => services.GetRequiredService<AlertPresenter>())

            // O aviso do agente de IA (ADR-037) sai pela mesma pilha de janelas.
            .AddSingleton<IAgentAttentionPresenter>(services => services.GetRequiredService<AlertPresenter>())
            .AddTransient<AgentAlertViewModel>()

            // E o aviso de prazo (ADR-050), pela mesma pilha. Antes de
            // AddApplication, ganha do objeto nulo.
            .AddSingleton<IDeadlineAlertPresenter>(services => services.GetRequiredService<AlertPresenter>())
            .AddTransient<DeadlineAlertViewModel>()
            .AddSingleton<ISoundPlayer, WindowsSoundPlayer>()

            // Os sons dos avisos do agente (ADR-042): arquivo, e não bipe.
            // Registrado antes de AddApplication, ganha do objeto nulo.
            .AddSingleton<IAudioPlayer, WindowsAudioPlayer>()
            .AddTransient<ReminderAlertViewModel>()
            .AddSingleton<TrayIconHost>()

            // Posicao e tamanho do painel: arquivo proprio, sem migracao.
            .AddSingleton<IWidgetStateStore, WidgetStateStore>()

            // O que só o sistema faz com a janela do HUD (ADR-048): recortar a
            // região clicável e o atalho global. Fora do Windows, objetos
            // nulos — o HUD funciona igual, sem o recorte dos cantos e sem atalho.
            .AddSingleton<IWindowBehaviorService>(services => OperatingSystem.IsWindows()
                ? ActivatorUtilities.CreateInstance<WindowsWindowBehavior>(services)
                : PortableWindowBehavior.Instance)
            .AddSingleton<IGlobalHotkeyFactory>(services => OperatingSystem.IsWindows()
                ? ActivatorUtilities.CreateInstance<WindowsGlobalHotkeyFactory>(services)
                : UnsupportedGlobalHotkeyFactory.Instance)
            .AddSingleton<IGlobalHotkeyService>(services =>
                services.GetRequiredService<IGlobalHotkeyFactory>().Create(HotkeyGesture.ToggleHud))

            // Quem o app consegue identificar como autor das operacoes (§5, §8).
            // Registrado aqui, antes de AddApplication, porque o nome da conta
            // do sistema e conhecimento do host, nao da camada de aplicacao.
            .AddSingleton<ICurrentUser, CurrentWindowsUser>()

            // Perguntar antes de agir (§4, §7). Singleton sem estado: descobre
            // a janela dona a cada pergunta.
            .AddSingleton<IConfirmationDialog, ConfirmationDialog>()

            // Os parâmetros e a confirmação de um comando rápido (ADR-051).
            // Sem estado, como a confirmação: descobre a janela a cada pergunta.
            .AddSingleton<IQuickCommandPrompt, QuickCommandPrompt>()

            // Lançar e corrigir um período de trabalho (ADR-052), pelo mesmo molde.
            .AddSingleton<ITimeEntryEditor, TimeEntryEditor>()

            // O comando só do diretório da etiqueta (ADR-055), pelo mesmo molde.
            .AddSingleton<IDirectoryCommandEditor, DirectoryCommandEditor>()

            // Copiar o texto de uma linha (§12). Singleton sem estado, como o
            // dialogo: descobre a janela a cada escrita.
            .AddSingleton<IClipboardWriter, ClipboardWriter>()

            // Corretor ortografico (ADR-032). A escolha e aqui, e nao dentro
            // dele: fora do Windows, ou sem dicionario instalado, as caixas
            // recebem o objeto nulo e nao sublinham nada.
            .AddSingleton<ISpellChecker>(services => CreateSpellChecker(
                services.GetRequiredService<ILoggerFactory>().CreateLogger("MyTaskApp.Desktop.SpellChecking")))

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

            // Integrações — o Jira (ADR-045): janela única, como as outras do menu.
            .AddSingleton<IntegrationsViewModel>()
            .AddSingleton<IntegrationsWindow>()

            // Bancos de dados (ADR-056): janela única, com uma aba por ViewModel.
            .AddSingleton<DatabaseConnectionsViewModel>()
            .AddSingleton<DatabaseDiagnosticsViewModel>()
            .AddSingleton<DatabaseCopyViewModel>()
            .AddSingleton<DatabaseProfilesViewModel>()
            .AddSingleton<DatabaseHistoryViewModel>()
            .AddSingleton<DatabaseOperationsViewModel>()
            .AddSingleton<DatabaseOperationsWindow>()

            // Sons dos avisos do agente (ADR-042): janela única, como as outras.
            .AddSingleton<AgentAlertSoundsViewModel>()
            .AddSingleton<AgentAlertSoundsWindow>()

            // Post-its (ADR-054): um dono só das janelas abertas — uma por post-it —
            // e a lista, janela única como as outras do menu.
            .AddSingleton<StickyNoteWindowManager>()
            .AddSingleton<StickyNotesViewModel>()
            .AddSingleton<StickyNotesWindow>()

            // A anotacao de um item (§12). Transient, e nao singleton como as
            // duas acima: sao duas telas diferentes para dois checklists
            // diferentes, e o truque do "X que esconde" so faz sentido para
            // quem tem uma instancia so.
            .AddTransient<TaskNotesViewModel>()

            // O cartão da issue do Jira na janela da tarefa (ADR-045): um por janela.
            .AddTransient<TaskIssueViewModel>()

            // O card do prazo na janela da tarefa (ADR-050): um por janela.
            .AddTransient<TaskDeadlineViewModel>()

            // A aba "Tempo" da janela da tarefa (ADR-052): uma por janela.
            .AddTransient<TaskTimeLogViewModel>()

            // A aba Desenvolvimento de cada anotação (ADR-027): uma por janela,
            // como a própria anotação, com um ambiente por repositório (ADR-031).
            .AddTransient<TaskDevelopmentsViewModel>()
            .AddTransient<TaskDevelopmentViewModel>()
            .AddTransient<Func<TaskDevelopmentViewModel>>(provider =>
                () => provider.GetRequiredService<TaskDevelopmentViewModel>())

            // O card do agente de IA dentro dela (ADR-030): também um por janela.
            .AddTransient<AgentSessionViewModel>()

            // A seção "⚡ Comandos" de cada ambiente (ADR-051): uma por ambiente.
            .AddTransient<QuickCommandsViewModel>()
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .BuildServiceProvider(validateScopes: true);

    private static ISpellChecker CreateSpellChecker(Microsoft.Extensions.Logging.ILogger logger) =>
        OperatingSystem.IsWindows()
            ? WindowsSpellChecker.TryCreate(logger) ?? (ISpellChecker)NoSpellChecker.Instance
            : NoSpellChecker.Instance;
}
