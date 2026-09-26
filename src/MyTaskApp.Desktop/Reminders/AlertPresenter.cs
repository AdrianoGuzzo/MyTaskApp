using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;

namespace MyTaskApp.Desktop.Reminders;

/// <summary>
/// Põe o aviso na tela. É o <b>único</b> arquivo que o agendador alcança que
/// referencia Avalonia — o que torna a regra do pulo de thread verificável por
/// inspeção, em vez de por disciplina.
/// </summary>
/// <remarks>
/// Também põe o aviso do agente de IA (ADR-037): a mesma pilha no mesmo canto,
/// na tela do painel — dois apresentadores empilhariam janelas uma por cima da
/// outra. A chave é a ocorrência (lembrete) ou a sessão (agente).
/// </remarks>
internal sealed class AlertPresenter(
    IServiceProvider services,
    ILogger<AlertPresenter> logger) : IAlertPresenter, IAgentAttentionPresenter
{
    /// <summary>Acima disto a tela vira um mural; o despacho já agrega antes.</summary>
    private const int MaxVisible = 3;

    private readonly Dictionary<Guid, Window> _open = [];

    /// <summary>Avisa a aplicação de que o usuário reagiu, para o quadro recarregar.</summary>
    public event Action? Acted;

    /// <summary>A janela principal, para o último degrau da escada poder trazê-la à frente.</summary>
    public Window? MainWindow { get; set; }

    public Task PresentAsync(ReminderAlert alert, CancellationToken cancellationToken = default) =>
        // O agendador tica na thread pool: o pulo é a primeira coisa que acontece.
        Dispatcher.UIThread.InvokeAsync(() => Present(alert)).GetTask();

    public Task PresentDigestAsync(
        ReminderDigest digest,
        CancellationToken cancellationToken = default) =>
        Dispatcher.UIThread.InvokeAsync(() => PresentDigest(digest)).GetTask();

    public Task DismissAsync(Guid occurrenceId, CancellationToken cancellationToken = default) =>
        Dispatcher.UIThread.InvokeAsync(() => Close(occurrenceId)).GetTask();

    // IAgentAttentionPresenter.DismissAsync tem a mesma assinatura: a chave é a
    // sessão, e fechar é o mesmo gesto.

    public Task PresentAsync(AgentAttention attention, CancellationToken cancellationToken = default) =>
        // O aviso chega da fila da porta local, fora da thread de UI.
        Dispatcher.UIThread.InvokeAsync(() => Present(attention)).GetTask();

    /// <summary>
    /// Um aviso por sessão, atualizado no lugar: a pergunta seguinte do mesmo
    /// agente troca o texto, não empilha outra janela.
    /// </summary>
    private void Present(AgentAttention attention)
    {
        if (_open.TryGetValue(attention.SessionId, out var existing))
        {
            ((AgentAlertViewModel)existing.DataContext!).Show(attention);
            return;
        }

        if (_open.Count >= MaxVisible)
        {
            logger.LogDebug("AgentAlertSuppressed {SessionId}", attention.SessionId);
            return;
        }

        var viewModel = services.GetRequiredService<AgentAlertViewModel>();
        viewModel.Show(attention);

        var window = new AgentAlertWindow { DataContext = viewModel };

        viewModel.Closed += _ => Close(attention.SessionId);

        _open[attention.SessionId] = window;

        // Sem ativar: aparece por cima, mas o teclado fica onde estava.
        window.Show();

        Position(window);
    }

    private void Present(ReminderAlert alert)
    {
        // Já há janela para esta ocorrência: atualiza no lugar. Sem isto, um
        // lembrete repetindo empilha uma janela nova a cada 15 minutos enquanto
        // o usuário está fora.
        if (_open.TryGetValue(alert.OccurrenceId, out var existing))
        {
            ((ReminderAlertViewModel)existing.DataContext!).Show(alert);
            Escalate(existing, alert);
            return;
        }

        if (_open.Count >= MaxVisible)
        {
            logger.LogDebug("ReminderAlertSuppressed {OccurrenceId}", alert.OccurrenceId);
            return;
        }

        var viewModel = services.GetRequiredService<ReminderAlertViewModel>();
        viewModel.Show(alert);

        var window = new AlertWindow { DataContext = viewModel };

        viewModel.Acted += _ =>
        {
            Close(alert.OccurrenceId);
            Acted?.Invoke();
        };

        _open[alert.OccurrenceId] = window;

        // Nunca modal: o aviso não pode impedir o usuário de trabalhar.
        window.Show();

        Position(window);
        Escalate(window, alert);
    }

    private void PresentDigest(ReminderDigest digest)
    {
        var alert = new ReminderAlert(
            Guid.Empty,
            Guid.Empty,
            digest.Count == 1
                ? "1 checklist está esperando sua atenção"
                : $"{digest.Count} checklists estão esperando sua atenção",
            null,
            digest.Level,
            digest.LongestWait);

        Present(alert);
    }

    private void Close(Guid occurrenceId)
    {
        if (_open.Remove(occurrenceId, out var window))
        {
            window.Close();
        }
    }

    /// <summary>
    /// Empilha do canto inferior direito para cima. Depois do <c>Show()</c>
    /// porque <c>Screens</c> precisa de um handle de janela.
    /// </summary>
    private void Position(Window window)
    {
        // Na tela onde o painel esta, e nao sempre na primaria: com dois
        // monitores o aviso aparecia do outro lado da mesa.
        var area = ScreenFor(window)?.WorkingArea;

        if (area is not { } screen)
        {
            return;
        }

        const int Gap = 12;
        var scaling = window.RenderScaling;
        var width = (int)(window.Width * scaling);
        var height = (int)(Math.Max(window.Bounds.Height, 180) * scaling);
        var index = _open.Count - 1;

        window.Position = new Avalonia.PixelPoint(
            screen.X + screen.Width - width - (int)(Gap * scaling),
            screen.Y + screen.Height - ((height + (int)(Gap * scaling)) * (index + 1)));
    }

    private Avalonia.Platform.Screen? ScreenFor(Window window)
    {
        var onWidget = MainWindow is { } main
            ? window.Screens.ScreenFromPoint(main.Position)
            : null;

        return onWidget ?? window.Screens.Primary;
    }

    /// <summary>
    /// Só o topo da escada mexe com foco. Abaixo disso o aviso aparece sem
    /// roubar o cursor — é isso que "insistente, mas sem impedir de trabalhar"
    /// quer dizer na prática.
    /// </summary>
    private void Escalate(Window window, ReminderAlert alert)
    {
        window.Topmost = alert.Level.Prominent;

        if (!alert.Level.BringToFront)
        {
            return;
        }

        window.Topmost = true;
        window.Activate();

        if (MainWindow is not { } main)
        {
            return;
        }

        // Ordem importa: Show() numa janela escondida enquanto minimizada a
        // deixa minimizada. O Windows ainda pode recusar o foco a um processo em
        // segundo plano e só piscar na barra — a janela de aviso é quem
        // realmente chama atenção.
        main.Show();
        main.WindowState = WindowState.Normal;
        main.Activate();
    }
}
