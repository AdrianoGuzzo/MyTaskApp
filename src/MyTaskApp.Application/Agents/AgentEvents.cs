using System.Security.Cryptography;
using System.Text;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Agents;

/// <summary>
/// O que aconteceu dentro do agente, já traduzido do formato dele (ADR-036).
/// Quem traduz é a borda (o hook do Claude vira um destes); daqui para dentro
/// ninguém sabe o nome de evento de agente nenhum.
/// </summary>
public enum AgentEventType
{
    /// <summary>A conversa começou (ou recomeçou, depois de um <c>/clear</c>).</summary>
    SessionStarted,

    /// <summary>Recebeu uma mensagem ou usou uma ferramenta: está trabalhando.</summary>
    Working,

    /// <summary>Parou numa pergunta, numa permissão ou num plano para aprovar.</summary>
    NeedsUserInput,

    /// <summary>A resposta terminou — o fim de uma etapa, não o da sessão.</summary>
    ResponseCompleted,

    /// <summary>Uma tarefa da lista interna do agente foi concluída. Só registro.</summary>
    TaskCompleted,

    /// <summary>A conversa acabou. Quem encerra a sessão continua sendo o processo (ADR-030).</summary>
    SessionStopped,

    /// <summary>A resposta terminou em erro: limite, API fora, autenticação.</summary>
    SessionFailed,

    /// <summary>Um aviso do agente que não muda o que ele está fazendo.</summary>
    Notification,
}

/// <summary>
/// Um aviso de um agente, a caminho da sessão que ele diz ser.
/// </summary>
/// <param name="AgentSessionId">
/// A sessão do MyTaskApp, que o próprio app pôs no ambiente do processo — a
/// associação é explícita, nunca adivinhada pela pasta ou pelo título.
/// </param>
/// <param name="TaskId">A tarefa que o processo diz ser; conferida contra a da sessão.</param>
/// <param name="ExternalSessionId">O id que o agente dá à conversa (o <c>session_id</c> do Claude).</param>
/// <param name="SourceEvent">O nome original do evento (<c>Stop</c>, <c>Notification</c>…), para o log.</param>
/// <param name="Metadata">Detalhes do evento que só interessam ao log (<c>notification_type</c>, <c>reason</c>).</param>
public sealed record AgentEvent(
    AgentEventType Type,
    Guid AgentSessionId,
    Guid? TaskId,
    string? ExternalSessionId,
    string? WorkingDirectory,
    string? Message,
    DateTimeOffset ReceivedAt,
    string SourceEvent,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    /// <summary>
    /// Para onde o evento leva a atividade da sessão; <c>null</c> = não muda.
    /// "Terminou a resposta" é <see cref="AgentActivity.WaitingReview"/>, e não
    /// o fim da sessão: o Claude continua aberto esperando a próxima mensagem.
    /// </summary>
    public AgentActivity? Activity => Type switch
    {
        AgentEventType.Working => AgentActivity.Working,
        AgentEventType.NeedsUserInput => AgentActivity.WaitingForUser,
        AgentEventType.ResponseCompleted => AgentActivity.WaitingReview,
        AgentEventType.SessionFailed => AgentActivity.Failed,
        _ => null,
    };
}

/// <summary>
/// O endereço local em que o app recebe os avisos dos agentes (ADR-036).
/// </summary>
public interface IAgentEventEndpoint
{
    /// <summary>
    /// <c>http://127.0.0.1:{porta}/…</c>; <c>null</c> enquanto não está
    /// ouvindo — e aí o agente abre sem acompanhamento, em vez de não abrir.
    /// </summary>
    Uri? Address { get; }

    /// <summary>Começa a ouvir. Idempotente; nunca lança — sem porta, <see cref="Address"/> fica <c>null</c>.</summary>
    void Start();
}

/// <summary>
/// O que o processo do agente recebe no ambiente para se identificar
/// (ADR-036): a sessão, a tarefa, o ambiente e o segredo.
/// </summary>
public static class AgentMonitoringEnvironment
{
    public const string TaskId = "MYTASKAPP_TASK_ID";

    /// <summary>O ambiente da tarefa (ADR-031): o repositório em que o agente roda.</summary>
    public const string DevelopmentId = "MYTASKAPP_DEVELOPMENT_ID";

    /// <summary>A sessão do agente — a execução que o app acompanha.</summary>
    public const string AgentSessionId = "MYTASKAPP_AGENT_SESSION_ID";

    public const string WorktreePath = "MYTASKAPP_WORKTREE_PATH";

    public const string Branch = "MYTASKAPP_BRANCH";

    /// <summary>O segredo que prova que o aviso veio do processo que o app abriu.</summary>
    public const string HookToken = "MYTASKAPP_HOOK_TOKEN";

    /// <summary>Para onde mandar os avisos — informativo, para scripts do usuário.</summary>
    public const string EventsUrl = "MYTASKAPP_EVENTS_URL";

    public static IReadOnlyDictionary<string, string> For(
        AgentSession session,
        string worktreePath,
        string branch,
        string token,
        Uri endpoint) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TaskId] = session.TaskItemId.ToString(),
            [DevelopmentId] = session.TaskDevelopmentId?.ToString() ?? string.Empty,
            [AgentSessionId] = session.Id.ToString(),
            [WorktreePath] = worktreePath,
            [Branch] = branch,
            [HookToken] = token,
            [EventsUrl] = endpoint.ToString(),
        };
}

/// <summary>
/// O segredo por sessão (ADR-036). O processo recebe o segredo; o banco guarda
/// só o hash. A porta local aceita qualquer processo do computador, e é o
/// segredo que separa o Claude que o app abriu de quem só sabe o endereço.
/// </summary>
public static class AgentHookToken
{
    public static (string Token, string Hash) Create()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return (token, Hash(token));
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Tempo constante: a comparação não diz quantos caracteres acertaram.</summary>
    public static bool Matches(string? token, string? hash)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(hash))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(token)),
            Encoding.ASCII.GetBytes(hash.ToUpperInvariant()));
    }
}

/// <summary>Um agente parou esperando o usuário: o que o aviso mostra.</summary>
public sealed record AgentAttention(
    Guid SessionId,
    Guid TaskId,
    Guid? DevelopmentId,
    string TaskTitle,
    string AgentName,
    string? RepositoryName,
    string? Branch,
    AgentActivity Activity,
    string? Message,
    DateTimeOffset At);

/// <summary>
/// Põe na tela o aviso de que um agente precisa do usuário (ADR-036). A
/// implementação é da borda (Desktop), como a dos lembretes.
/// </summary>
public interface IAgentAttentionPresenter
{
    /// <summary>Mostra, ou atualiza no lugar, o aviso desta sessão.</summary>
    Task PresentAsync(AgentAttention attention, CancellationToken cancellationToken = default);

    /// <summary>Tira da tela o aviso desta sessão, se houver.</summary>
    Task DismissAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Sem tela (testes, host sem UI): ninguém para avisar.</summary>
public sealed class NoAgentAttentionPresenter : IAgentAttentionPresenter
{
    public Task PresentAsync(AgentAttention attention, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DismissAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
