using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Terminals;

/// <summary>O que o host precisa saber da sessão, além do processo.</summary>
/// <param name="Monitored">A sessão abriu com os hooks: o estado vem deles.</param>
/// <param name="PermissionsBypassed">Pelos argumentos ou pela configuração do Claude (ADR-099).</param>
public sealed record TerminalSessionInfo(
    Guid SessionId,
    Guid TaskId,
    Guid? DevelopmentId,
    string WorkingDirectory,
    bool Monitored,
    bool PermissionsBypassed,
    string ProviderId = "claude-code",
    bool SupportsPermissionChoices = true);

/// <summary>
/// Uma sessão de terminal hospedada pelo agente (ADR-098): o pseudoterminal,
/// a tela que se monta com a saída dele e os clientes ligados — a janela
/// nativa, o app desta máquina e quem controla de longe. Todos veem a mesma
/// sessão; nenhum é dono dela, e desligar um não encerra nada.
/// </summary>
/// <remarks>
/// <para>
/// <b>A vez de digitar (lease).</b> Quem está na máquina digita quando quiser:
/// tecla local tira a vez de quem estava de longe, e ele é avisado. De longe
/// é preciso pedir a vez (<see cref="TerminalTakeControl"/>), e ela vence
/// depois de <see cref="RemoteLeaseIdle"/> sem digitar. As ações
/// estruturadas (prompt, resposta, interromper) não pedem a vez, mas não
/// passam por cima de quem a tem.
/// </para>
/// <para>
/// <b>Memória limitada.</b> Nada da saída é guardado além da tela e do
/// histórico do <see cref="VtScreen"/>. Cada cliente tem uma fila de até
/// <see cref="MaxPendingBytes"/>; quem não acompanha perde a saída do meio e
/// recebe o retrato (<see cref="TerminalOutput.Reset"/>) quando volta a ler.
/// </para>
/// <para>
/// <b>O estado vem dos hooks e do processo</b>, nunca do texto da tela: o
/// diálogo aberto, se o Claude trabalha ou espera. Sem hooks, só teclas valem.
/// </para>
/// </remarks>
public sealed partial class TerminalSessionHost : IAsyncDisposable
{
    /// <summary>A fila de um cliente que não lê. Passou disso, ele recebe o retrato depois.</summary>
    public const int MaxPendingBytes = 1024 * 1024;

    /// <summary>O maior prompt aceito pela ação estruturada.</summary>
    public const int MaxPromptLength = 16_000;

    /// <summary>O maior lote de teclas de uma vez.</summary>
    public const int MaxKeysLength = 64 * 1024;

    /// <summary>A vez de digitar de longe vence sem uso: ninguém fica preso fora da própria máquina.</summary>
    public static readonly TimeSpan RemoteLeaseIdle = TimeSpan.FromMinutes(10);

    private const int ReadBufferSize = 16 * 1024;

    private readonly IPseudoTerminal _terminal;

    private readonly TimeProvider _time;

    private readonly ILogger _logger;

    private readonly Lock _gate = new();

    private readonly VtScreen _screen;

    private readonly List<TerminalClient> _clients = [];

    private readonly CancellationTokenSource _stopping = new();

    private readonly Task _pump;

    private long _offset;

    private TerminalLeaseState? _lease;

    private TerminalProcessState _process = TerminalProcessState.Running;

    private int? _exitCode;

    private AgentActivity _activity;

    private string? _activityMessage;

    private TerminalDialog? _dialog;

    /// <summary>Alguém digitou desde que o diálogo abriu: o Enter já não é "permitir" com certeza.</summary>
    private bool _keysSinceDialog;

    private string? _permissionMode;

    private bool _bypassed;

    private DateTimeOffset? _lastOutputAt;

    private int _disposed;

    public TerminalSessionHost(TerminalSessionInfo info, IPseudoTerminal terminal, TimeProvider time, ILogger<TerminalSessionHost> logger)
    {
        Info = info;
        _terminal = terminal;
        _time = time;
        _logger = logger;
        _bypassed = info.PermissionsBypassed;
        _screen = new VtScreen(terminal.Size) { AnswerQueries = true };
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Mudou o estado (atividade, diálogo, a vez, clientes, o processo). Fora de qualquer trava.</summary>
    public event EventHandler? Changed;

    /// <summary>O Claude parou esperando alguém, ou terminou a resposta: hora de chamar quem está perto.</summary>
    public event EventHandler<TerminalAttentionEventArgs>? AttentionNeeded;

    public TerminalSessionInfo Info { get; }

    public Guid SessionId => Info.SessionId;

    public int ProcessId => _terminal.ProcessId;

    public DateTimeOffset ProcessStartedAt => _terminal.ProcessStartedAt;

    /// <summary>Completa quando o processo saiu e toda a saída dele foi lida.</summary>
    public Task Completion => _pump;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process == TerminalProcessState.Running;
            }
        }
    }

    public TerminalSessionStatus Status
    {
        get
        {
            lock (_gate)
            {
                return StatusLocked();
            }
        }
    }

    /// <summary>
    /// Liga um cliente. A primeira coisa que ele lê é o retrato da tela; dali
    /// em diante, a saída e o estado, em ordem.
    /// </summary>
    /// <param name="snapshotScrollback">Quantas linhas do histórico o retrato leva.</param>
    public TerminalClient Attach(TerminalClientKind kind, string label, int snapshotScrollback = VtScreen.DefaultScrollback)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var client = new TerminalClient(this, kind, Label(label), _time.GetUtcNow(), Math.Max(0, snapshotScrollback));

        lock (_gate)
        {
            _clients.Add(client);
            ApplySizeOwnerLocked();
            BroadcastStateLocked();
        }

        LogAttached(SessionId, client.Id, kind);
        RaiseChanged();
        return client;
    }

    /// <summary>
    /// Um cliente pede uma ação. Recusa (com <see cref="DomainException"/> e o
    /// motivo) o que não cabe agora: prompt com o Claude trabalhando, resposta
    /// a um diálogo que já fechou, tecla de longe sem a vez.
    /// </summary>
    public async Task SendAsync(TerminalClient client, TerminalInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(input);

        byte[]? bytes;
        bool changed;

        lock (_gate)
        {
            if (!_clients.Contains(client))
            {
                throw new DomainException("A conexão com o terminal já foi fechada.");
            }

            (bytes, changed) = input switch
            {
                TerminalKeys keys => (KeysLocked(client, keys.Data), true),
                TerminalPrompt prompt => (PromptLocked(client, prompt.Text), false),
                TerminalChoice choice => (ChoiceLocked(client, choice), true),
                TerminalInterrupt => (InterruptLocked(client), false),
                TerminalResize resize => (null, ResizeLocked(client, resize.Size)),
                TerminalTakeControl => (null, TakeLocked(client)),
                TerminalReleaseControl => (null, ReleaseLocked(client)),
                TerminalResync => (null, client.RequestResync()),
                _ => throw new DomainException("Ação de terminal desconhecida."),
            };

            if (changed)
            {
                BroadcastStateLocked();
            }
        }

        if (changed)
        {
            RaiseChanged();
        }

        if (bytes is { Length: > 0 })
        {
            await _terminal.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Um aviso dos hooks desta sessão. Atualiza a atividade e o diálogo, e
    /// chama atenção quando o Claude passa a esperar alguém.
    /// </summary>
    public void Observe(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        TerminalAttentionEventArgs? attention = null;

        lock (_gate)
        {
            if (_process != TerminalProcessState.Running)
            {
                return;
            }

            if (agentEvent.Metadata?.GetValueOrDefault("permission_mode") is { } mode)
            {
                _permissionMode = mode;
                _bypassed |= mode == "bypassPermissions";
            }

            if (agentEvent.Activity is { } activity)
            {
                var opened = activity == AgentActivity.WaitingForUser ? OpenDialogLocked(agentEvent) : null;

                if (activity != AgentActivity.WaitingForUser)
                {
                    _dialog = null;
                }

                var previous = _activity;
                _activity = activity;
                _activityMessage = _dialog?.Message ?? Truncate(agentEvent.Message);

                if (opened is not null || (activity is AgentActivity.WaitingReview or AgentActivity.Failed && previous != activity))
                {
                    attention = new TerminalAttentionEventArgs(activity, _activityMessage, opened);
                }
            }
            else if (agentEvent.Type == AgentEventType.SessionStopped)
            {
                _dialog = null;
            }

            BroadcastStateLocked();
        }

        RaiseChanged();

        if (attention is not null)
        {
            AttentionNeeded?.Invoke(this, attention);
        }
    }

    /// <summary>
    /// Encerra a sessão. Sem <paramref name="force"/>, como fechar a janela do
    /// terminal: o Claude recebe o fim do terminal e sai por conta própria,
    /// com a conversa salva. Com <paramref name="force"/>, a árvore de
    /// processos da sessão — e só ela — é encerrada na hora.
    /// </summary>
    public void Stop(bool force)
    {
        LogStopping(SessionId, force);

        if (force)
        {
            _terminal.Kill();
        }
        else
        {
            _terminal.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _terminal.DisposeAsync().ConfigureAwait(false);
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lock (_gate)
        {
            foreach (var client in _clients)
            {
                client.Complete();
            }

            _clients.Clear();
        }

        _stopping.Dispose();
    }

    internal void Detach(TerminalClient client)
    {
        bool removed;

        lock (_gate)
        {
            removed = _clients.Remove(client);

            if (removed)
            {
                if (_lease?.Client == client)
                {
                    _lease = null;
                }

                client.Complete(discard: true);
                ApplySizeOwnerLocked();
                BroadcastStateLocked();
            }
        }

        if (removed)
        {
            LogDetached(SessionId, client.Id, client.Kind);
            RaiseChanged();
        }
    }

    /// <summary>O retrato para um cliente que perdeu saída, já na posição em que a saída seguinte começa.</summary>
    internal TerminalOutput SnapshotFor(TerminalClient client)
    {
        lock (_gate)
        {
            client.ResumeAfterSnapshot();
            return new TerminalOutput(_offset, _screen.Snapshot(client.SnapshotScrollback), Reset: true);
        }
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[ReadBufferSize];

        try
        {
            int count;

            while ((count = await _terminal.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false)) > 0)
            {
                byte[] replies;

                lock (_gate)
                {
                    var data = buffer.AsSpan(0, count).ToArray();
                    _screen.Feed(data);
                    replies = _screen.DrainReplies();
                    var output = new TerminalOutput(_offset, data, Reset: false);
                    _offset += count;
                    _lastOutputAt = _time.GetUtcNow();

                    foreach (var client in _clients)
                    {
                        client.EnqueueOutput(output);
                    }
                }

                if (replies.Length > 0)
                {
                    await WriteQuietlyAsync(replies).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }

        int? exitCode;

        try
        {
            exitCode = await _terminal.Exited.WaitAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            exitCode = null;
        }

        lock (_gate)
        {
            _exitCode = exitCode;
            _process = exitCode == 0 ? TerminalProcessState.Exited : TerminalProcessState.Crashed;
            _dialog = null;
            _lease = null;
            BroadcastStateLocked();
        }

        LogEnded(SessionId, exitCode);
        RaiseChanged();
    }

    private async Task WriteQuietlyAsync(byte[] data)
    {
        try
        {
            await _terminal.WriteAsync(data, _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DomainException or OperationCanceledException)
        {
        }
    }

    private byte[] KeysLocked(TerminalClient client, byte[] data)
    {
        RequireRunningLocked();

        if (data.Length > MaxKeysLength)
        {
            throw new DomainException("Texto demais de uma vez para o terminal.");
        }

        if (client.Kind == TerminalClientKind.NativeWindow && TerminalReplies.IsPassive(data))
        {
            // A janela respondendo ao Claude ou relatando o mouse: chega a ele, sem tirar a vez de ninguém.
            return data;
        }

        if (client.Kind == TerminalClientKind.Remote)
        {
            if (LeaseLocked()?.Client != client)
            {
                throw new DomainException("Assuma o controle do terminal antes de digitar.");
            }
        }
        else if (LeaseLocked() is { } lease && lease.Client != client && lease.Client.Kind == TerminalClientKind.Remote)
        {
            lease.Client.EnqueueNotice("Alguém digitou na máquina: o controle voltou para lá.");
            LogLease(SessionId, client.Id, client.Kind);
            _lease = null;
        }

        if (client.Kind == TerminalClientKind.Remote)
        {
            _lease!.LastInputAt = _time.GetUtcNow();
        }

        _keysSinceDialog |= _dialog is not null;
        return data;
    }

    private byte[] PromptLocked(TerminalClient client, string text)
    {
        RequireStructuredLocked(client);

        if (_dialog is not null)
        {
            throw new DomainException("O Claude está esperando uma resposta no terminal. Responda o pedido antes de mandar outro prompt.");
        }

        if (_activity == AgentActivity.Working)
        {
            throw new DomainException("O Claude ainda está trabalhando. Espere ele terminar ou interrompa.");
        }

        var normalized = (text ?? string.Empty).ReplaceLineEndings("\n").Trim();

        if (normalized.Length == 0)
        {
            throw new DomainException("Escreva o prompt.");
        }

        if (normalized.Length > MaxPromptLength)
        {
            throw new DomainException($"O prompt não pode passar de {MaxPromptLength} caracteres.");
        }

        if (normalized.Any(c => char.IsControl(c) && c is not '\n' and not '\t'))
        {
            throw new DomainException("O prompt tem caracteres de controle. Use as teclas do terminal para isso.");
        }

        if (!_screen.BracketedPaste && normalized.Contains('\n', StringComparison.Ordinal))
        {
            throw new DomainException("O terminal não aceita texto colado agora: mande o prompt numa linha só.");
        }

        // Como quem cola o texto e aperta Enter: chega inteiro, sem cada linha virar um envio.
        var paste = _screen.BracketedPaste ? $"\u001b[200~{normalized}\u001b[201~\r" : normalized + "\r";
        _activity = AgentActivity.Working;
        return Encoding.UTF8.GetBytes(paste);
    }

    private byte[] ChoiceLocked(TerminalClient client, TerminalChoice choice)
    {
        RequireStructuredLocked(client);

        if (_dialog is not { } dialog || dialog.Id != choice.DialogId)
        {
            throw new DomainException("Esse pedido já foi respondido ou fechou. Veja o terminal.");
        }

        if (!dialog.AnswerableFromTheApp)
        {
            throw new DomainException("Essa pergunta se responde no terminal, com as teclas.");
        }

        if (choice.Allow && _keysSinceDialog)
        {
            // O Enter confirma a opção marcada, e alguém já pode ter mudado qual é.
            throw new DomainException("Alguém já mexeu nesse pedido pelo terminal. Responda por lá.");
        }

        _dialog = null;
        _activity = choice.Allow ? AgentActivity.Working : _activity;
        return choice.Allow ? "\r"u8.ToArray() : "\u001b"u8.ToArray();
    }

    private byte[] InterruptLocked(TerminalClient client)
    {
        RequireStructuredLocked(client);

        if (_activity != AgentActivity.Working || _dialog is not null)
        {
            throw new DomainException("O Claude não está trabalhando agora: não há o que interromper.");
        }

        return "\u001b"u8.ToArray();
    }

    private bool ResizeLocked(TerminalClient client, TerminalSize size)
    {
        if (!size.IsValid)
        {
            throw new DomainException("Tamanho de terminal fora dos limites.");
        }

        client.RequestedSize = size;

        if (SizeOwnerLocked() != client || size == _screen.Size)
        {
            return false;
        }

        ResizeTerminalLocked(size);
        return true;
    }

    private bool TakeLocked(TerminalClient client)
    {
        RequireRunningLocked();

        if (LeaseLocked() is { } lease && lease.Client == client)
        {
            return false;
        }

        if (_lease is { } previous)
        {
            previous.Client.EnqueueNotice($"{client.Label} assumiu o controle do terminal.");
        }

        _lease = new TerminalLeaseState(client, _time.GetUtcNow());
        LogLease(SessionId, client.Id, client.Kind);
        ApplySizeOwnerLocked();
        return true;
    }

    private bool ReleaseLocked(TerminalClient client)
    {
        if (_lease?.Client != client)
        {
            return false;
        }

        _lease = null;
        ApplySizeOwnerLocked();
        return true;
    }

    /// <summary>Prompt, resposta, interromper: pedem os hooks e não passam por cima de quem tem a vez.</summary>
    private void RequireStructuredLocked(TerminalClient client)
    {
        RequireRunningLocked();

        if (!Info.Monitored)
        {
            throw new DomainException("Esta sessão abriu sem acompanhamento: o app não sabe o estado do Claude. Use as teclas do terminal.");
        }

        if (LeaseLocked() is { } lease && lease.Client != client)
        {
            throw new DomainException($"{lease.Client.Label} está com o controle do terminal agora.");
        }
    }

    private void RequireRunningLocked()
    {
        if (_process != TerminalProcessState.Running)
        {
            throw new DomainException("O Claude desta sessão já saiu.");
        }
    }

    /// <summary>A vez de digitar, já sem a de longe vencida.</summary>
    private TerminalLeaseState? LeaseLocked()
    {
        if (_lease is { Client.Kind: TerminalClientKind.Remote } lease && _time.GetUtcNow() - lease.LastInputAt > RemoteLeaseIdle)
        {
            lease.Client.EnqueueNotice("O controle do terminal venceu por falta de uso.");
            _lease = null;
        }

        return _lease;
    }

    private TerminalDialog? OpenDialogLocked(AgentEvent agentEvent)
    {
        var kind = agentEvent.SourceEvent switch
        {
            "PermissionRequest" => Info.SupportsPermissionChoices ? TerminalDialogKind.Permission : TerminalDialogKind.Input,
            "Notification" when agentEvent.Metadata?.GetValueOrDefault("notification_type") == "permission_prompt" => TerminalDialogKind.Permission,
            "PreToolUse" when agentEvent.Metadata?.GetValueOrDefault("tool_name") == "AskUserQuestion" => TerminalDialogKind.Question,
            "PreToolUse" when agentEvent.Metadata?.GetValueOrDefault("tool_name") == "ExitPlanMode" => TerminalDialogKind.Plan,
            _ => TerminalDialogKind.Input,
        };

        if (_dialog is { } current && current.Kind == kind)
        {
            // O aviso que vem depois do mesmo pedido (Notification depois do
            // PermissionRequest): o diálogo é o mesmo, e o texto mais rico fica.
            if (agentEvent.SourceEvent == "PermissionRequest" && agentEvent.Message is { } message)
            {
                _dialog = current with { Message = Truncate(message)! };
            }

            return null;
        }

        _dialog = new TerminalDialog(
            Guid.CreateVersion7(_time.GetUtcNow()),
            kind,
            Truncate(agentEvent.Message) ?? "O Claude precisa de você.",
            _time.GetUtcNow());
        _keysSinceDialog = false;
        return _dialog;
    }

    /// <summary>
    /// Quem dita o tamanho: a janela nativa, se houver (é ela que a pessoa
    /// redimensiona com o mouse); senão quem tem a vez; senão o app desta
    /// máquina; senão quem olha de longe.
    /// </summary>
    private TerminalClient? SizeOwnerLocked() =>
        _clients.FirstOrDefault(client => client.Kind == TerminalClientKind.NativeWindow)
        ?? _lease?.Client
        ?? _clients.FirstOrDefault(client => client.Kind == TerminalClientKind.Embedded)
        ?? _clients.FirstOrDefault();

    private void ApplySizeOwnerLocked()
    {
        // Com a janela nativa ligada, é ela quem responde às consultas do Claude.
        _screen.AnswerQueries = _clients.All(client => client.Kind != TerminalClientKind.NativeWindow);

        if (SizeOwnerLocked()?.RequestedSize is { } size && size != _screen.Size && _process == TerminalProcessState.Running)
        {
            ResizeTerminalLocked(size);
        }
    }

    private void ResizeTerminalLocked(TerminalSize size)
    {
        _terminal.Resize(size);
        _screen.Resize(size);
    }

    private void BroadcastStateLocked()
    {
        var message = new TerminalStateMessage(StatusLocked());

        foreach (var client in _clients)
        {
            client.EnqueueState(message);
        }
    }

    private TerminalSessionStatus StatusLocked()
    {
        var lease = LeaseLocked();

        return new TerminalSessionStatus(
            Info.SessionId,
            Info.TaskId,
            Info.DevelopmentId,
            Info.WorkingDirectory,
            _terminal.ProcessId,
            _terminal.ProcessStartedAt,
            _screen.Size,
            _process,
            _exitCode,
            _activity,
            _activityMessage,
            _dialog,
            lease is null ? null : new TerminalLease(lease.Client.Id, lease.Client.Kind, lease.Client.Label, lease.Since),
            [.. _clients.Select(client => new TerminalClientView(client.Id, client.Kind, client.Label, client.AttachedAt))],
            Info.Monitored,
            _bypassed,
            _permissionMode,
            _lastOutputAt, ProviderId: Info.ProviderId);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static string Label(string label)
    {
        var normalized = (label ?? string.Empty).Trim();
        return normalized.Length switch
        {
            0 => "Outro aparelho",
            > AgentSession.MaxRemoteOriginLength => normalized[..AgentSession.MaxRemoteOriginLength],
            _ => normalized,
        };
    }

    private static string? Truncate(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null
            : normalized.Length > AgentSession.MaxActivityMessageLength ? normalized[..AgentSession.MaxActivityMessageLength]
            : normalized;
    }

    [LoggerMessage(EventId = 9801, Level = LogLevel.Information, Message = "TerminalClientAttached {SessionId} {ClientId} {Kind}")]
    private partial void LogAttached(Guid sessionId, Guid clientId, TerminalClientKind kind);

    [LoggerMessage(EventId = 9802, Level = LogLevel.Information, Message = "TerminalClientDetached {SessionId} {ClientId} {Kind}")]
    private partial void LogDetached(Guid sessionId, Guid clientId, TerminalClientKind kind);

    [LoggerMessage(EventId = 9803, Level = LogLevel.Information, Message = "TerminalLeaseChanged {SessionId} {ClientId} {Kind}")]
    private partial void LogLease(Guid sessionId, Guid clientId, TerminalClientKind kind);

    [LoggerMessage(EventId = 9804, Level = LogLevel.Information, Message = "TerminalSessionStopping {SessionId} {Force}")]
    private partial void LogStopping(Guid sessionId, bool force);

    [LoggerMessage(EventId = 9805, Level = LogLevel.Information, Message = "TerminalSessionEnded {SessionId} {ExitCode}")]
    private partial void LogEnded(Guid sessionId, int? exitCode);

    private sealed class TerminalLeaseState(TerminalClient client, DateTimeOffset since)
    {
        public TerminalClient Client { get; } = client;

        public DateTimeOffset Since { get; } = since;

        public DateTimeOffset LastInputAt { get; set; } = since;
    }
}

/// <summary>Por que chamar quem está perto: o diálogo que abriu, ou a resposta que terminou.</summary>
public sealed class TerminalAttentionEventArgs(AgentActivity activity, string? message, TerminalDialog? dialog) : EventArgs
{
    public AgentActivity Activity { get; } = activity;

    public string? Message { get; } = message;

    public TerminalDialog? Dialog { get; } = dialog;
}

/// <summary>
/// Um cliente ligado a uma <see cref="TerminalSessionHost"/>. Ler é seguir a
/// sessão; <see cref="Dispose"/> é desligar, e a sessão continua.
/// </summary>
public sealed class TerminalClient : IDisposable
{
    private readonly TerminalSessionHost _host;

    private readonly Channel<TerminalMessage> _messages = Channel.CreateUnbounded<TerminalMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private long _pendingBytes;

    /// <summary>Perdeu saída (ou acabou de ligar): a próxima leitura é o retrato.</summary>
    private bool _gapped = true;

    /// <summary>Desligado por quem leu: o que sobrou na fila não interessa mais.</summary>
    private bool _detached;

    internal TerminalClient(TerminalSessionHost host, TerminalClientKind kind, string label, DateTimeOffset attachedAt, int snapshotScrollback)
    {
        _host = host;
        Kind = kind;
        Label = label;
        AttachedAt = attachedAt;
        SnapshotScrollback = snapshotScrollback;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public TerminalClientKind Kind { get; }

    public string Label { get; }

    public DateTimeOffset AttachedAt { get; }

    public TerminalSessionHost Host => _host;

    internal int SnapshotScrollback { get; }

    /// <summary>O tamanho que a janela deste cliente tem, para quando ele ditar o tamanho.</summary>
    internal TerminalSize? RequestedSize { get; set; }

    /// <summary>A próxima mensagem; <c>null</c> quando o cliente foi desligado e não há mais nada.</summary>
    public async ValueTask<TerminalMessage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (Volatile.Read(ref _detached))
            {
                return null;
            }

            // Sem saída antiga na fila, o retrato vem antes de tudo: o estado e os avisos podem vir depois dele.
            if (Volatile.Read(ref _gapped) && Interlocked.Read(ref _pendingBytes) == 0 && !_messages.Reader.Completion.IsCompleted)
            {
                return _host.SnapshotFor(this);
            }

            if (_messages.Reader.TryRead(out var message))
            {
                switch (message)
                {
                    case TerminalOutput output:
                        Interlocked.Add(ref _pendingBytes, -output.Data.Length);
                        return output;
                    case ResyncMarker:
                        continue;
                    default:
                        return message;
                }
            }

            if (!await _messages.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }
    }

    public Task SendAsync(TerminalInput input, CancellationToken cancellationToken = default) =>
        _host.SendAsync(this, input, cancellationToken);

    public void Dispose() => _host.Detach(this);

    /// <summary>Sob a trava do host: entra na fila, ou marca o buraco se a fila encheu.</summary>
    internal void EnqueueOutput(TerminalOutput output)
    {
        if (_gapped)
        {
            return;
        }

        if (Interlocked.Read(ref _pendingBytes) + output.Data.Length > TerminalSessionHost.MaxPendingBytes)
        {
            _gapped = true;
            return;
        }

        Interlocked.Add(ref _pendingBytes, output.Data.Length);
        _messages.Writer.TryWrite(output);
    }

    internal void EnqueueState(TerminalStateMessage state) => _messages.Writer.TryWrite(state);

    internal void EnqueueNotice(string text) => _messages.Writer.TryWrite(new TerminalNotice(text));

    /// <summary>Sob a trava do host. Devolve <c>false</c>: pedir o retrato não muda o estado de ninguém.</summary>
    internal bool RequestResync()
    {
        _gapped = true;
        _messages.Writer.TryWrite(ResyncMarker.Instance);
        return false;
    }

    /// <summary>Sob a trava do host: o retrato saiu, a saída volta a entrar na fila.</summary>
    internal void ResumeAfterSnapshot() => _gapped = false;

    /// <param name="discard">O próprio cliente desligou: nada mais é lido.</param>
    internal void Complete(bool discard = false)
    {
        Volatile.Write(ref _detached, discard || _detached);
        _messages.Writer.TryComplete();
    }

    /// <summary>Acorda a leitura para ela ver o pedido de retrato.</summary>
    private sealed record ResyncMarker : TerminalMessage
    {
        public static readonly ResyncMarker Instance = new();
    }
}
