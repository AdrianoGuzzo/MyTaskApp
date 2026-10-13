using MyTaskApp.Application.Abstractions;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Application.Sync;

/// <summary>A ligação desta instalação com uma conta.</summary>
public enum SyncLinkState
{
    /// <summary>Nunca conectou, ou desconectou: tudo é só local.</summary>
    Unlinked = 0,

    Linked = 1,

    /// <summary>O login expirou ou foi revogado: precisa entrar de novo pelo navegador.</summary>
    NeedsReconnect = 2,

    /// <summary>Outro dispositivo revogou este: entrar de novo o registra como dispositivo novo.</summary>
    DeviceRevoked = 3,
}

/// <summary>O que a sincronização sabe de si mesma, sem segredo nenhum.</summary>
public sealed record SyncLocalState(
    SyncLinkState State,
    bool IsEnabled,
    Guid? UserId,
    string? UserDisplayName,
    string? UserEmail,
    Guid? WorkspaceId,
    Guid? LinkedDeviceId,
    string Cursor,
    int ClientSchemaVersion,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastPushAt,
    DateTimeOffset? LastPullAt,
    string? LastErrorCode,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    bool HasKnownEntities = false)
{
    public static SyncLocalState Unlinked { get; } =
        new(SyncLinkState.Unlinked, false, null, null, null, null, null, string.Empty, 0, null, null, null, null, null, null);
}

/// <summary>A conta e o workspace que o login escolheu.</summary>
public sealed record SyncAccountLink(Guid UserId, string DisplayName, string? Email, Guid WorkspaceId, Guid DeviceId, Uri Server);

/// <summary>O que um lote de resultados do push mudou aqui.</summary>
/// <param name="Done">Operações que saíram do outbox (aplicadas, sem efeito ou resolvidas).</param>
/// <param name="Waiting">Ficaram para depois (o pai ainda não chegou ao servidor).</param>
public sealed record PushOutcome(
    int Done,
    int Conflicts,
    int Failed,
    int Waiting,
    IReadOnlyList<Guid> TaskIds,
    DataArea Areas)
{
    public bool MadeProgress => Done > 0 || Failed > 0;
}

public sealed record SyncCounts(int Pending, int Failed, int OpenConflicts, int ApplyFailures, int CommandsToReview);

/// <summary>Um valor local que perdeu para o servidor, para a tela mostrar.</summary>
public sealed record SyncConflictView(
    Guid Id,
    string EntityType,
    Guid EntityId,
    string? EntityLabel,
    string Field,
    string? LocalValue,
    string? ServerValue,
    string Reason,
    DateTimeOffset DetectedAt);

/// <summary>Uma alteração local que o servidor recusou de vez: espera alguém decidir.</summary>
public sealed record SyncFailedOperationView(
    Guid OpId,
    string EntityType,
    Guid EntityId,
    string? EntityLabel,
    string? ErrorCode,
    string? Error,
    DateTimeOffset CreatedAt);

/// <summary>Uma mudança do servidor que não entrou neste computador.</summary>
public sealed record SyncApplyFailureView(string EntityType, Guid EntityId, string? ErrorCode, string? Error);

/// <summary>Um comando que veio de outro dispositivo e ainda não foi revisto aqui.</summary>
public sealed record SyncCommandReviewItem(string EntityType, Guid EntityId, string Label, string CommandText, Guid? FromDeviceId, long Version);

/// <summary>De onde veio uma tarefa: quem criou e quem mexeu por último.</summary>
public sealed record SyncOrigin(Guid? CreatedByDeviceId, DateTimeOffset? OriginCreatedAt, Guid? UpdatedByDeviceId, DateTimeOffset? UpdatedAt);

/// <summary>
/// O estado local da sincronização (ADR-060): a ligação com a conta, o outbox,
/// os conflitos e as falhas. Tudo no banco do app, nada de segredo.
/// </summary>
public interface ISyncLocalStore
{
    Task<SyncLocalState> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Liga a instalação à conta. Outra conta ou outro workspace que não o de
    /// antes: o que a sincronização sabia é esquecido — os dados locais ficam.
    /// </summary>
    Task LinkAsync(SyncAccountLink link, CancellationToken cancellationToken);

    /// <summary>Desliga a captura. Os dados, o outbox e os estados ficam para reconectar à mesma conta.</summary>
    Task UnlinkAsync(CancellationToken cancellationToken);

    Task SetStateAsync(SyncLinkState state, CancellationToken cancellationToken);

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken);

    Task MarkSchemaCurrentAsync(int schemaVersion, CancellationToken cancellationToken);

    Task ResetCursorAsync(CancellationToken cancellationToken);

    Task RecordSuccessAsync(bool pushed, bool pulled, CancellationToken cancellationToken);

    Task RecordErrorAsync(string code, string message, CancellationToken cancellationToken);

    /// <summary>
    /// As próximas operações, na ordem, já marcadas como tentadas — gravado
    /// antes de enviar, para nenhuma ser alterada depois de sair do computador.
    /// </summary>
    Task<IReadOnlyList<SyncOperation>> TakeOperationsAsync(Guid deviceId, int max, CancellationToken cancellationToken);

    Task<PushOutcome> ApplyPushResultsAsync(IReadOnlyList<OperationResult> results, CancellationToken cancellationToken);

    Task<SyncCounts> CountAsync(Guid deviceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncConflictView>> ListConflictsAsync(CancellationToken cancellationToken);

    /// <summary>Grava de novo o valor local e o manda como alteração nova, sobre a versão atual.</summary>
    Task ReapplyConflictAsync(Guid conflictId, CancellationToken cancellationToken);

    Task DismissConflictAsync(Guid conflictId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncFailedOperationView>> ListFailedAsync(CancellationToken cancellationToken);

    /// <summary>Manda de novo, com outro id: o servidor reavalia em vez de devolver a recusa guardada.</summary>
    Task RetryFailedAsync(Guid opId, CancellationToken cancellationToken);

    /// <summary>Tira a operação; devolve a entidade para buscar o estado do servidor por cima.</summary>
    Task<EntityRef?> DiscardFailedAsync(Guid opId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncApplyFailureView>> ListApplyFailuresAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncCommandReviewItem>> ListCommandReviewsAsync(Guid deviceId, CancellationToken cancellationToken);

    Task MarkCommandReviewedAsync(string entityType, Guid entityId, CancellationToken cancellationToken);

    Task<SyncOrigin?> OriginOfAsync(string entityType, Guid entityId, CancellationToken cancellationToken);
}
