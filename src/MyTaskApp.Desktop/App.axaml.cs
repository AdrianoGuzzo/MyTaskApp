using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Reminders;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Desktop.Widget;
using Serilog;

namespace MyTaskApp.Desktop;

public sealed partial class App : Avalonia.Application
{
    /// <summary>
    /// Preenchido pelo composition root antes da UI subir. Fica nulo no designer
    /// e no host headless, que não precisam do contêiner.
    /// </summary>
    internal static IServiceProvider? Services { get; set; }

    /// <summary>
    /// As anotações abertas, por checklist. Existe para o segundo clique no
    /// mesmo ícone trazer a janela de volta em vez de abrir uma cópia — duas
    /// telas do mesmo texto teriam duas versões dele, e a última a salvar
    /// apagaria a outra.
    /// </summary>
    private readonly Dictionary<Guid, TaskNotesWindow> _notes = [];

    private TrayIconHost? _tray;
    private MainWindow? _window;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            _window = window;

            if (Services is not null)
            {
                var todayViewModel = Services.GetRequiredService<TodayViewModel>();
                window.DataContext = todayViewModel;

                // Antes de aparecer: senão o painel pisca no meio da tela com
                // o tamanho padrão e só depois pula para onde o usuário o deixou.
                window.Attach(Services.GetRequiredService<IWidgetStateStore>());

                // Primeira carga dispara fora do caminho de inicialização da UI,
                // para a janela aparecer sem esperar o banco.
                _ = todayViewModel.LoadAsync(CancellationToken.None);

                SetUpTray(Services, desktop, window);
                StartReminders(Services, window, todayViewModel);
                SetUpDataManagement(Services, window, todayViewModel);
                SetUpNotes(Services, window, todayViewModel);
                SetUpTags(Services, window, todayViewModel);
                SetUpAgentSessions(Services, todayViewModel);
                ListenForSecondLaunch(Services, window);

                // A moldura só sabe iniciar com o Windows depois de conhecer o
                // registro — até aqui ela usa o objeto nulo (ADR-023).
                window.Chrome.UseStartup(Services.GetRequiredService<IStartupRegistration>());

                StartHiddenIfAsked(window, Services.GetRequiredService<LaunchOptions>());
            }

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Fechar a janela esconde o app em vez de encerrá-lo — é o que permite ele
    /// continuar lembrando. Se a bandeja não subir, o comportamento volta ao
    /// normal: senão não sobraria nenhuma forma de sair do app.
    /// </summary>
    private void SetUpTray(
        IServiceProvider services,
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window)
    {
        _tray = services.GetRequiredService<TrayIconHost>();

        var installed = _tray.TryInstall(this, new TrayActions(
            Open: () => OnUiThread(() => Reveal(window)),
            Settings: () => OnUiThread(() => ShowSettings(services, window)),
            Exit: () => OnUiThread(() => Exit(desktop)),
            ToggleTopmost: () => OnUiThread(window.Chrome.ToggleTopmost),
            ToggleGhost: () => OnUiThread(window.Chrome.ToggleGhost),
            UseCompact: () => OnUiThread(() => window.Chrome.UseMode("compact")),
            Hide: () => OnUiThread(window.HideAndRemember)));

        desktop.ShutdownRequested += (_, _) => Shutdown(services);

        if (!installed)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // O menu da bandeja e o menu do painel mostram o mesmo estado.
        _tray.ShowTopmost(window.Chrome.IsTopmost);
        _tray.ShowGhost(window.Chrome.IsGhost);

        window.Chrome.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WidgetChromeViewModel.IsTopmost))
            {
                _tray?.ShowTopmost(window.Chrome.IsTopmost);
            }

            // O pino liga o modo discreto junto, então um visto muda sem que
            // ninguém tenha clicado no outro.
            if (args.PropertyName == nameof(WidgetChromeViewModel.IsGhost))
            {
                _tray?.ShowGhost(window.Chrome.IsGhost);
            }
        };

        window.Closing += (_, args) =>
        {
            if (_exiting)
            {
                return;
            }

            args.Cancel = true;
            window.HideAndRemember();
        };
    }

    private void StartReminders(
        IServiceProvider services,
        MainWindow window,
        TodayViewModel todayViewModel)
    {
        var presenter = services.GetRequiredService<AlertPresenter>();

        presenter.MainWindow = window;

        todayViewModel.SettingsRequested += () => ShowSettings(services, window);
        todayViewModel.StartAutoRefresh();

        // O contador de pendências vira o balão do ícone: o aviso mais discreto
        // que existe, porque só aparece se o usuário for olhar.
        todayViewModel.PendingChanged += pending =>
            OnUiThread(() => _tray?.ShowPending(pending));

        // Reagir a um aviso muda o quadro: recarregar aqui faz o ⚠ sumir na
        // hora, sem o usuário precisar atualizar nada.
        presenter.Acted += () => Dispatcher.UIThread.Post(
            () => _ = todayViewModel.LoadAsync(CancellationToken.None));

        // Depois de a janela existir: o último degrau da escada precisa de algo
        // para trazer à frente. O primeiro tique é imediato, e é ele que
        // recupera o que venceu com o app fechado.
        services.GetRequiredService<ReminderScheduler>().Start();
    }

    /// <summary>
    /// Liga a janela de Arquivados/Lixeira e a varredura do ciclo de vida
    /// (§3, §5, §6).
    /// </summary>
    private static void SetUpDataManagement(
        IServiceProvider services,
        MainWindow window,
        TodayViewModel todayViewModel)
    {
        todayViewModel.DataManagementRequested += () => ShowDataManagement(services, window);

        // Restaurar da lixeira devolve um checklist à lista principal; sem isto
        // o painel só mostraria a volta dele no refresh de 60 s, e o usuário
        // ficaria olhando para uma tela que ainda não sabe o que ele acabou de
        // fazer.
        services.GetRequiredService<DataManagementViewModel>().ChecklistsChanged +=
            () => Dispatcher.UIThread.Post(
                () => _ = todayViewModel.LoadAsync(CancellationToken.None));

        // Primeiro tique imediato, como o dos lembretes: é ele que põe em dia o
        // que venceu enquanto o app esteve fechado.
        services.GetRequiredService<LifecycleMaintenanceScheduler>().Start();
    }

    /// <summary>
    /// Liga o menu "Etiquetas…" e o atalho do seletor da linha à janela de
    /// gerenciamento (ADR-025).
    /// </summary>
    private static void SetUpTags(
        IServiceProvider services,
        MainWindow window,
        TodayViewModel todayViewModel)
    {
        todayViewModel.TagsRequested += () => ShowTags(services, window);
        todayViewModel.CommandsRequested += () => ShowDevelopmentCommands(services, window);

        // Renomear, recolorir ou excluir muda as bolinhas de todo o painel; sem
        // isto a mudança só apareceria no refresh de 60 s.
        services.GetRequiredService<TagsViewModel>().Changed +=
            () => Dispatcher.UIThread.Post(
                () =>
                {
                    _ = todayViewModel.LoadAsync(CancellationToken.None);
                    _ = todayViewModel.RefreshCaptureTagsAsync(CancellationToken.None);
                });
    }

    /// <summary>
    /// Liga o monitor das sessões de agente (ADR-030). O primeiro tique
    /// reencontra os terminais que ficaram abertos com o app fechado; depois,
    /// cada fim de processo acende ou apaga o selo da linha e o card da tarefa
    /// sem esperar o refresh de 60 s.
    /// </summary>
    /// <remarks>
    /// A janela da tarefa é avisada por aqui, e não assinando o monitor: o
    /// ViewModel dela é transitório, e um singleton segurando o evento dele o
    /// manteria vivo depois de a janela fechar.
    /// </remarks>
    private void SetUpAgentSessions(IServiceProvider services, TodayViewModel todayViewModel)
    {
        var monitor = services.GetRequiredService<AgentSessionMonitor>();

        monitor.SessionsChanged += taskId => OnUiThread(() =>
        {
            _ = todayViewModel.LoadAsync(CancellationToken.None);

            if (_notes.TryGetValue(taskId, out var notes) && notes.DataContext is TaskNotesViewModel viewModel)
            {
                _ = viewModel.Development.Agent.RefreshAsync(CancellationToken.None);
            }
        });

        monitor.Start();
    }

    private static void ShowTags(IServiceProvider services, Window owner)
    {
        var window = services.GetRequiredService<TagsWindow>();

        window.Show(owner);
        window.Activate();
        window.Reveal();
    }

    /// <summary>
    /// A janela de comandos globais (ADR-028). Aberta pela aba Desenvolvimento
    /// e pelo menu; uma só, como a de etiquetas.
    /// </summary>
    private static void ShowDevelopmentCommands(IServiceProvider services, Window owner)
    {
        var window = services.GetRequiredService<DevelopmentCommandsWindow>();

        window.Show(owner);
        window.Activate();
        window.Reveal();
    }

    /// <summary>
    /// Liga o ícone de anotações da linha (§12) à janela que as edita.
    /// </summary>
    private void SetUpNotes(
        IServiceProvider services,
        MainWindow window,
        TodayViewModel todayViewModel)
    {
        todayViewModel.NotesRequested += row => ShowNotes(services, window, todayViewModel, row);
    }

    /// <summary>
    /// Abre — ou traz de volta — a anotação de um checklist. O ViewModel é
    /// resolvido por item, e não reaproveitado: ele carrega o texto de uma
    /// linha só, e reusá-lo obrigaria a lembrar de limpar tudo a cada abertura.
    /// </summary>
    private void ShowNotes(
        IServiceProvider services,
        Window owner,
        TodayViewModel todayViewModel,
        TaskRowViewModel row)
    {
        if (_notes.TryGetValue(row.TaskId, out var opened))
        {
            opened.Show();
            opened.Activate();
            return;
        }

        var viewModel = services.GetRequiredService<TaskNotesViewModel>();

        viewModel.Load(row);

        // Salvar muda o que a lista desenha: o ícone da linha acende, e no dia
        // em que o texto for apagado ele precisa apagar junto.
        viewModel.Saved += () => Dispatcher.UIThread.Post(
            () => _ = todayViewModel.LoadAsync(CancellationToken.None));

        var notes = new TaskNotesWindow(
            viewModel,
            services.GetRequiredService<IConfirmationDialog>());

        viewModel.Development.CommandsRequested += () => ShowDevelopmentCommands(services, notes);

        _notes[row.TaskId] = notes;
        notes.Closed += (_, _) => _notes.Remove(row.TaskId);

        notes.Show(owner);
        notes.Activate();
    }

    /// <summary>
    /// Clicar no atalho com o app já aberto traz o painel de volta em vez de
    /// não fazer nada (ADR-019). Importa justamente porque o app vive na
    /// bandeja: o usuário fecha a janela, acha que encerrou, e clica de novo.
    /// </summary>
    private static void ListenForSecondLaunch(IServiceProvider services, MainWindow window)
    {
        services.GetRequiredService<SingleInstance>().WhenActivated(() =>
            OnUiThread(() =>
            {
                Log.Information("SecondLaunchRevealedTheWindow");
                Reveal(window);
            }));
    }

    /// <summary>
    /// "Iniciar recolhido" na prática: o lifetime mostra a MainWindow sozinho,
    /// então o jeito de subir direto para a bandeja é esconder assim que ela
    /// abre.
    /// </summary>
    /// <remarks>
    /// Duas origens, e nenhuma manda na outra: a preferência do menu ("abrir
    /// recolhido da próxima vez") e o login do Windows, que sobe o app com
    /// <c>--startup</c> justamente para ele não aparecer na frente de ninguém
    /// no boot (ADR-023).
    /// </remarks>
    private static void StartHiddenIfAsked(MainWindow window, LaunchOptions launch)
    {
        if (!window.Chrome.StartHidden && !launch.StartedByWindows)
        {
            return;
        }

        void HideOnFirstOpen(object? sender, EventArgs args)
        {
            window.Opened -= HideOnFirstOpen;
            window.Hide();
        }

        window.Opened += HideOnFirstOpen;
    }

    private static void Reveal(Window window)
    {
        window.Show();

        // Ordem importa: Show() numa janela escondida enquanto minimizada a
        // deixa minimizada.
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static void ShowSettings(IServiceProvider services, Window owner)
    {
        var window = services.GetRequiredService<ReminderSettingsWindow>();

        window.Show(owner);
        window.Activate();
    }

    private static void ShowDataManagement(IServiceProvider services, Window owner)
    {
        var window = services.GetRequiredService<DataManagementWindow>();

        window.Show(owner);
        window.Activate();
    }

    private static void OnUiThread(Action action) => Dispatcher.UIThread.Post(action);

    private void Exit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _exiting = true;
        desktop.Shutdown();
    }

    /// <summary>Para o agendador e solta o ícone antes de o processo morrer.</summary>
    private void Shutdown(IServiceProvider services)
    {
        _window?.PersistNow();

        services.GetRequiredService<ReminderScheduler>().Dispose();

        // Também aqui: um tique de manutenção em voo precisa terminar antes de
        // o banco ser solto, senão um lote pela metade seria interrompido.
        services.GetRequiredService<LifecycleMaintenanceScheduler>().Dispose();

        // Só para de vigiar: o Claude continua aberto no terminal, e a próxima
        // abertura o reencontra pelo PID (ADR-030).
        services.GetRequiredService<AgentSessionMonitor>().Dispose();

        _tray?.Dispose();
        _tray = null;
    }
}
