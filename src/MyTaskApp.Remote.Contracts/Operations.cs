using System.Text.Json;

namespace MyTaskApp.Remote.Contracts;

/// <summary>
/// O pedido que o controlador assina. O agente lê os bytes assinados, e não uma
/// cópia do servidor (ADR-087).
/// </summary>
/// <param name="OperationId">UUIDv7 do controlador: a chave de idempotência dos dois lados.</param>
/// <param name="TargetDeviceId">Amarra o pedido a uma máquina: outra recusa.</param>
/// <param name="Payload">Um dos pedidos de <see cref="RemoteOperationKinds.PayloadTypeOf"/>.</param>
/// <param name="RequiredEntities">O agente espera o sync trazer estas entidades (a tarefa criada no celular).</param>
/// <param name="NotAfter">Sem aceite até aqui, a operação expira.</param>
/// <param name="RetryOf">"Repetir" cria outra operação, apontando a anterior. Nada se repete sozinho.</param>
/// <param name="SkipSteps">Numa composta, as etapas já concluídas que a repetição pula.</param>
public sealed record RemoteOperationRequest(
    Guid OperationId,
    Guid WorkspaceId,
    Guid TargetDeviceId,
    Guid RequestedByDeviceId,
    string Kind,
    JsonElement Payload,
    Guid? TaskId,
    IReadOnlyList<Guid> RequiredEntities,
    DateTimeOffset CreatedAt,
    DateTimeOffset NotAfter,
    Guid? RetryOf = null,
    IReadOnlyList<string>? SkipSteps = null);

/// <summary>JWS compacto ES256: cabeçalho com <c>alg</c> e <c>kid</c> (a impressão digital), payload em JSON.</summary>
public sealed record SignedEnvelope(string Jws);

public static class RemoteOperationKinds
{
    public const string EnvironmentInspect = "environment.inspect";
    public const string WorktreeProvision = "worktree.provision";
    public const string WorktreeRemove = "worktree.remove";
    public const string CommandRun = "command.run";
    public const string AgentStart = "agent.start";
    public const string AgentPrompt = "agent.prompt";
    public const string AgentStop = "agent.stop";
    public const string DatabaseCopy = "database.copy";

    private static readonly Dictionary<string, Type> Payloads = new(StringComparer.Ordinal)
    {
        [EnvironmentInspect] = typeof(EnvironmentInspectionRequest),
        [WorktreeProvision] = typeof(EnvironmentProvisioningRequest),
        [WorktreeRemove] = typeof(WorktreeRemovalRequest),
        [CommandRun] = typeof(WorktreeExecutionRequest),
        [AgentStart] = typeof(ClaudeCodeSessionRequest),
        [AgentPrompt] = typeof(PromptRequest),
        [AgentStop] = typeof(AgentStopRequest),
        [DatabaseCopy] = typeof(DatabaseCopyRequest),
    };

    /// <summary>O catálogo fechado (ADR-088): o que não está aqui não existe.</summary>
    public static IReadOnlyCollection<string> All => Payloads.Keys;

    public static bool IsKnown(string? kind) => kind is not null && Payloads.ContainsKey(kind);

    /// <summary>O tipo do <see cref="RemoteOperationRequest.Payload"/> de cada operação, ou nulo para um tipo desconhecido.</summary>
    public static Type? PayloadTypeOf(string kind) => Payloads.GetValueOrDefault(kind);
}

public enum RemoteOperationStatus
{
    /// <summary>Gravada no servidor.</summary>
    Queued = 1,

    /// <summary>O agente buscou.</summary>
    Dispatched = 2,

    /// <summary>O agente conferiu assinatura e política, e gravou no diário.</summary>
    Accepted = 3,

    Running = 4,

    /// <summary>Parada esperando um humano.</summary>
    WaitingApproval = 5,

    Succeeded = 6,
    Failed = 7,
    Canceled = 8,

    /// <summary>O agente recusou: assinatura, catálogo ou política.</summary>
    Rejected = 9,

    /// <summary>Passou do <see cref="RemoteOperationRequest.NotAfter"/> sem ser aceita.</summary>
    Expired = 10,

    /// <summary>O agente caiu no meio de uma etapa que não se retoma.</summary>
    Interrupted = 11,
}

/// <summary>Quem pode decidir uma transição: até o aceite, o servidor; depois, o agente.</summary>
public enum StatusAuthority
{
    Server,
    Agent,
}
