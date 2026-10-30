using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Em que pé está a ligação com a sessão.</summary>
public enum TerminalConnection
{
    Connecting,
    Connected,
    Disconnected,
}

/// <summary>
/// Uma vista de uma sessão do terminal do agente (ADR-098, ADR-099): a tela que
/// chega pelo canal, o estado que o agente manda, e o que a pessoa digita ou
/// pede. O mesmo para a vista dentro do app e para quem controla de longe; só
/// muda o canal.
/// </summary>
/// <remarks>
/// A leitura roda fora da thread da tela; cada mensagem volta para ela pelo
/// <c>post</c> antes de tocar na <see cref="VtScreen"/>, que não é segura entre
/// threads. Os envios saem um de cada vez.
/// </remarks>
public sealed partial class TerminalSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Func<TerminalSize, CancellationToken, Task<ITerminalChannel>> _connect;

    private readonly Action<Action> _post;

    private readonly IConfirmationDialog? _confirmations;

    private readonly ILogger<TerminalSessionViewModel> _logger;

    private readonly TerminalClientKind _kind;

    private readonly SemaphoreSlim _sending = new(1, 1);

    private readonly CancellationTokenSource _closing = new();

    private ITerminalChannel? _channel;

    private TerminalSize _size = TerminalSize.Default;

    /// <summary>Onde o próximo pedaço de saída precisa começar; outro valor é um buraco.</summary>
    private long? _expected;

    private bool _resyncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsDisconnected), nameof(IsReadOnly), nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(ReconnectCommand), nameof(InterruptCommand), nameof(AllowCommand), nameof(DenyCommand), nameof(StopCommand), nameof(ForceStopCommand))]
    private TerminalConnection _connection = TerminalConnection.Connecting;

    [ObservableProperty]
    private VtScreen? _screen;

    /// <summary>Sobe a cada alimentação da tela: a vista redesenha.</summary>
    [ObservableProperty]
    private long _version;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsRunning), nameof(IsReadOnly), nameof(StatusText), nameof(DialogText), nameof(CanAnswerDialog),
        nameof(BypassWarning), nameof(MonitoringWarning), nameof(LeaseText), nameof(OthersText), nameof(ProcessText))]
    [NotifyCanExecuteChangedFor(nameof(InterruptCommand), nameof(AllowCommand), nameof(DenyCommand), nameof(StopCommand), nameof(ForceStopCommand))]
    private TerminalSessionStatus? _status;

    /// <summary>Um recado curto do agente ou desta vista: uma recusa, o controle que voltou.</summary>
    [ObservableProperty]
    private string? _message;

    /// <param name="kind">Que vista é esta: a lista "também aberto em" não a repete.</param>
    /// <param name="connect">Liga à sessão pedindo o tamanho que cabe; o canal local ou o relay remoto.</param>
    /// <param name="post">Leva uma ação para a thread da tela.</param>
    /// <param name="confirmations">Pergunta antes de forçar o fim; sem ele, "Forçar" não aparece.</param>
    public TerminalSessionViewModel(
        Guid sessionId,
        string title,
        TerminalClientKind kind,
        Func<TerminalSize, CancellationToken, Task<ITerminalChannel>> connect,
        Action<Action> post,
        IConfirmationDialog? confirmations,
        ILogger<TerminalSessionViewModel> logger)
    {
        SessionId = sessionId;
        Title = title;
        _kind = kind;
        _connect = connect;
        _post = post;
        _confirmations = confirmations;
        _logger = logger;
    }

    /// <summary>A vista já aberta foi pedida de novo: a janela vem para a frente.</summary>
    public event EventHandler? ActivationRequested;

    public Guid SessionId { get; }

    public string Title { get; }

    public bool IsConnected => Connection == TerminalConnection.Connected;

    public bool IsDisconnected => Connection == TerminalConnection.Disconnected;

    public bool IsRunning => Status?.IsRunning == true;

    /// <summary>Sem ligação, ou a sessão terminou: a tela fica, as teclas não saem.</summary>
    public bool IsReadOnly => !IsConnected || Status is { IsRunning: false };

    public bool CanForceStop => _confirmations is not null;

    public string StatusText => Connection switch
    {
        TerminalConnection.Connecting => "Conectando ao terminal do agente…",
        TerminalConnection.Disconnected => "Desconectado: o Claude segue rodando no agente, se ele estiver no ar.",
        _ => Status switch
        {
            null => "Conectado.",
            { IsRunning: false, ExitCode: { } code } => $"○ Encerrado (código {code})",
            { IsRunning: false } => "○ Encerrado",
            { Dialog: not null } => "◐ Esperando você responder",
            { Activity: AgentActivity.Working } => "● Trabalhando",
            { Activity: AgentActivity.WaitingForUser } => "◐ Esperando você",
            { Activity: AgentActivity.WaitingReview } => "◐ Terminou: espera o próximo prompt",
            { Monitored: false } => "● Rodando, sem acompanhamento",
            _ => "● Rodando",
        },
    };

    public string? DialogText => Status?.Dialog?.Message;

    /// <summary>Permitir ou negar por aqui: só a permissão, que tem só essas duas saídas.</summary>
    public bool CanAnswerDialog => IsRunning && Status?.Dialog?.AnswerableFromTheApp == true;

    public string? BypassWarning => Status?.PermissionsBypassed == true
        ? "Aberto sem as aprovações do Claude Code: o que for pedido aqui roda sem perguntar antes."
        : null;

    public string? MonitoringWarning => Status is { Monitored: false, IsRunning: true }
        ? "Sem acompanhamento: o MyTaskApp não sabe se o Claude trabalha ou espera. Só o teclado vale."
        : null;

    public string? LeaseText => Status?.Lease is { Kind: TerminalClientKind.Remote } lease
        ? $"Controlado de longe por {lease.Label}. Digitar aqui toma o controle de volta."
        : null;

    public string? OthersText
    {
        get
        {
            var others = Status?.Clients
                .Where(client => client.Kind != _kind)
                .Select(client => client.Kind switch
                {
                    TerminalClientKind.NativeWindow => "a janela do terminal",
                    TerminalClientKind.Embedded => "o MyTaskApp da máquina",
                    TerminalClientKind.Remote => $"{client.Label}, de longe",
                    _ => client.Label,
                })
                .Distinct()
                .ToList();

            return others is { Count: > 0 } ? $"Também aberto em {string.Join(", ", others)}." : null;
        }
    }

    public string? ProcessText => Status is { } status
        ? $"PID {status.ProcessId} · {status.Size.Columns}×{status.Size.Rows} · {status.WorkingDirectory}"
        : null;

    /// <summary>Liga (ou religa) à sessão. Falhar não lança: a vista mostra o motivo e "Reconectar".</summary>
    public async Task ConnectAsync()
    {
        await DropChannelAsync();
        Connection = TerminalConnection.Connecting;
        Message = null;
        _expected = null;
        _resyncing = false;

        ITerminalChannel channel;

        try
        {
            channel = await _connect(_size, _closing.Token);
        }
        catch (DomainException exception)
        {
            Disconnected(exception.Message);
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "TerminalConnectFailed {SessionId}", SessionId);
            Disconnected("Não foi possível ligar ao terminal do agente.");
            return;
        }

        _channel = channel;
        Connection = TerminalConnection.Connected;
        _ = PumpAsync(channel, _closing.Token);
    }

    public void RequestActivation() => ActivationRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>O que a pessoa digitou na vista.</summary>
    public Task TypeAsync(byte[] keys) => SendAsync(new TerminalKeys(keys));

    /// <summary>O tamanho que cabe na vista; o agente só aplica se ela dita o tamanho.</summary>
    public Task ResizeAsync(TerminalSize size)
    {
        _size = size;

        // Até chegar o primeiro estado, a tela vazia acompanha a vista.
        if (Status is null && Screen is { } screen && screen.Size != size)
        {
            screen.Resize(size);
            Version++;
        }

        return IsConnected ? SendAsync(new TerminalResize(size)) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(IsDisconnected))]
    private Task ReconnectAsync() => ConnectAsync();

    private bool CanInterrupt => IsConnected && Status is { IsRunning: true, Activity: AgentActivity.Working, Dialog: null };

    /// <summary>O Esc do Claude: para o que ele está fazendo, a conversa fica.</summary>
    [RelayCommand(CanExecute = nameof(CanInterrupt))]
    private Task InterruptAsync() => SendAsync(new TerminalInterrupt());

    private bool CanAnswer => IsConnected && CanAnswerDialog;

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task AllowAsync() => AnswerAsync(allow: true);

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task DenyAsync() => AnswerAsync(allow: false);

    private bool CanStop => IsConnected && IsRunning;

    /// <summary>Como fechar a janela de um terminal: o Claude recebe o fim e salva a conversa.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        try
        {
            await _channel!.StopAsync(force: false, _closing.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SendFailed(exception);
        }
    }

    private bool CanForce => CanStop && CanForceStop;

    /// <summary>Mata a árvore da sessão, e só ela: o que o Claude abriu dentro dela vai junto.</summary>
    [RelayCommand(CanExecute = nameof(CanForce))]
    private async Task ForceStopAsync()
    {
        var confirmed = await _confirmations!.AskAsync(new ConfirmationRequest(
            "Forçar o fim desta sessão?",
            "O Claude e tudo o que ele abriu nesta sessão são encerrados na hora, sem salvar. Os outros programas da máquina não são tocados.",
            "Forçar o fim",
            IsIrreversible: true));

        if (!confirmed)
        {
            return;
        }

        try
        {
            await _channel!.StopAsync(force: true, _closing.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SendFailed(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_closing.IsCancellationRequested)
        {
            return;
        }

        await _closing.CancelAsync();
        await DropChannelAsync();
        _closing.Dispose();
    }

    private Task AnswerAsync(bool allow) =>
        Status?.Dialog is { } dialog ? SendAsync(new TerminalChoice(dialog.Id, allow)) : Task.CompletedTask;

    private async Task SendAsync(TerminalInput input)
    {
        if (_channel is not { } channel || _closing.IsCancellationRequested)
        {
            return;
        }

        await _sending.WaitAsync();

        try
        {
            await channel.SendAsync(input, _closing.Token);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SendFailed(exception);
        }
        finally
        {
            _sending.Release();
        }
    }

    private void SendFailed(Exception exception)
    {
        _logger.LogInformation(exception, "TerminalSendFailed {SessionId}", SessionId);
        _post(() => Disconnected("A ligação com o terminal caiu."));
    }

    private async Task PumpAsync(ITerminalChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            while (await channel.ReadAsync(cancellationToken) is { } message)
            {
                _post(() => Apply(channel, message));
            }

            _post(() => Disconnected("O agente fechou a ligação com o terminal.", channel));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "TerminalReadFailed {SessionId}", SessionId);
            _post(() => Disconnected("A ligação com o terminal caiu.", channel));
        }
    }

    private void Apply(ITerminalChannel channel, TerminalMessage message)
    {
        // Mensagem de uma ligação que já foi trocada por outra.
        if (channel != _channel)
        {
            return;
        }

        switch (message)
        {
            case TerminalOutput output:
                Apply(output);
                break;
            case TerminalStateMessage state:
                Apply(state.Status);
                break;
            case TerminalNotice notice:
                Message = notice.Text;
                break;
            case TerminalRefusal refusal:
                Message = refusal.Text;

                // Recusado antes de ver a tela: a sessão não existe mais, ou não é para esta vista.
                if (Screen is null && Status is null)
                {
                    Disconnected(refusal.Text);
                }

                break;
        }
    }

    private void Apply(TerminalOutput output)
    {
        if (output.Reset)
        {
            Screen = new VtScreen(Status?.Size ?? _size);
            _resyncing = false;
        }
        else if (Screen is null || (_expected is { } expected && output.Offset != expected))
        {
            // Um buraco na saída: a tela daqui não é mais a de lá. Pede o retrato e espera por ele.
            if (!_resyncing)
            {
                _resyncing = true;
                _ = SendAsync(new TerminalResync());
            }

            return;
        }

        Screen!.Feed(output.Data);
        _expected = output.End;
        Version++;
    }

    private void Apply(TerminalSessionStatus status)
    {
        var resized = Screen is { } screen && screen.Size != status.Size;
        Status = status;

        if (resized)
        {
            // Quem dita o tamanho é outra vista: a tela daqui segue a de lá, e o retrato acerta as linhas.
            Screen!.Resize(status.Size);
            Version++;

            if (!_resyncing && status.IsRunning)
            {
                _resyncing = true;
                _ = SendAsync(new TerminalResync());
            }
        }
    }

    private void Disconnected(string reason, ITerminalChannel? from = null)
    {
        if (from is not null && from != _channel)
        {
            return;
        }

        Connection = TerminalConnection.Disconnected;
        Message = reason;
        _ = DropChannelAsync();
    }

    private async Task DropChannelAsync()
    {
        if (Interlocked.Exchange(ref _channel, null) is { } channel)
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "TerminalChannelDisposeFailed {SessionId}", SessionId);
            }
        }
    }
}
