namespace MyTaskApp.Remote.Contracts;

/// <summary>
/// O estado de uma conversa do Claude sem terminal (ADR-091), como o agente
/// relata. É o estado de execução; se o agente está dando notícias é outra
/// coisa (<see cref="RemoteAgentPresence"/>).
/// </summary>
public enum RemoteAgentSessionStatus
{
    /// <summary>O prompt chegou e o processo está abrindo.</summary>
    Starting,

    /// <summary>O Claude está respondendo um prompt.</summary>
    Running,

    /// <summary>O último prompt acabou; a conversa espera o próximo.</summary>
    Idle,

    /// <summary>Parada a pedido; não recebe mais prompts.</summary>
    Stopped,

    /// <summary>Não chegou a abrir.</summary>
    Failed,
}

/// <summary>O que o Claude está fazendo, pelo que o agente leu da saída.</summary>
public enum RemoteAgentActivity
{
    Unknown,
    Working,
    WaitingForUser,
    WaitingReview,
    Failed,
}

/// <summary>
/// Uma conversa do Claude sem terminal desta máquina, relatada pelo agente
/// (<c>PUT /api/v1/remote/agent/sessions</c>). Só ids e estado: nenhum caminho,
/// comando ou texto de prompt; o título e a branch o controlador lê da tarefa
/// sincronizada.
/// </summary>
/// <param name="Version">Sobe a cada mudança; o servidor guarda só a maior.</param>
/// <param name="CurrentOperationId">A operação do prompt atual (ou do último).</param>
public sealed record RemoteAgentSessionReport(
    Guid SessionId,
    long Version,
    Guid TaskId,
    Guid? DevelopmentId,
    string ProfileId,
    string? Model,
    RemoteAgentSessionStatus Status,
    RemoteAgentActivity Activity,
    string? ActivityMessage,
    Guid? CurrentOperationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// O lote de relatos do agente. Vazio também vale: é o sinal de vida do
/// agente, separado do desktop da mesma instalação.
/// </summary>
public sealed record RemoteAgentSessionBatch(IReadOnlyList<RemoteAgentSessionReport> Sessions);

/// <summary>A versão que o servidor tem de cada conversa do lote, depois de gravar.</summary>
public sealed record RemoteAgentSessionAck(IReadOnlyList<RemoteAgentSessionVersion> Sessions);

public sealed record RemoteAgentSessionVersion(Guid SessionId, long Version);

/// <summary>Uma conversa como a conta vê: o último relato aceito, de qual máquina e quando chegou.</summary>
public sealed record RemoteAgentSessionView(
    Guid SessionId,
    Guid DeviceId,
    long Version,
    Guid TaskId,
    Guid? DevelopmentId,
    string ProfileId,
    string? Model,
    RemoteAgentSessionStatus Status,
    RemoteAgentActivity Activity,
    string? ActivityMessage,
    Guid? CurrentOperationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ReportedAt);

/// <summary>
/// O agente de uma máquina, pelo último relato dele (o lote vazio conta). O
/// desktop aberto não conta: a máquina pode estar ligada com o agente parado.
/// </summary>
public sealed record RemoteAgentPresence(Guid DeviceId, DateTimeOffset? AgentSeenAt)
{
    /// <summary>Deu notícias há no máximo <see cref="RemoteLimits.OfflineAfter"/>.</summary>
    public bool IsOnlineAt(DateTimeOffset now) =>
        AgentSeenAt is { } at && now - at <= RemoteLimits.OfflineAfter;
}

/// <summary>As conversas da conta (<c>GET /api/v1/remote/sessions</c>), as mais recentes primeiro, e os agentes delas.</summary>
public sealed record RemoteAgentSessionList(IReadOnlyList<RemoteAgentPresence> Agents, IReadOnlyList<RemoteAgentSessionView> Sessions);

/// <summary>Uma conversa e as operações dela (os prompts e a parada), da mais antiga à mais nova.</summary>
public sealed record RemoteAgentSessionDetail(
    RemoteAgentSessionView Session,
    RemoteAgentPresence Agent,
    IReadOnlyList<RemoteOperationView> Operations);
