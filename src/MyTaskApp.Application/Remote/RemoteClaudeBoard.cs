using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>De onde vem o estado: o terminal do agente (ADR-099) ou uma conversa sem terminal (ADR-091).</summary>
public enum RemoteClaudeKind
{
    Terminal,
    Conversation,
}

/// <summary>O que o Claude de longe pede de quem olha, do mais ao menos urgente.</summary>
public enum RemoteClaudeAttention
{
    None,

    /// <summary>Espera a pessoa: uma pergunta, uma permissão.</summary>
    Waiting,

    /// <summary>Terminou a resposta e espera o próximo prompt.</summary>
    Review,

    /// <summary>A última resposta terminou em erro.</summary>
    Failed,
}

/// <summary>
/// O Claude de outra máquina da conta numa tarefa (ADR-103): o que a linha da
/// tarefa e o aviso mostram, no desktop e no celular.
/// </summary>
/// <param name="CanFollow">
/// Dá para seguir a tela: é um terminal, a máquina aceita o terminal remoto e a
/// sessão é de um ambiente da tarefa. Sem isso, só o estado.
/// </param>
/// <param name="AttentionSince">
/// Quando este quadro viu começar o pedido atual: é a chave do "já vi". O
/// relato não traz o instante da mudança, e o contrato é estrito (ADR-103).
/// </param>
/// <param name="Version">A versão da conversa (só nas conversas): é como a verificação em segundo plano anota.</param>
/// <param name="Prompt">
/// A operação do prompt atual da conversa: um prompt inteiro entre duas
/// leituras volta ao mesmo "terminou", e é ela que diz que é outro pedido.
/// </param>
public sealed record RemoteClaudeState(
    Guid DeviceId,
    string MachineName,
    Guid SessionId,
    Guid TaskId,
    Guid? DevelopmentId,
    RemoteClaudeKind Kind,
    RemoteAgentActivity Activity,
    bool HasDialog,
    string? Message,
    bool CanFollow,
    RemoteClaudeAttention Attention,
    DateTimeOffset? AttentionSince,
    long? Version = null,
    Guid? Prompt = null)
{
    public bool NeedsAttention => Attention != RemoteClaudeAttention.None;

    /// <summary>A atividade como o desktop local a chama: o som, o selo e o "já vi" são os mesmos.</summary>
    public AgentActivity LocalActivity => Attention switch
    {
        RemoteClaudeAttention.Waiting => AgentActivity.WaitingForUser,
        RemoteClaudeAttention.Review => AgentActivity.WaitingReview,
        RemoteClaudeAttention.Failed => AgentActivity.Failed,
        _ => Activity == RemoteAgentActivity.Working ? AgentActivity.Working : AgentActivity.Unknown,
    };
}

/// <summary>Um pedido novo de um Claude de longe: sai uma vez por pedido.</summary>
public sealed class RemoteClaudeAttentionEventArgs(RemoteClaudeState state) : EventArgs
{
    public RemoteClaudeState State { get; } = state;
}

/// <summary>
/// O estado do Claude das outras máquinas da conta, por tarefa (ADR-103). Lê
/// os terminais e as conversas que os agentes relatam ao servidor; com o hub
/// ligado, relê no aviso, e a leitura por tempo só cobre o aviso perdido.
/// </summary>
/// <remarks>
/// <para>
/// Sem o remoto ligado, ou sem outra máquina que aceite controle, não segura o
/// hub e só confere a configuração de minuto em minuto: quem tem uma máquina
/// só não paga a conexão.
/// </para>
/// <para>
/// O "pedido novo" é observado aqui: o relato não traz o instante da mudança.
/// A primeira leitura depois de <see cref="Start"/> só anota, como a
/// verificação em segundo plano: o que já esperava antes não é novidade.
/// </para>
/// </remarks>
public sealed partial class RemoteClaudeBoard(
    IUseCaseRunner runner,
    ISyncAccount account,
    IRemoteWatch watch,
    TimeProvider timeProvider,
    ILogger<RemoteClaudeBoard> logger) : IAsyncDisposable, IDisposable
{
    /// <summary>Remoto desligado ou sem outra máquina: só confere de novo depois disso.</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(1);

    /// <summary>Sem hub, a leitura por tempo.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    /// <summary>Com o hub, a leitura de reserva.</summary>
    public static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(60);

    /// <summary>Quanto vale a lista de máquinas (apelido, terminal oferecido).</summary>
    public static readonly TimeSpan MachinesLifetime = TimeSpan.FromMinutes(5);

    /// <summary>A conversa parada esperando o próximo prompt aparece até isso depois do último relato.</summary>
    public static readonly TimeSpan IdleConversationWindow = TimeSpan.FromHours(24);

    /// <summary>A conversa que falhou aparece até isso depois.</summary>
    public static readonly TimeSpan FailedConversationWindow = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Lock _loopGate = new();

    /// <summary>O pedido atual de cada sessão: o tipo, a operação (nas conversas) e quando começou.</summary>
    private readonly Dictionary<Guid, Episode> _episodes = [];

    private RemotePace? _pace;

    private bool _listening;

    private CancellationTokenSource? _loop;

    private bool _baseline = true;

    private IReadOnlyDictionary<Guid, MachineView> _machines = new Dictionary<Guid, MachineView>();

    private DateTimeOffset _machinesAt = DateTimeOffset.MinValue;

    private Guid? _thisDevice;

    /// <summary>Os estados de agora, os que mais pedem alguém primeiro.</summary>
    public IReadOnlyList<RemoteClaudeState> States { get; private set; } = [];

    /// <summary>A lista mudou; pode vir de fora da thread da tela.</summary>
    public event EventHandler? Changed;

    /// <summary>Um Claude de longe passou a pedir alguém; uma vez por pedido.</summary>
    public event EventHandler<RemoteClaudeAttentionEventArgs>? AttentionRaised;

    /// <summary>O pedido acabou (respondido, voltou a trabalhar, sessão encerrada): o id da sessão.</summary>
    public event EventHandler<Guid>? AttentionCleared;

    /// <summary>Quantas leituras terminaram (os testes esperam por elas).</summary>
    public int Refreshes { get; private set; }

    /// <summary>O quadro está segurando o hub: há outra máquina para ouvir.</summary>
    public bool IsListening => _listening;

    public IReadOnlyList<RemoteClaudeState> ForTask(Guid taskId) => [.. States.Where(state => state.TaskId == taskId)];

    /// <summary>Começa a ler; de novo, não faz nada. A primeira leitura só anota.</summary>
    public void Start()
    {
        lock (_loopGate)
        {
            if (_loop is not null)
            {
                return;
            }

            _baseline = true;
            _loop = new CancellationTokenSource();
            _ = RunAsync(_loop.Token);
        }
    }

    /// <summary>Para de ler e solta o hub. Os pedidos já vistos ficam: voltar não avisa de novo.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? loop;

        lock (_loopGate)
        {
            loop = _loop;
            _loop = null;
        }

        if (loop is not null)
        {
            await loop.CancelAsync();
            loop.Dispose();
        }

        await StopListeningAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Síncrono também: o contêiner do desktop é descartado com um <c>using</c> comum.</summary>
    public void Dispose()
    {
        CancellationTokenSource? loop;

        lock (_loopGate)
        {
            loop = _loop;
            _loop = null;
        }

        loop?.Cancel();
        loop?.Dispose();
        _ = StopListeningAsync();
    }

    /// <summary>Uma leitura agora.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            Refreshes++;
            _gate.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(cancellationToken);

                if (_listening && _pace is { } pace)
                {
                    await pace.WaitAsync(LiveInterval, cancellationToken);
                }
                else
                {
                    await Task.Delay(_machines.Count == 0 ? IdleInterval : PollInterval, timeProvider, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogFailed(exception.GetType().Name);

                try
                {
                    await Task.Delay(PollInterval, timeProvider, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (!account.IsAvailable || !(await runner.RunAsync<GetRemoteSettingsHandler, RemoteSettings>(
                (handler, token) => handler.HandleAsync(new GetRemoteSettings(), token),
                cancellationToken)).Enabled)
        {
            await GoQuietAsync();
            return;
        }

        var now = timeProvider.GetUtcNow();

        if (now - _machinesAt >= MachinesLifetime && !await ReadMachinesAsync(cancellationToken))
        {
            return;
        }

        if (_machines.Count == 0)
        {
            await GoQuietAsync();
            return;
        }

        await StartListeningAsync(cancellationToken);

        IReadOnlyList<RemoteTerminalSessionView> terminals;
        RemoteAgentSessionList conversations;

        try
        {
            terminals = await ReadTerminalsAsync(cancellationToken);
            conversations = await ReadConversationsAsync(cancellationToken);
        }
        catch (SyncApiException exception)
        {
            // Sem rede ou sem login agora: fica o que se via, e a próxima leitura tenta.
            LogUnavailable(exception.Kind);
            return;
        }

        // Uma máquina que relata e ainda não está na lista: lê as máquinas de novo na próxima.
        if (terminals.Any(view => view.DeviceId != _thisDevice && !_machines.ContainsKey(view.DeviceId)))
        {
            _machinesAt = DateTimeOffset.MinValue;
        }

        Publish([.. TerminalStates(terminals), .. ConversationStates(conversations, now)], now);
    }

    private async Task<bool> ReadMachinesAsync(CancellationToken cancellationToken)
    {
        var view = await runner.RunAsync<GetMachinesHandler, MachinesView>(
            (handler, token) => handler.HandleAsync(new GetMachines(), token),
            cancellationToken);

        switch (view.Availability)
        {
            case RemoteAvailability.Ready:
                _thisDevice = view.Machines.FirstOrDefault(machine => machine.IsThis)?.DeviceId ?? _thisDevice;
                _machines = view.Machines
                    .Where(machine => !machine.IsThis && machine.AcceptsControl)
                    .ToDictionary(machine => machine.DeviceId);
                _machinesAt = timeProvider.GetUtcNow();
                return true;

            case RemoteAvailability.Unreachable:
                // Sem resposta agora: fica o que se via.
                return false;

            default:
                // Sem login, servidor antigo: nada para mostrar.
                _machines = new Dictionary<Guid, MachineView>();
                _machinesAt = timeProvider.GetUtcNow();
                return true;
        }
    }

    private async Task<IReadOnlyList<RemoteTerminalSessionView>> ReadTerminalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await runner.RunAsync<IRemoteTerminalsApi, RemoteTerminalSessionList>(
                (api, token) => api.ListTerminalsAsync(token),
                cancellationToken)).Sessions;
        }
        catch (SyncApiException exception) when (exception is { Kind: SyncFailureKind.NotFound, Code: null })
        {
            // Servidor de antes do terminal remoto.
            return [];
        }
    }

    private async Task<RemoteAgentSessionList> ReadConversationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync<IRemoteSessionsApi, RemoteAgentSessionList>(
                (api, token) => api.ListSessionsAsync(null, false, null, token),
                cancellationToken);
        }
        catch (SyncApiException exception) when (exception is { Kind: SyncFailureKind.NotFound, Code: null })
        {
            // Servidor de antes das conversas.
            return new RemoteAgentSessionList([], []);
        }
    }

    private IEnumerable<RemoteClaudeState> TerminalStates(IReadOnlyList<RemoteTerminalSessionView> terminals)
    {
        foreach (var view in terminals)
        {
            var session = view.Session;

            if (session.Process != RemoteTerminalProcess.Running || !_machines.TryGetValue(view.DeviceId, out var machine))
            {
                continue;
            }

            var attention = session switch
            {
                { HasDialog: true } or { Activity: RemoteAgentActivity.WaitingForUser } => RemoteClaudeAttention.Waiting,
                { Activity: RemoteAgentActivity.WaitingReview } => RemoteClaudeAttention.Review,
                { Activity: RemoteAgentActivity.Failed } => RemoteClaudeAttention.Failed,
                _ => RemoteClaudeAttention.None,
            };

            yield return new RemoteClaudeState(
                view.DeviceId,
                machine.Nickname,
                session.SessionId,
                session.TaskId,
                session.DevelopmentId,
                RemoteClaudeKind.Terminal,
                session.Activity,
                session.HasDialog,
                session.ActivityMessage,
                CanFollow: machine.OffersTerminal && session.DevelopmentId is not null,
                attention,
                AttentionSince: null);
        }
    }

    private IEnumerable<RemoteClaudeState> ConversationStates(RemoteAgentSessionList list, DateTimeOffset now)
    {
        var stopped = list.Agents
            .Where(agent => agent.AgentSeenAt is not null && !agent.IsOnlineAt(now))
            .Select(agent => agent.DeviceId)
            .ToHashSet();

        foreach (var session in list.Sessions)
        {
            if (!_machines.TryGetValue(session.DeviceId, out var machine) || stopped.Contains(session.DeviceId) || !IsShown(session, now))
            {
                continue;
            }

            var attention = session switch
            {
                { Status: RemoteAgentSessionStatus.Failed } or { Activity: RemoteAgentActivity.Failed } => RemoteClaudeAttention.Failed,
                { Activity: RemoteAgentActivity.WaitingForUser } => RemoteClaudeAttention.Waiting,
                { Status: RemoteAgentSessionStatus.Idle } => RemoteClaudeAttention.Review,
                _ => RemoteClaudeAttention.None,
            };

            yield return new RemoteClaudeState(
                session.DeviceId,
                machine.Nickname,
                session.SessionId,
                session.TaskId,
                session.DevelopmentId,
                RemoteClaudeKind.Conversation,
                session.Status is RemoteAgentSessionStatus.Starting or RemoteAgentSessionStatus.Running && session.Activity == RemoteAgentActivity.Unknown
                    ? RemoteAgentActivity.Working
                    : session.Activity,
                HasDialog: false,
                session.ActivityMessage,
                CanFollow: false,
                attention,
                AttentionSince: null,
                session.Version,
                session.CurrentOperationId);
        }
    }

    /// <summary>A conversa em curso, a parada há pouco e a que falhou há pouco; a encerrada, não.</summary>
    private static bool IsShown(RemoteAgentSessionView session, DateTimeOffset now) => session.Status switch
    {
        RemoteAgentSessionStatus.Starting or RemoteAgentSessionStatus.Running => true,
        RemoteAgentSessionStatus.Idle => session.EndedAt is null && now - session.UpdatedAt <= IdleConversationWindow,
        RemoteAgentSessionStatus.Failed => now - session.UpdatedAt <= FailedConversationWindow,
        _ => false,
    };

    private void Publish(List<RemoteClaudeState> read, DateTimeOffset now)
    {
        var raised = new List<RemoteClaudeState>();
        var cleared = new List<Guid>();
        var states = new List<RemoteClaudeState>(read.Count);

        foreach (var state in read)
        {
            if (state.Attention == RemoteClaudeAttention.None)
            {
                if (_episodes.Remove(state.SessionId))
                {
                    cleared.Add(state.SessionId);
                }

                states.Add(state);
                continue;
            }

            if (!_episodes.TryGetValue(state.SessionId, out var episode) || episode.Attention != state.Attention || episode.Prompt != state.Prompt)
            {
                episode = new Episode(state.Attention, state.Prompt, now);
                _episodes[state.SessionId] = episode;

                if (!_baseline)
                {
                    raised.Add(state with { AttentionSince = now });
                }
            }

            states.Add(state with { AttentionSince = episode.Since });
        }

        // A sessão que sumiu da lista acabou: o pedido dela também.
        var present = read.Select(state => state.SessionId).ToHashSet();

        foreach (var gone in _episodes.Keys.Where(id => !present.Contains(id)).ToList())
        {
            _episodes.Remove(gone);
            cleared.Add(gone);
        }

        _baseline = false;

        var ordered = states
            .OrderByDescending(state => Urgency(state.Attention))
            .ThenBy(state => state.MachineName, StringComparer.CurrentCulture)
            .ThenBy(state => state.SessionId)
            .ToList();

        var changed = !ordered.SequenceEqual(States);
        States = ordered;

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        foreach (var id in cleared)
        {
            AttentionCleared?.Invoke(this, id);
        }

        foreach (var state in raised)
        {
            AttentionRaised?.Invoke(this, new RemoteClaudeAttentionEventArgs(state));
        }
    }

    internal static int Urgency(RemoteClaudeAttention attention) => attention switch
    {
        RemoteClaudeAttention.Waiting => 3,
        RemoteClaudeAttention.Failed => 2,
        RemoteClaudeAttention.Review => 1,
        _ => 0,
    };

    private async Task GoQuietAsync()
    {
        _machines = new Dictionary<Guid, MachineView>();
        _machinesAt = DateTimeOffset.MinValue;
        await StopListeningAsync();

        // A lista esvazia, mas a primeira leitura de verdade continua só anotando.
        var baseline = _baseline;
        Publish([], timeProvider.GetUtcNow());
        _baseline = baseline;
    }

    private async Task StartListeningAsync(CancellationToken cancellationToken)
    {
        if (_listening)
        {
            return;
        }

        _pace ??= new RemotePace(watch, timeProvider, IsRelevant);
        _listening = true;
        await _pace.StartAsync(cancellationToken);
    }

    private async Task StopListeningAsync()
    {
        if (!_listening || _pace is null)
        {
            return;
        }

        _listening = false;
        await _pace.DisposeAsync();
    }

    /// <summary>Os avisos que pedem leitura: os terminais de outra máquina e as conversas.</summary>
    private bool IsRelevant(RemoteNotice notice) => notice.Kind switch
    {
        RemoteNoticeKind.Terminals => notice.Id != _thisDevice,
        RemoteNoticeKind.Session => true,
        _ => false,
    };

    /// <summary>
    /// Um pedido: o tipo e, na conversa, o prompt. O mesmo tipo com outro
    /// prompt é outro pedido; no terminal, só o tipo distingue (dois diálogos
    /// seguidos entre duas leituras viram um, ADR-103).
    /// </summary>
    private sealed record Episode(RemoteClaudeAttention Attention, Guid? Prompt, DateTimeOffset Since);

    [LoggerMessage(EventId = 9880, Level = LogLevel.Warning, Message = "RemoteClaudeBoardFailed {Error}")]
    private partial void LogFailed(string error);

    [LoggerMessage(EventId = 9881, Level = LogLevel.Debug, Message = "RemoteClaudeBoardUnavailable {Kind}")]
    private partial void LogUnavailable(SyncFailureKind kind);
}
