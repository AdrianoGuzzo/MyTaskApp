namespace MyTaskApp.Infrastructure.Sync.Persistence;

/// <summary>Em que pé está a ligação desta instalação com uma conta.</summary>
internal enum SyncLinkStatus
{
    /// <summary>Nunca conectou, ou desconectou: nada é capturado.</summary>
    Unlinked = 0,

    Linked = 1,

    /// <summary>O refresh token não vale mais: precisa entrar de novo pelo navegador.</summary>
    NeedsReconnect = 2,

    /// <summary>Outro dispositivo revogou este: só reconectando, como dispositivo novo.</summary>
    DeviceRevoked = 3,
}

/// <summary>
/// A ligação com a conta, em linha única (ADR-060). Sem segredo: o refresh
/// token fica no cofre do sistema, e o access token só na memória.
/// </summary>
internal sealed class SyncStateRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string? ServerBaseUrl { get; set; }

    public Guid? UserId { get; set; }

    public string? UserDisplayName { get; set; }

    public string? UserEmail { get; set; }

    public Guid? WorkspaceId { get; set; }

    /// <summary>
    /// O dispositivo com que este banco foi ligado. Diferente do
    /// <c>device.json</c> desta máquina = o banco foi copiado de outra.
    /// </summary>
    public Guid? LinkedDeviceId { get; set; }

    public SyncLinkStatus Status { get; set; }

    /// <summary>Ligado pelo usuário. Desligado, captura continua e nada viaja.</summary>
    public bool IsEnabled { get; set; }

    public string Cursor { get; set; } = string.Empty;

    /// <summary>A versão do contrato com que o último pull completo foi feito.</summary>
    public int ClientSchemaVersion { get; set; }

    public DateTimeOffset? LinkedAt { get; set; }

    public DateTimeOffset? LastPushAt { get; set; }

    public DateTimeOffset? LastPullAt { get; set; }

    public DateTimeOffset? LastSuccessAt { get; set; }

    public string? LastErrorCode { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? LastErrorAt { get; set; }
}

/// <summary>
/// O que a sincronização sabe de cada entidade: a versão do servidor, o último
/// estado compartilhado conhecido (de onde sai o diff) e a origem.
/// </summary>
/// <remarks>
/// Uma tabela à parte, e não colunas nas tabelas do domínio: a origem e a
/// versão são da sincronização, e uma instalação que nunca conectou não ganha
/// nenhuma linha aqui.
/// </remarks>
internal sealed class SyncEntityStateRow
{
    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    /// <summary>A versão que o servidor confirmou; 0 enquanto a criação não foi aceita.</summary>
    public long ServerVersion { get; set; }

    /// <summary>
    /// Os campos compartilhados como a sincronização os conhece: o que já foi
    /// para o outbox ou veio do servidor. JSON; <c>null</c> na entidade excluída.
    /// </summary>
    public string? Snapshot { get; set; }

    public bool IsDeleted { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Guid? CreatedByDeviceId { get; set; }

    public DateTimeOffset? OriginCreatedAt { get; set; }

    public Guid? UpdatedByDeviceId { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// A versão de um comando que veio de outro dispositivo já revista aqui:
    /// texto de comando alheio não roda sem alguém olhar (ADR-060).
    /// </summary>
    public long ReviewedVersion { get; set; }

    public string? ApplyErrorCode { get; set; }

    public string? ApplyError { get; set; }
}

internal enum SyncOutboxState
{
    Pending = 0,

    /// <summary>Recusada de forma permanente: espera alguém corrigir ou descartar.</summary>
    Failed = 1,
}

/// <summary>
/// Uma alteração local a enviar, gravada no mesmo <c>SaveChanges</c> — e
/// portanto na mesma transação — da alteração de negócio.
/// </summary>
internal sealed class SyncOutboxRow
{
    /// <summary>A ordem de envio.</summary>
    public long Seq { get; set; }

    /// <summary>A chave de idempotência: a mesma em toda tentativa.</summary>
    public Guid OpId { get; set; }

    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    public MyTaskApp.Sync.Contracts.SyncOperationKind Kind { get; set; }

    public long BaseVersion { get; set; }

    /// <summary>Os campos alterados, em JSON; <c>{}</c> na exclusão.</summary>
    public string Fields { get; set; } = "{}";

    public DateTimeOffset? OriginCreatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Já saiu do computador ao menos uma vez. A partir daí a operação não
    /// muda mais — o servidor pode tê-la gravado —, e uma alteração nova vira
    /// outra operação.
    /// </summary>
    public bool Attempted { get; set; }

    public int Attempts { get; set; }

    public SyncOutboxState State { get; set; }

    public string? LastErrorCode { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// Um valor local que perdeu para o servidor (ADR-060): o mesmo campo mudou
/// em outro dispositivo antes. Fica visível para reaplicar ou descartar.
/// </summary>
internal sealed class SyncConflictRow
{
    public Guid Id { get; set; }

    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    /// <summary>O campo do contrato; vazio quando a operação inteira foi recusada.</summary>
    public required string Field { get; set; }

    /// <summary>JSON do valor que ficou de fora.</summary>
    public string? LocalValue { get; set; }

    /// <summary>JSON do valor que vale agora.</summary>
    public string? ServerValue { get; set; }

    public long ServerVersion { get; set; }

    public required string Reason { get; set; }

    /// <summary>Um texto curto para a lista: o título da tarefa, o nome da etiqueta.</summary>
    public string? EntityLabel { get; set; }

    public DateTimeOffset DetectedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public string? Resolution { get; set; }
}
