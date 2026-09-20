using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
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
                ListenForSecondLaunch(Services, window);
                StartHiddenIfAsked(window);
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
    private static void StartHiddenIfAsked(MainWindow window)
    {
        if (!window.Chrome.StartHidden)
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

        _tray?.Dispose();
        _tray = null;
    }
}
