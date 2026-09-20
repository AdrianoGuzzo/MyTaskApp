using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Reminders;

namespace MyTaskApp.Desktop.Reminders;

/// <summary>
/// O que a bandeja sabe fazer. Um registro em vez de seis parâmetros soltos:
/// a ordem de <c>Action, Action, Action…</c> é fácil de trocar sem o
/// compilador notar.
/// </summary>
internal sealed record TrayActions(
    Action Open,
    Action Settings,
    Action Exit,
    Action ToggleTopmost,
    Action ToggleGhost,
    Action UseCompact,
    Action Hide);

/// <summary>
/// O ícone da bandeja: é o que permite fechar a janela e o app continuar
/// lembrando. Montado em código, e não em XAML, porque <c>App.axaml</c> não tem
/// <c>DataContext</c> e binding sobre <c>Application</c> com compiled bindings
/// dá mais trabalho do que estas poucas linhas.
/// </summary>
internal sealed class TrayIconHost(
    IUseCaseRunner runner,
    ILogger<TrayIconHost> logger) : IDisposable
{
    private static readonly Uri IconUri = new("avares://MyTaskApp/Assets/tray.ico");

    // Guardado em campo de propósito: se a coleção for coletada, o ícone some da
    // bandeja sem erro nenhum e sem nada no log.
    private TrayIcons? _icons;
    private TrayIcon? _icon;
    private NativeMenuItem? _topmost;
    private NativeMenuItem? _ghost;

    /// <summary>
    /// Instala o ícone. Devolve <c>false</c> se não deu — e aí quem chama
    /// <b>precisa</b> voltar o <c>ShutdownMode</c>, senão o app fica sem nenhuma
    /// forma de ser encerrado.
    /// </summary>
    public bool TryInstall(Avalonia.Application application, TrayActions actions)
    {
        try
        {
            _icon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(IconUri)),
                ToolTipText = "MyTaskApp",
                IsVisible = true,
                Menu = BuildMenu(actions),
            };

            // Clicar no ícone reabre — é o gesto que todo mundo tenta primeiro.
            _icon.Clicked += (_, _) => actions.Open();

            _icons = [_icon];
            TrayIcon.SetIcons(application, _icons);

            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "TrayIconUnavailable");
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// O "badge" do widget. Sem janela, sem popup: o número de pendências vive
    /// no balão do ícone, que é o lugar onde ele não interrompe ninguém.
    /// </summary>
    public void ShowPending(int pending)
    {
        if (_icon is null)
        {
            return;
        }

        _icon.ToolTipText = pending switch
        {
            <= 0 => "MyTaskApp — tudo em dia",
            1 => "MyTaskApp — 1 tarefa pendente",
            _ => $"MyTaskApp — {pending} tarefas pendentes",
        };
    }

    /// <summary>Mantém o visto do menu de acordo com o estado real da janela.</summary>
    public void ShowTopmost(bool isTopmost)
    {
        if (_topmost is not null)
        {
            _topmost.IsChecked = isTopmost;
        }
    }

    /// <summary>
    /// Sem cabeçalho o modo discreto não tem botão próprio na janela: a bandeja
    /// é a rota de volta, e precisa mostrar o estado certo.
    /// </summary>
    public void ShowGhost(bool isGhost)
    {
        if (_ghost is not null)
        {
            _ghost.IsChecked = isGhost;
        }
    }

    public void Dispose()
    {
        // Sem isto fica um ícone fantasma na bandeja até o usuário passar o
        // mouse por cima.
        _icon?.Dispose();
        _icon = null;
        _icons = null;
        _topmost = null;
        _ghost = null;
    }

    private NativeMenu BuildMenu(TrayActions actions)
    {
        var open = new NativeMenuItem("Abrir");
        open.Click += (_, _) => actions.Open();

        var compact = new NativeMenuItem("Modo compacto");
        compact.Click += (_, _) => actions.UseCompact();

        _topmost = new NativeMenuItem("Sempre no topo")
        {
            ToggleType = MenuItemToggleType.CheckBox,
        };
        _topmost.Click += (_, _) => actions.ToggleTopmost();

        _ghost = new NativeMenuItem("Modo discreto")
        {
            ToggleType = MenuItemToggleType.CheckBox,
        };
        _ghost.Click += (_, _) => actions.ToggleGhost();

        var hide = new NativeMenuItem("Ocultar");
        hide.Click += (_, _) => actions.Hide();

        var pause = new NativeMenuItem("Pausar lembretes por 1 hora");
        pause.Click += (_, _) => _ = PauseAsync();

        var settings = new NativeMenuItem("Configuração de lembretes…");
        settings.Click += (_, _) => actions.Settings();

        var exit = new NativeMenuItem("Sair");
        exit.Click += (_, _) => actions.Exit();

        return
        [
            open,
            compact,
            _topmost,
            _ghost,
            hide,
            new NativeMenuItemSeparator(),
            pause,
            settings,
            new NativeMenuItemSeparator(),
            exit,
        ];
    }

    private async Task PauseAsync()
    {
        try
        {
            await runner.RunAsync<PauseRemindersHandler>(
                (handler, token) => handler.HandleAsync(
                    new PauseReminders(TimeSpan.FromHours(1)), token),
                CancellationToken.None);

            logger.LogInformation("RemindersPausedFromTray");
        }
        catch (Exception exception)
        {
            // Não há tela para mostrar o erro: a bandeja é um caminho sem UI.
            logger.LogError(exception, "PauseFromTrayFailed");
        }
    }
}
