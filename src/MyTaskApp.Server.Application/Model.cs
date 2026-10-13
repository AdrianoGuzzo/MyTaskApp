using System.Text.Json;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.Application;

/// <summary>Quem chama: o usuário do token e o dispositivo da claim <c>device_id</c>.</summary>
public sealed record SyncCaller(Guid UserId, Guid DeviceId);

/// <summary>Quem escreveu um campo por último, e em que versão da entidade.</summary>
public sealed record FieldStamp(long Version, Guid DeviceId);

/// <summary>
/// Uma entidade guardada no servidor: o JSON do contrato como veio — campo
/// desconhecido incluído, para o cliente mais novo não perder o que mandou —
/// e, por campo, a versão e o dispositivo da última escrita.
/// </summary>
public sealed class StoredEntity
{
    public required Guid WorkspaceId { get; init; }

    public required string EntityType { get; init; }

    public required Guid EntityId { get; init; }

    public long Version { get; set; }

    /// <summary>A posição no log do workspace da última mudança: é o que o cursor compara.</summary>
    public long LastSeq { get; set; }

    public Dictionary<string, JsonElement> Data { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, FieldStamp> FieldVersions { get; set; } = new(StringComparer.Ordinal);

    public string? UniqueKey { get; set; }

    public bool IsDeleted { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Guid? CreatedByDeviceId { get; set; }

    public DateTimeOffset? OriginCreatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    public Guid? UpdatedByDeviceId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public SyncChange ToChange() => new(
        EntityType,
        EntityId,
        Version,
        IsDeleted,
        IsDeleted ? new Dictionary<string, JsonElement>() : Data,
        CreatedByUserId,
        CreatedByDeviceId,
        OriginCreatedAt,
        UpdatedByDeviceId,
        UpdatedAt);
}

/// <summary>Uma entidade que aponta para outra: o "pai existe" da escrita e a cascata da exclusão.</summary>
public sealed record StoredReference(
    string FromType,
    Guid FromId,
    string Field,
    string ToType,
    Guid ToId,
    SyncDeleteBehavior OnDelete);

/// <summary>Uma linha do log do workspace: diagnóstico e auditoria, nunca lida pelo pull.</summary>
public sealed record ChangeLogEntry(
    Guid WorkspaceId,
    long Seq,
    string EntityType,
    Guid EntityId,
    long Version,
    Guid? OpId,
    Guid UserId,
    Guid DeviceId,
    string Kind,
    IReadOnlyList<string> ChangedFields,
    DateTimeOffset At);

/// <summary>O resultado guardado de uma operação: o mesmo id devolve o mesmo resultado.</summary>
public sealed record ProcessedOperation(Guid OpId, byte[] RequestHash, OperationResult Result);

/// <summary>Um dispositivo de um usuário.</summary>
public sealed class DeviceRecord
{
    public required Guid UserId { get; init; }

    public required Guid DeviceId { get; init; }

    public required string Nickname { get; set; }

    public string Platform { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public int SchemaVersion { get; set; }

    public DateTimeOffset RegisteredAt { get; init; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsRevoked => RevokedAt is not null;

    public DeviceInfo ToInfo(Guid currentDeviceId) =>
        new(DeviceId, Nickname, Platform, AppVersion, RegisteredAt, LastSeenAt, RevokedAt, DeviceId == currentDeviceId);
}
