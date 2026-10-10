using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Deadlines;
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
    ILogger<AlertPresenter> logger) : IAlertPresenter, IAgentAttentionPresenter, IDeadlineAlertPresenter
{
    /// <summary>Acima disto a tela vira um mural; o despacho já agrega antes.</summary>
    private const int MaxVisible = 3;

    /// <summary>
    /// A chave é o id e se o aviso é de prazo: a mesma ocorrência pode ter, ao
    /// mesmo tempo, o lembrete e o prazo na tela (ADR-050), e um não fecha o outro.
    /// </summary>
    private readonly Dictionary<(bool IsDeadline, Guid Id), Window> _open = [];

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
        Dispatcher.UIThread.InvokeAsync(() => Close((false, occurrenceId))).GetTask();

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
        if (_open.TryGetValue((false, attention.SessionId), out var existing))
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

        viewModel.Closed += _ => Close((false, attention.SessionId));

        _open[(false, attention.SessionId)] = window;

        // Sem ativar: aparece por cima, mas o teclado fica onde estava.
        window.Show();

        Position(window);
    }

    private void Present(ReminderAlert alert)
    {
        // Já há janela para esta ocorrência: atualiza no lugar. Sem isto, um
        // lembrete repetindo empilha uma janela nova a cada 15 minutos enquanto
        // o usuário está fora.
        if (_open.TryGetValue((false, alert.OccurrenceId), out var existing))
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
            Close((false, alert.OccurrenceId));
            Acted?.Invoke();
        };

        _open[(false, alert.OccurrenceId)] = window;

        // Nunca modal: o aviso não pode impedir o usuário de trabalhar.
        window.Show();

        Position(window);
        Escalate(window, alert);
    }

    // ---------------------------------------------------------------------
    // Prazo (ADR-050): mesma pilha, mesmo canto, chave própria.
    // ---------------------------------------------------------------------

    public Task PresentAsync(DeadlineAlert alert, CancellationToken cancellationToken = default) =>
        Dispatcher.UIThread.InvokeAsync(() => Present(alert)).GetTask();

    public Task PresentDigestAsync(DeadlineDigest digest, CancellationToken cancellationToken = default) =>
        Dispatcher.UIThread.InvokeAsync(() => Present(new DeadlineAlert(
            Guid.Empty,
            Guid.Empty,
            $"{digest.Count} tarefas com prazo",
            "Prazos pedindo atenção",
            "Veja a seção PRAZOS e as atrasadas na lista.",
            DeadlineAlertStage.None,
            digest.IsUrgent ? DeadlineSeverity.Urgent : DeadlineSeverity.Attention,
            digest.IsUrgent))).GetTask();

    /// <summary>Explícito: a assinatura é a mesma do lembrete, e a chave não.</summary>
    /// <remarks>
    /// Fora da thread da tela — o servidor MCP mudando o prazo (ADR-059) —, só
    /// enfileira e volta. Esperar aqui seria esperar a tela segurando a
    /// transação do SQLite, enquanto a tela espera o mesmo banco.
    /// </remarks>
    Task IDeadlineAlertPresenter.DismissAsync(Guid occurrenceId, CancellationToken cancellationToken)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Close((true, occurrenceId)));
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(() => Close((true, occurrenceId))).GetTask();
    }

    /// <summary>
    /// Um aviso de prazo por ocorrência, atualizado no lugar: o degrau seguinte
    /// troca o texto em vez de empilhar. Urgente fica por cima das outras
    /// janelas, mas nunca rouba o foco — o prazo é lembrete, não alarme.
    /// </summary>
    private void Present(DeadlineAlert alert)
    {
        var key = (true, alert.OccurrenceId);

        if (_open.TryGetValue(key, out var existing))
        {
            ((DeadlineAlertViewModel)existing.DataContext!).Show(alert);
            existing.Topmost = alert.IsUrgent;
            return;
        }

        if (_open.Count >= MaxVisible)
        {
            logger.LogDebug("DeadlineAlertSuppressed {OccurrenceId}", alert.OccurrenceId);
            return;
        }

        var viewModel = services.GetRequiredService<DeadlineAlertViewModel>();
        viewModel.Show(alert);

        var window = new DeadlineAlertWindow { DataContext = viewModel };

        viewModel.Acted += () =>
        {
            Close(key);
            Acted?.Invoke();
        };

        viewModel.OpenRequested += occurrenceId => OpenRequested?.Invoke(occurrenceId);

        _open[key] = window;

        window.Topmost = alert.IsUrgent;
        window.Show();

        Position(window);
    }

    /// <summary>"Abrir" no aviso de prazo: o composition root sabe abrir a tarefa.</summary>
    public event Action<Guid>? OpenRequested;

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

    private void Close((bool IsDeadline, Guid Id) key)
    {
        if (_open.Remove(key, out var window))
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
