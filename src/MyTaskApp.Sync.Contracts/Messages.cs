using System.Text.Json;

namespace MyTaskApp.Sync.Contracts;

// ---------------------------------------------------------------------------
// Push: o desktop envia as operações do outbox.
// ---------------------------------------------------------------------------

public enum SyncOperationKind
{
    Upsert,
    Delete,
}

/// <summary>
/// Uma alteração local. O <see cref="OpId"/> é a chave de idempotência: a
/// mesma operação reenviada depois de um timeout devolve o resultado guardado.
/// </summary>
/// <param name="BaseVersion">A versão do servidor que o dispositivo conhecia (0 = criação).</param>
/// <param name="Fields">Só os campos alterados (todos, na criação), pelo nome do contrato.</param>
/// <param name="OriginCreatedAt">Quando a entidade nasceu no dispositivo de origem.</param>
public sealed record SyncOperation(
    Guid OpId,
    Guid DeviceId,
    string EntityType,
    Guid EntityId,
    SyncOperationKind Kind,
    long BaseVersion,
    IReadOnlyDictionary<string, JsonElement> Fields,
    DateTimeOffset? OriginCreatedAt = null);

public sealed record PushRequest(int SchemaVersion, Guid DeviceId, IReadOnlyList<SyncOperation> Operations);

public enum OperationOutcome
{
    /// <summary>Tudo aplicado.</summary>
    Applied,

    /// <summary>Parte dos campos aplicada; os outros estão em <see cref="OperationResult.RejectedFields"/>.</summary>
    PartiallyApplied,

    /// <summary>Nada aplicado; o motivo está em <see cref="OperationResult.Error"/> ou nos campos.</summary>
    Rejected,

    /// <summary>Nada a mudar (o servidor já tinha esses valores, ou a exclusão já valia).</summary>
    Noop,
}

/// <summary>Um campo que não entrou, e o valor que vale no servidor.</summary>
public sealed record RejectedField(string Field, string Reason, JsonElement ServerValue, Guid? ConflictingEntityId = null);

public sealed record SyncError(string Code, string Message);

/// <param name="Version">A versão da entidade no servidor depois da operação.</param>
/// <param name="PreviousVersion">A versão antes dela; o cliente só adota a nova se conhecia a anterior.</param>
/// <param name="Duplicate">A operação já tinha sido processada: este é o resultado guardado.</param>
public sealed record OperationResult(
    Guid OpId,
    OperationOutcome Outcome,
    bool Duplicate,
    long Version,
    long PreviousVersion,
    IReadOnlyList<RejectedField> RejectedFields,
    SyncError? Error = null);

public sealed record PushResponse(IReadOnlyList<OperationResult> Results, DateTimeOffset ServerTime);

// ---------------------------------------------------------------------------
// Pull e fetch: o desktop recebe o estado atual das entidades que mudaram.
// ---------------------------------------------------------------------------

/// <summary>
/// O estado inteiro de uma entidade numa versão. Excluída (<see cref="Deleted"/>)
/// vem sem campos: é o tombstone que impede a ressurreição.
/// </summary>
public sealed record SyncChange(
    string EntityType,
    Guid EntityId,
    long Version,
    bool Deleted,
    IReadOnlyDictionary<string, JsonElement> Fields,
    Guid? CreatedByUserId,
    Guid? CreatedByDeviceId,
    DateTimeOffset? OriginCreatedAt,
    Guid? UpdatedByDeviceId,
    DateTimeOffset UpdatedAt);

public sealed record PullResponse(IReadOnlyList<SyncChange> Changes, string NextCursor, bool HasMore);

public sealed record EntityRef(string Type, Guid Id);

public sealed record FetchRequest(IReadOnlyList<EntityRef> Entities);

public sealed record FetchResponse(IReadOnlyList<SyncChange> Changes);

// ---------------------------------------------------------------------------
// Conta, workspaces e dispositivos.
// ---------------------------------------------------------------------------

public sealed record UserInfo(Guid Id, string DisplayName, string? Email);

public enum WorkspaceRole
{
    Viewer,
    Editor,
    Owner,
}

public sealed record WorkspaceInfo(Guid Id, string Name, WorkspaceRole Role, bool IsPersonal);

/// <summary>
/// O "olá" de cada ciclo: registra versão e plataforma do dispositivo.
/// </summary>
/// <param name="Capabilities">
/// O que este dispositivo sabe fazer além de sincronizar. Vazio na v1; é o
/// ponto de extensão reservado para um futuro agente local, sem antecipá-lo.
/// </param>
public sealed record DeviceHello(
    string? Nickname,
    string Platform,
    string AppVersion,
    int SchemaVersion,
    IReadOnlyList<string>? Capabilities = null);

public sealed record DeviceInfo(
    Guid DeviceId,
    string Nickname,
    string Platform,
    string AppVersion,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? RevokedAt,
    bool IsCurrent);

public sealed record RenameDeviceRequest(string Nickname);

public sealed record MeResponse(UserInfo User, DeviceInfo? Device);

public sealed record SyncStatusResponse(string LatestCursor, long EntityCount, IReadOnlyList<DeviceInfo> Devices);
