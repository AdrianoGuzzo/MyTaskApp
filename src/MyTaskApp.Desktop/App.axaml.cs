using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.LocalPeers;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Reminders;
using MyTaskApp.Desktop.SpellChecking;
using MyTaskApp.Desktop.StickyNotes;
using MyTaskApp.Desktop.Theming;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Desktop.Widget;
using MyTaskApp.Mcp.Hosting;
using Serilog;

namespace MyTaskApp.Desktop;

public sealed partial class App : Avalonia.Application
{
    /// <summary>
    /// Preenchido pelo composition root antes da UI subir. Fica nulo no designer
    /// e no host headless, que não precisam do contêiner.
    /// </summary>
    internal static IServiceProvider? Services { get; set; }

    /// <summary>O encerramento fora da thread da UI, que o <see cref="Program"/> espera antes de descartar o contêiner.</summary>
    internal static PendingShutdown ShutdownInProgress { get; } = new();

    /// <summary>
    /// As anotações abertas, por checklist. Existe para o segundo clique no
    /// mesmo ícone trazer a janela de volta em vez de abrir uma cópia — duas
    /// telas do mesmo texto teriam duas versões dele, e a última a salvar
    /// apagaria a outra.
    /// </summary>
    private readonly Dictionary<Guid, TaskNotesWindow> _notes = [];

    private ThemeController? _themes;
    private TrayIconHost? _tray;
    private MainWindow? _window;

    /// <summary>"Janela e comportamento" (ADR-048): uma só, recriada se fechada.</summary>
    private WindowSettingsWindow? _windowSettings;

    /// <summary>Quem pinta o app. Exposto para os testes headless trocarem o tema.</summary>
    internal ThemeController? Themes => _themes;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Antes de qualquer janela e em qualquer lifetime: as cores só existem
        // depois disto, e o host headless dos testes também precisa delas.
        _themes = new ThemeController(this);
        _themes.Use(ThemeCatalog.SystemId);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Antes da primeira janela: cada caixa com SpellCheck.IsEnabled
            // pega o corretor quando o XAML é carregado (ADR-032).
            if (Services is not null)
            {
                SpellCheck.Checker = Services.GetRequiredService<ISpellChecker>();
            }

            var window = new MainWindow();
            _window = window;

            if (Services is not null)
            {
                var todayViewModel = Services.GetRequiredService<TodayViewModel>();
                window.DataContext = todayViewModel;

                // Antes de aparecer: senão o painel pisca no meio da tela com
                // o tamanho padrão e só depois pula para onde o usuário o deixou.
                window.Attach(
                    Services.GetRequiredService<IWidgetStateStore>(),
                    Services.GetRequiredService<IWindowBehaviorService>(),
                    Services.GetRequiredService<IGlobalHotkeyService>(),
                    Services.GetRequiredService<IGlobalHotkeyFactory>());

                // "Sair" de dentro da janela (o X com "fechar o aplicativo", o
                // menu do HUD) passa pelo mesmo encerramento da bandeja.
                window.ExitHandler = () => Exit(Services, desktop);
                window.Chrome.WindowSettingsRequested += () => ShowWindowSettings(window);

                // Primeira carga dispara fora do caminho de inicialização da UI,
                // para a janela aparecer sem esperar o banco.
                _ = todayViewModel.LoadAsync(CancellationToken.None);

                SetUpTray(Services, desktop, window);
                StartReminders(Services, window, todayViewModel);
                SetUpDataManagement(Services, window, todayViewModel);
                SetUpNotes(Services, window, todayViewModel);
                SetUpTags(Services, window, todayViewModel);
                SetUpStickyNotes(Services, window, todayViewModel);
                SetUpIntegrations(Services, todayViewModel);
                SetUpAgentSessions(Services, todayViewModel);
                SetUpCommandExecutions(Services);
                SetUpTimeTracking(todayViewModel);
                SetUpHoursReport(Services, window, todayViewModel);
                SetUpMcpServer(Services, todayViewModel);
                SetUpSync(Services, todayViewModel);
                ListenForSecondLaunch(Services, window);

                // A moldura só sabe iniciar com o Windows depois de conhecer o
                // registro — até aqui ela usa o objeto nulo (ADR-023).
                window.Chrome.UseStartup(Services.GetRequiredService<IStartupRegistration>());

                StartHiddenIfAsked(window, Services.GetRequiredService<LaunchOptions>());
            }

            FollowTheme(window.Chrome);

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// A escolha mora na moldura, que a lê do <c>widget.json</c> e a grava de
    /// volta; aqui ela só vira cor. Antes de a janela aparecer, para o painel
    /// não abrir num tema e piscar para outro.
    /// </summary>
    private void FollowTheme(WidgetChromeViewModel chrome)
    {
        _themes?.Use(chrome.ThemeId);

        chrome.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WidgetChromeViewModel.ThemeId))
            {
                _themes?.Use(chrome.ThemeId);
            }
        };
    }

    /// <summary>
    /// A bandeja é o que permite fechar a janela sem encerrar o app — e o que
    /// o X faz é escolha do usuário (ADR-048), decidida pela própria janela.
    /// Se a bandeja não subir, a moldura fica sabendo: "ocultar" sem ícone
    /// não teria volta, e vira sair.
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
            Exit: () => OnUiThread(() => Exit(services, desktop)),
            ToggleTopmost: () => OnUiThread(window.Chrome.ToggleTopmost),
            ToggleHud: () => OnUiThread(() =>
            {
                Reveal(window);
                window.Chrome.ToggleHud();
            }),
            WindowSettings: () => OnUiThread(() => ShowWindowSettings(window)),
            UseCompact: () => OnUiThread(() => window.Chrome.UseMode("compact")),
            Hide: () => OnUiThread(window.HideAndRemember),
            NewStickyNote: () => OnUiThread(() => _ = services.GetRequiredService<StickyNoteWindowManager>().CreateNewAsync()),
            StickyNotes: () => OnUiThread(() => ShowStickyNotes(services, window, StickyNoteScope.Active)),
            McpServer: () => OnUiThread(() => ShowMcpServer(services, window))));

        window.Chrome.TrayAvailable = installed;

        if (!installed)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // O menu da bandeja e o menu do painel mostram o mesmo estado.
        _tray.ShowTopmost(window.Chrome.IsTopmost);
        _tray.ShowHud(window.Chrome.IsHud);

        window.Chrome.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WidgetChromeViewModel.IsTopmost))
            {
                _tray?.ShowTopmost(window.Chrome.IsTopmost);
            }

            if (args.PropertyName == nameof(WidgetChromeViewModel.WindowMode))
            {
                _tray?.ShowHud(window.Chrome.IsHud);
            }
        };
    }

    /// <summary>
    /// "Janela e comportamento…", aberta pelo menu do painel, do HUD e da
    /// bandeja. Liga direto na moldura: nada a carregar, nada a salvar.
    /// </summary>
    private void ShowWindowSettings(MainWindow owner)
    {
        if (_windowSettings is null)
        {
            _windowSettings = new WindowSettingsWindow(owner.Chrome);
            _windowSettings.Closed += (_, _) => _windowSettings = null;
        }

        if (!owner.IsVisible)
        {
            Reveal(owner);
        }

        _windowSettings.Show(owner);
        _windowSettings.Activate();
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

        // A limpeza de worktrees (ADR-065) apaga a bolinha e o "Worktree criado"
        // das linhas: a lista precisa saber agora, e não no próximo refresh.
        services.GetRequiredService<WorktreeCleanupViewModel>().WorktreesChanged +=
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
        todayViewModel.SoundsRequested += () => ShowAgentAlertSounds(services, window);
        todayViewModel.IntegrationsRequested += () => ShowIntegrations(services, window);
        todayViewModel.DatabaseOperationsRequested += () => ShowDatabaseOperations(services, window);
        todayViewModel.McpServerRequested += () => ShowMcpServer(services, window);
        todayViewModel.SyncRequested += () => ShowSync(services, window);
        todayViewModel.MachinesRequested += () => ShowMachines(services, window);

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
    /// Post-its (ADR-054): os fixados voltam para a tela junto com o app, e um
    /// novo nasce na tela onde o widget está.
    /// </summary>
    private void SetUpStickyNotes(
        IServiceProvider services,
        MainWindow window,
        TodayViewModel todayViewModel)
    {
        var notes = services.GetRequiredService<StickyNoteWindowManager>();

        // A cor do post-it é calculada sobre o tema: trocar o tema repinta.
        notes.Themes = _themes;

        // Recolorir ou excluir uma etiqueta muda quem usa a cor dela.
        services.GetRequiredService<TagsViewModel>().Changed +=
            () => Dispatcher.UIThread.Post(() => _ = notes.RefreshAllAsync());

        // Escondido na bandeja, o widget não diz nada sobre onde o usuário está.
        notes.Anchor = () => window.IsVisible ? window.Position : null;
        notes.Failed += message => Log.Warning("StickyNoteFailed {Message}", message);

        todayViewModel.NewStickyNoteRequested += () => _ = notes.CreateNewAsync();

        // Ctrl+Alt+N de qualquer lugar, com o app até escondido na bandeja.
        window.NewNoteHotkeyPressed += () => _ = notes.CreateNewAsync();

        // Post-it → tarefa: a tarefa nasce para hoje e tem de aparecer já, sem
        // esperar o refresh de 60 s.
        notes.TaskCreated += created => _ = todayViewModel.LoadAsync(CancellationToken.None);

        var list = services.GetRequiredService<StickyNotesViewModel>();
        list.TaskCreated += created => _ = todayViewModel.LoadAsync(CancellationToken.None);

        todayViewModel.StickyNotesRequested += scope => ShowStickyNotes(services, window, scope);

        _ = notes.OpenStartupNotesAsync();
    }

    /// <summary>
    /// O autocomplete do Jira na captura (ADR-045) só pergunta com o Jira
    /// conectado. A primeira conferência é sem esperar, como a carga do quadro;
    /// depois, a janela de Integrações avisa quando a conexão muda.
    /// </summary>
    private static void SetUpIntegrations(IServiceProvider services, TodayViewModel todayViewModel)
    {
        _ = todayViewModel.RefreshIssueSearchAsync(CancellationToken.None);

        services.GetRequiredService<IntegrationsViewModel>().ConnectionChanged +=
            () => Dispatcher.UIThread.Post(() => _ = todayViewModel.RefreshIssueSearchAsync(CancellationToken.None));
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
                _ = viewModel.Developments.RefreshAgentsAsync();

                // A consulta aberta ou encerrada redesenha a linha de estado da aba (ADR-081).
                if (viewModel.Consultation is { } consultation)
                {
                    _ = consultation.RefreshSessionsAsync();
                }
            }
        });

        monitor.Start();

        // A porta local dos hooks (ADR-037). Antes de qualquer "Iniciar": sem
        // ela, o agente abre sem acompanhamento. Os Claude que ficaram abertos
        // com o app fechado voltam a ser ouvidos a partir daqui.
        services.GetRequiredService<IAgentEventEndpoint>().Start();

        // A conversa sem terminal (ADR-092): o agente grava no mesmo banco e
        // avisa pelo canal local. O selo da lista e o card da tarefa mudam na
        // hora, e a conversa aberta de longe ganha um aviso no canto.
        services.GetRequiredService<IDataChangeNotifier>().Changed += areas =>
        {
            if (areas.HasFlag(DataArea.AgentSessions))
            {
                OnUiThread(() => RefreshAgents(todayViewModel));
            }
        };

        services.GetRequiredService<ManagedConversationOpener>().ControlOpened +=
            (_, control) => new RemoteControlWindow(control).Show();

        services.GetRequiredService<RemoteSessionAnnouncer>().Start();
    }

    /// <summary>O quadro e o card do agente das janelas de tarefa abertas, relidos.</summary>
    private void RefreshAgents(TodayViewModel todayViewModel)
    {
        _ = todayViewModel.LoadAsync(CancellationToken.None);

        foreach (var notes in _notes.Values)
        {
            if (notes.DataContext is TaskNotesViewModel viewModel)
            {
                _ = viewModel.Developments.RefreshAgentsAsync();
            }
        }
    }

    /// <summary>
    /// Os comandos rápidos em terminal (ADR-051): quando um fecha, a janela da
    /// tarefa, se aberta, atualiza o botão. Pelo mesmo motivo do agente, quem
    /// assina o monitor é a <see cref="App"/>, e não o ViewModel da janela.
    /// </summary>
    /// <summary>
    /// O cronômetro começou, parou ou trocou de tarefa (ADR-052) — pela lista,
    /// pela faixa ou por outra janela: a aba "Tempo" das tarefas envolvidas, se
    /// abertas, recarrega o histórico. Pela <see cref="App"/>, e não assinando o
    /// relógio no ViewModel da janela, pelo mesmo motivo dos monitores.
    /// </summary>
    private void SetUpTimeTracking(TodayViewModel todayViewModel)
    {
        todayViewModel.ActiveTimer.Changed += (previous, current) => OnUiThread(() =>
        {
            foreach (var taskId in new[] { previous?.TaskId, current?.TaskId }.OfType<Guid>().Distinct())
            {
                if (_notes.TryGetValue(taskId, out var notes)
                    && notes.DataContext is TaskNotesViewModel { Time: { } time })
                {
                    _ = time.ActivateAsync(CancellationToken.None);
                }
            }
        });
    }

    /// <summary>
    /// O servidor MCP local (ADR-059). Sobe sozinho só se habilitado e com
    /// "iniciar com o app", fora da thread da tela. O que a IA grava aparece na
    /// hora: o gateway avisa, e aqui o aviso vira a mesma recarga que a tela
    /// faria — o quadro, a aba "Tempo" das janelas abertas e a de Bancos.
    /// </summary>
    /// <summary>
    /// A sincronização (ADR-061) em segundo plano. Sem servidor configurado ou
    /// sem conta ligada, o laço acorda, vê que não há o que fazer e dorme: o
    /// app continua inteiro, só local.
    /// </summary>
    /// <remarks>
    /// A bolinha do cabeçalho (ADR-063) assina o agendador antes do Start:
    /// o primeiro ciclo, logo na abertura, já aparece nela.
    /// </remarks>
    private static void SetUpSync(IServiceProvider services, TodayViewModel todayViewModel)
    {
        var indicator = services.GetRequiredService<SyncIndicatorViewModel>();
        todayViewModel.SyncIndicator = indicator;
        _ = indicator.LoadAsync(CancellationToken.None);

        services.GetRequiredService<SyncScheduler>().Start();

        // O sinal de vida do controle remoto (ADR-087): com ele desligado, o tique só lê a configuração.
        services.GetRequiredService<RemotePresenceScheduler>().Start();

        // Com o agente rodando (ADR-085), o que um grava o outro mostra, e quem tem o lease sincroniza.
        services.GetRequiredService<LocalPeerBridge>().Start();

        // Aceitando controle, o agente fica no logon e, se tiver caído, sobe de novo agora.
        _ = services.GetRequiredService<IUseCaseRunner>().RunAsync<ApplyAgentAutostartHandler>(
            (handler, token) => handler.HandleAsync(new ApplyAgentAutostart(), token),
            CancellationToken.None);
    }

    private void SetUpMcpServer(IServiceProvider services, TodayViewModel todayViewModel)
    {
        services.GetRequiredService<IDataChangeNotifier>().Changed += areas => OnUiThread(() =>
        {
            // Etiquetas e post-its que chegam de outro computador (ADR-061):
            // as bolinhas da lista e os post-its abertos se redesenham.
            if (areas.HasFlag(DataArea.Tags))
            {
                _ = todayViewModel.LoadAsync(CancellationToken.None);
                _ = services.GetRequiredService<StickyNoteWindowManager>().RefreshAllAsync();

                if (services.GetRequiredService<TagsWindow>() is { IsVisible: true } tags)
                {
                    tags.Reveal();
                }
            }

            if (areas.HasFlag(DataArea.StickyNotes))
            {
                _ = services.GetRequiredService<StickyNoteWindowManager>().RefreshAllAsync();
            }

            if (areas.HasFlag(DataArea.Tasks) || areas.HasFlag(DataArea.Time))
            {
                _ = todayViewModel.LoadAsync(CancellationToken.None);

                foreach (var notes in _notes.Values)
                {
                    if (notes.DataContext is TaskNotesViewModel { Time: { } time })
                    {
                        _ = time.ActivateAsync(CancellationToken.None);
                    }
                }
            }

            if (areas.HasFlag(DataArea.Databases)
                && services.GetRequiredService<DatabaseOperationsWindow>() is { IsVisible: true } databases)
            {
                databases.Reveal();
            }
        });

        var manager = services.GetRequiredService<IMcpServerManager>();

        _ = Task.Run(async () =>
        {
            try
            {
                var status = await manager.StartIfConfiguredAsync();

                if (status.State is McpServerState.Error)
                {
                    Log.Warning("McpServerAutoStartFailed {Error}", status.Error);
                }
            }
            catch (Exception exception)
            {
                // Um servidor que não subiu não derruba o app: a tela dele mostra o erro.
                Log.Warning(exception, "McpServerAutoStartFailed");
            }
        });
    }

    private void SetUpCommandExecutions(IServiceProvider services)
    {
        var monitor = services.GetRequiredService<CommandExecutionMonitor>();

        monitor.ExecutionsChanged += taskId => OnUiThread(() =>
        {
            if (_notes.TryGetValue(taskId, out var notes) && notes.DataContext is TaskNotesViewModel viewModel)
            {
                _ = viewModel.Developments.RefreshQuickCommandsAsync();
            }
        });

        // Reencontra os terminais que ficaram abertos com o app fechado.
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

    /// <summary>A janela de integrações — o Jira (ADR-045), aberta pelo menu.</summary>
    private static void ShowIntegrations(IServiceProvider services, Window owner)
    {
        var window = services.GetRequiredService<IntegrationsWindow>();

        window.Show(owner);
        window.Activate();
        window.Reveal();
    }

    /// <summary>A janela de bancos de dados (ADR-056), aberta pelo menu.</summary>
    private static void ShowDatabaseOperations(IServiceProvider services, Window owner)
    {
        var window = services.GetRequiredService<DatabaseOperationsWindow>();

        window.Show(owner);
        window.Activate();
        window.Reveal();
    }

    /// <summary>A janela do servidor MCP (ADR-059), aberta pelo menu e pela bandeja.</summary>
    private static void ShowMcpServer(IServiceProvider services, Window owner)
    {
        var window = services.GetRequiredService<McpServerWindow>();

        window.Show(owner);
        window.Activate();
        window.Reveal();
    }

    /// <summary>A janela da sincronização (ADR-061), aberta pelo menu.</summary>
    private static void ShowSync(IServiceProvider services, Window owner)
    {
        var window = services.GetRequiredService<SyncWindow>();

        window.Show(owner);
        window.Activate();
        window.Reveal();
    }

    /// <summary>A janela "Máquinas" (ADR-087), aberta pelo menu.</summary>
    private static void ShowMachines(IServiceProvider services, Window owner)
    {
        var window = services.GetRequiredService<MachinesWindow>();

        window.Show(owner);
        window.Activate();
        window.Reveal();
    }

    /// <summary>
    /// O relatório de horas (ADR-097): pelo menu, pelo Ctrl+Shift+H e pelo
    /// "Ver relatório completo" do histórico de 7 dias. Aberto, ele acompanha
    /// cada recarga da lista — que já vem depois de toda mudança, daqui ou de
    /// fora, e do refresh periódico, que mantém o cronômetro em dia.
    /// </summary>
    private static void SetUpHoursReport(IServiceProvider services, MainWindow window, TodayViewModel todayViewModel)
    {
        var report = services.GetRequiredService<HoursReportViewModel>();
        report.FollowDevices(todayViewModel.Devices);

        todayViewModel.HoursReportRequested += () => ShowHoursReport(services, window);
        todayViewModel.History.ReportRequested += () => ShowHoursReport(services, window);
        todayViewModel.BoardLoaded += () => _ = report.RefreshAsync(CancellationToken.None);
    }

    /// <summary>A janela "Relatório de horas" (ADR-097).</summary>
    private static void ShowHoursReport(IServiceProvider services, Window owner)
    {
        var window = services.GetRequiredService<HoursReportWindow>();

        window.Show(owner);
        window.Activate();
        window.Reveal();
    }

    /// <summary>A janela de sons dos avisos do agente (ADR-042), aberta pelo menu.</summary>
    private static void ShowAgentAlertSounds(IServiceProvider services, Window owner)
    {
        var window = services.GetRequiredService<AgentAlertSoundsWindow>();

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
        todayViewModel.DeadlineEditorRequested += row => EditDeadline(services, window, todayViewModel, row);
        services.GetRequiredService<AlertPresenter>().OpenRequested += occurrenceId =>
            OpenFromDeadlineAlert(services, window, todayViewModel, occurrenceId);
        todayViewModel.History.OpenRequested += row => OpenFromHistory(services, window, todayViewModel, row);
    }

    /// <summary>
    /// Abre — ou traz de volta, mesmo minimizada — a anotação de um checklist.
    /// O ViewModel é resolvido por item, e não reaproveitado: ele carrega o
    /// texto de uma linha só, e reusá-lo obrigaria a lembrar de limpar tudo a
    /// cada abertura.
    /// </summary>
    private TaskNotesWindow ShowNotes(
        IServiceProvider services,
        Window owner,
        TodayViewModel todayViewModel,
        TaskRowViewModel row)
    {
        if (_notes.TryGetValue(row.TaskId, out var opened))
        {
            opened.Reveal();
            return opened;
        }

        var viewModel = services.GetRequiredService<TaskNotesViewModel>();

        viewModel.Load(row);

        // O que muda nesta janela muda o que a linha desenha: a lista recarrega
        // na thread da UI.
        void ReloadToday() => Dispatcher.UIThread.Post(() => _ = todayViewModel.LoadAsync(CancellationToken.None));

        // Salvar muda o que a lista desenha: o ícone da linha acende, e no dia
        // em que o texto for apagado ele precisa apagar junto.
        viewModel.Saved += ReloadToday;

        var notes = new TaskNotesWindow(
            viewModel,
            services.GetRequiredService<IConfirmationDialog>());

        viewModel.Developments.CommandsRequested += () => ShowDevelopmentCommands(services, notes);
        viewModel.Developments.DatabaseOperationsRequested += () => ShowDatabaseOperations(services, notes);

        if (viewModel.Developments.RemotePrepare is { } prepare)
        {
            prepare.ControlOpened += (_, control) => new RemoteControlWindow(control).Show(notes);
        }

        // A Consulta Inteligente (ADR-081) leva às mesmas janelas: etiquetas,
        // bancos e o servidor MCP que as ferramentas de banco usam.
        if (viewModel.Consultation is { } consultation)
        {
            consultation.TagsRequested += () => ShowTags(services, notes);
            consultation.DatabaseOperationsRequested += () => ShowDatabaseOperations(services, notes);
            consultation.McpServerRequested += () => ShowMcpServer(services, notes);
        }

        // Vincular, atualizar ou desvincular muda a chave que a linha desenha.
        if (viewModel.Issue is { } issue)
        {
            issue.Changed += ReloadToday;
        }

        // O prazo, os avisos e a próxima ação mudam o que a linha desenha (ADR-050).
        if (viewModel.Deadline is { } deadline)
        {
            deadline.Changed += ReloadToday;
        }

        // O tempo lançado e o cronômetro mudam o total e o relógio da linha (ADR-052).
        if (viewModel.Time is { } time)
        {
            time.Changed += ReloadToday;
        }

        // Mandar para outro computador tira a linha daqui; abrir a que chegou apaga o destaque (ADR-062).
        if (viewModel.Placement is { } placement)
        {
            placement.Changed += ReloadToday;
        }

        // As etiquetas mudam as bolinhas da linha (ADR-025).
        if (viewModel.Tags is { } tags)
        {
            tags.Changed += ReloadToday;
            tags.ManageRequested += () => ShowTags(services, notes);
        }

        _notes[row.TaskId] = notes;
        notes.Closed += (_, _) =>
        {
            _notes.Remove(row.TaskId);

            // O relógio é do app: sem soltar, ele seguraria esta aba viva.
            viewModel.Time?.Detach();

            // A pasta digitada e a PR não têm mais para quem mostrar.
            viewModel.Developments.Detach();
        };

        notes.Show(owner);
        notes.Activate();

        return notes;
    }

    /// <summary>
    /// "Personalizado…" no menu da linha (ADR-050): a tarefa abre já com o
    /// card do prazo escolhendo dia e hora.
    /// </summary>
    private void EditDeadline(
        IServiceProvider services,
        Window owner,
        TodayViewModel todayViewModel,
        TaskRowViewModel row)
    {
        var notes = ShowNotes(services, owner, todayViewModel, row);

        if (notes.DataContext is TaskNotesViewModel { Deadline: { } deadline })
        {
            deadline.BeginEdit();
        }
    }

    /// <summary>
    /// "Abrir" no aviso de prazo: a tarefa da linha que está na tela. Sem a
    /// linha (já concluída, ou fora do quadro), basta trazer o painel.
    /// </summary>
    private void OpenFromDeadlineAlert(
        IServiceProvider services,
        MainWindow window,
        TodayViewModel todayViewModel,
        Guid occurrenceId)
    {
        var row = todayViewModel.Sections
            .SelectMany(section => section.Items)
            .FirstOrDefault(item => item.OccurrenceId == occurrenceId);

        if (row is null)
        {
            Reveal(window);
            return;
        }

        ShowNotes(services, window, todayViewModel, row);
    }

    /// <summary>
    /// Uma linha do histórico (ADR-053): a mesma janela da tarefa, já na aba
    /// "Tempo" — quem chega pelo histórico quer ver onde o tempo foi. Se a
    /// tarefa está no quadro, vale a linha de lá, que já tem o Git aplicado.
    /// </summary>
    private void OpenFromHistory(
        IServiceProvider services,
        MainWindow window,
        TodayViewModel todayViewModel,
        TaskRowViewModel row)
    {
        var onBoard = todayViewModel.Sections
            .SelectMany(section => section.Items)
            .FirstOrDefault(item => item.OccurrenceId == row.OccurrenceId);

        var notes = ShowNotes(services, window, todayViewModel, onBoard ?? row);

        if (notes.DataContext is TaskNotesViewModel { Time: not null } viewModel)
        {
            viewModel.SelectedTabIndex = TaskNotesViewModel.TimeTab;
        }
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
    /// no boot (ADR-023). A exceção é "iniciar no HUD": quem pediu o painel
    /// permanente no canto quer vê-lo depois do login, e o HUD é justamente a
    /// forma que não aparece na frente de ninguém (ADR-048).
    /// </remarks>
    private static void StartHiddenIfAsked(MainWindow window, LaunchOptions launch)
    {
        var hiddenByLogin = launch.StartedByWindows && !window.Chrome.StartInHud;

        if (!window.Chrome.StartHidden && !hiddenByLogin)
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

    /// <summary>
    /// A lista de post-its. Da bandeja o painel pode estar escondido, e janela
    /// com dono escondido não aparece: aí ela abre sozinha.
    /// </summary>
    private static void ShowStickyNotes(IServiceProvider services, Window owner, StickyNoteScope scope)
    {
        var window = services.GetRequiredService<StickyNotesWindow>();

        if (owner.IsVisible)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }

        window.Activate();
        _ = window.RevealAsync(scope);
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

    private void Exit(IServiceProvider services, IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Aqui, e não dentro do Task.Run: ler o tamanho da janela fora da
        // thread da UI lança, e o encerramento pararia antes de soltar a bandeja.
        _window?.PersistNow();

        ShutdownInProgress.Start(() => Shutdown(services));
        desktop.Shutdown();
    }

    /// <summary>Para o agendador e solta o ícone antes de o processo morrer.</summary>
    private void Shutdown(IServiceProvider services)
    {
        // Uma cópia de banco em andamento para; a limpeza do diretório
        // temporário roda no caminho de volta dela (ADR-056).
        services.GetRequiredService<DatabaseOperationsViewModel>().CancelForShutdown();
        services.GetRequiredService<DatabaseCopyRuns>().CancelAll();
        services.GetRequiredService<ReminderScheduler>().Dispose();
        services.GetRequiredService<LifecycleMaintenanceScheduler>().Dispose();

        // Um push no meio para; o outbox guarda, e o próximo início reenvia com os mesmos ids (ADR-061).
        services.GetRequiredService<SyncScheduler>().Dispose();
        services.GetRequiredService<RemotePresenceScheduler>().Dispose();
        _ = services.GetRequiredService<LocalPeerBridge>().DisposeAsync().AsTask();
        services.GetRequiredService<AgentSessionMonitor>().Dispose();
        services.GetRequiredService<CommandExecutionMonitor>().Dispose();
        (services.GetRequiredService<IAgentEventEndpoint>() as IDisposable)?.Dispose();
        (services.GetRequiredService<IAudioPlayer>() as IDisposable)?.Dispose();

        _tray?.Dispose();
        _tray = null;

        // Depois do ícone, que some na hora; o MCP pode levar até o limite de
        // parada para soltar a porta, com quem estava no meio de uma chamada
        // terminando (ADR-059).
        services.GetRequiredService<McpServerManager>().Dispose();

        Services = null;
    }
}
