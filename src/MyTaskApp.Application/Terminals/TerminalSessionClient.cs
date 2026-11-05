using Microsoft.Extensions.Logging;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Terminals;

/// <summary>Em que pé está a ligação com a sessão.</summary>
public enum TerminalConnection
{
    Connecting,
    Connected,
    Disconnected,
}

/// <summary>
/// Uma vista de uma sessão do terminal do agente (ADR-098, ADR-099), sem a
/// tela: a saída que chega pelo canal numa <see cref="VtScreen"/>, o estado
/// que o agente manda, e o que a pessoa digita ou pede. A janela do desktop e
/// a tela do celular (ADR-101) desenham em cima dela; só muda o canal.
/// </summary>
/// <remarks>
/// A leitura roda fora da thread da tela; cada mensagem volta para ela pelo
/// <c>post</c> antes de tocar na <see cref="VtScreen"/>, que não é segura entre
/// threads. Os eventos saem na thread da tela. Os envios saem um de cada vez.
/// </remarks>
public sealed class TerminalSessionClient : IAsyncDisposable
{
    private readonly Func<TerminalSize, CancellationToken, Task<ITerminalChannel>> _connect;

    private readonly Action<Action> _post;

    private readonly ILogger _logger;

    private readonly TerminalClientKind _kind;

    private readonly SemaphoreSlim _sending = new(1, 1);

    private readonly CancellationTokenSource _closing = new();

    private ITerminalChannel? _channel;

    private TerminalSize _size = TerminalSize.Default;

    /// <summary>Onde o próximo pedaço de saída precisa começar; outro valor é um buraco.</summary>
    private long? _expected;

    private bool _resyncing;

    /// <param name="kind">Que vista é esta: a lista "também aberto em" não a repete.</param>
    /// <param name="connect">Liga à sessão pedindo o tamanho que cabe; o canal local ou o relay remoto.</param>
    /// <param name="post">Leva uma ação para a thread da tela.</param>
    public TerminalSessionClient(
        Guid sessionId,
        TerminalClientKind kind,
        Func<TerminalSize, CancellationToken, Task<ITerminalChannel>> connect,
        Action<Action> post,
        ILogger logger)
    {
        SessionId = sessionId;
        _kind = kind;
        _connect = connect;
        _post = post;
        _logger = logger;
    }

    /// <summary>A tela mudou (<see cref="Screen"/> ou <see cref="Version"/>): a vista redesenha.</summary>
    public event Action? ScreenChanged;

    /// <summary>O resto mudou: a ligação, o estado, o recado.</summary>
    public event Action? StateChanged;

    public Guid SessionId { get; }

    public TerminalConnection Connection { get; private set; } = TerminalConnection.Connecting;

    public VtScreen? Screen { get; private set; }

    /// <summary>Sobe a cada alimentação da tela: a vista redesenha.</summary>
    public long Version { get; private set; }

    public TerminalSessionStatus? Status { get; private set; }

    /// <summary>Um recado curto do agente ou desta vista: uma recusa, o controle que voltou.</summary>
    public string? Message { get; private set; }

    /// <summary>O tamanho que esta vista pediu por último.</summary>
    public TerminalSize RequestedSize => _size;

    public bool IsConnected => Connection == TerminalConnection.Connected;

    public bool IsDisconnected => Connection == TerminalConnection.Disconnected;

    public bool IsRunning => Status?.IsRunning == true;

    /// <summary>
    /// Sem ligação, ou a sessão terminou: a tela fica, as teclas não saem. De
    /// longe, também sem a vez (ADR-099): ela é pedida, não tomada digitando.
    /// </summary>
    public bool IsReadOnly => !IsConnected || Status is { IsRunning: false } || (IsRemote && !HasControl);

    /// <summary>A vista de quem controla de outra máquina: assume e solta a vez pelos botões.</summary>
    public bool IsRemote => _kind == TerminalClientKind.Remote;

    /// <summary>Esta vista tem a vez de digitar.</summary>
    public bool HasControl => Status is { Lease: { } lease, YourClientId: { } you } && lease.ClientId == you;

    public bool CanTakeControlNow => IsRemote && IsConnected && IsRunning && !HasControl;

    public bool CanReleaseControlNow => IsRemote && IsConnected && HasControl;

    public bool CanInterrupt => IsConnected && Status is { IsRunning: true, Activity: AgentActivity.Working, Dialog: null };

    /// <summary>Permitir ou negar por aqui: só a permissão, que tem só essas duas saídas.</summary>
    public bool CanAnswerDialog => IsRunning && Status?.Dialog?.AnswerableFromTheApp == true;

    public bool CanAnswer => IsConnected && CanAnswerDialog;

    /// <summary>
    /// Mandar um prompt pronto (colado, mais Enter): com o Claude acompanhado,
    /// parado e sem diálogo, e com a vez quando de longe (ADR-098). O agente
    /// confere de novo e recusa com o motivo.
    /// </summary>
    public bool CanPrompt =>
        IsConnected
        && Status is { IsRunning: true, Monitored: true, Dialog: null } status
        && status.Activity != AgentActivity.Working
        && (!IsRemote || HasControl);

    public bool CanStop => IsConnected && IsRunning;

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

    public string? BypassWarning => Status?.PermissionsBypassed == true
        ? "Aberto sem as aprovações do Claude Code: o que for pedido aqui roda sem perguntar antes."
        : null;

    public string? MonitoringWarning => Status is { Monitored: false, IsRunning: true }
        ? "Sem acompanhamento: o MyTaskApp não sabe se o Claude trabalha ou espera. Só o teclado vale."
        : null;

    public string? LeaseText => IsRemote
        ? Status switch
        {
            null or { IsRunning: false } => null,
            _ when HasControl => "Você está com o controle: o que digitar aqui vai para o Claude de lá. Quem está na máquina pode tomar de volta digitando.",
            { Lease: { } holder } => $"{holder.Label} está com o controle. Assumir passa a vez para cá.",
            _ => "Só olhando. Assuma o controle para digitar.",
        }
        : Status?.Lease is { Kind: TerminalClientKind.Remote } lease
            ? $"Controlado de longe por {lease.Label}. Digitar aqui toma o controle de volta."
            : null;

    public string? OthersText
    {
        get
        {
            // De longe, a vista sabe quem é; as outras se reconhecem pelo tipo.
            var others = Status?.Clients
                .Where(client => Status.YourClientId is { } you ? client.Id != you : client.Kind != _kind)
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

    /// <summary>De longe, a pasta não vem: o caminho é da outra máquina.</summary>
    public string? ProcessText => Status is { } status
        ? status.WorkingDirectory is { Length: > 0 } directory
            ? $"PID {status.ProcessId} · {status.Size.Columns}×{status.Size.Rows} · {directory}"
            : $"PID {status.ProcessId} · {status.Size.Columns}×{status.Size.Rows}"
        : null;

    /// <summary>Liga (ou religa) à sessão. Falhar não lança: a vista mostra o motivo e "Reconectar".</summary>
    public async Task ConnectAsync()
    {
        await DropChannelAsync();
        Connection = TerminalConnection.Connecting;
        Message = null;
        _expected = null;
        _resyncing = false;
        StateChanged?.Invoke();

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
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
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
        StateChanged?.Invoke();
        _ = PumpAsync(channel, _closing.Token);
    }

    /// <summary>Solta a ligação sem encerrar nada lá: a vista fica, com "Reconectar".</summary>
    public async Task DisconnectAsync(string reason)
    {
        await DropChannelAsync();
        Connection = TerminalConnection.Disconnected;
        Message = reason;
        StateChanged?.Invoke();
    }

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
            ScreenChanged?.Invoke();
        }

        return IsConnected ? SendAsync(new TerminalResize(size)) : Task.CompletedTask;
    }

    public Task TakeControlAsync() => SendAsync(new TerminalTakeControl());

    public Task ReleaseControlAsync() => SendAsync(new TerminalReleaseControl());

    /// <summary>O Esc do Claude: para o que ele está fazendo, a conversa fica.</summary>
    public Task InterruptAsync() => SendAsync(new TerminalInterrupt());

    /// <summary>Responde o diálogo aberto agora; um id velho o agente recusa.</summary>
    public Task AnswerAsync(bool allow) =>
        Status?.Dialog is { } dialog ? SendAsync(new TerminalChoice(dialog.Id, allow)) : Task.CompletedTask;

    /// <summary>Um prompt pronto: o agente cola entre colchetes e manda o Enter.</summary>
    public Task PromptAsync(string text) =>
        string.IsNullOrWhiteSpace(text) ? Task.CompletedTask : SendAsync(new TerminalPrompt(text));

    /// <summary>
    /// <paramref name="force"/> falso é como fechar a janela de um terminal: o
    /// Claude recebe o fim e salva a conversa. Verdadeiro mata a árvore da
    /// sessão, e só ela.
    /// </summary>
    public async Task StopAsync(bool force)
    {
        if (_channel is not { } channel)
        {
            return;
        }

        try
        {
            await channel.StopAsync(force, _closing.Token);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception exception)
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
                StateChanged?.Invoke();
                break;
            case TerminalRefusal refusal:
                Message = refusal.Text;

                // Recusado antes de ver a tela: a sessão não existe mais, ou não é para esta vista.
                if (Screen is null && Status is null)
                {
                    Disconnected(refusal.Text);
                }
                else
                {
                    StateChanged?.Invoke();
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
        ScreenChanged?.Invoke();
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
            ScreenChanged?.Invoke();

            if (!_resyncing && status.IsRunning)
            {
                _resyncing = true;
                _ = SendAsync(new TerminalResync());
            }
        }

        StateChanged?.Invoke();
    }

    private void Disconnected(string reason, ITerminalChannel? from = null)
    {
        if (from is not null && from != _channel)
        {
            return;
        }

        Connection = TerminalConnection.Disconnected;
        Message = reason;
        StateChanged?.Invoke();
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
