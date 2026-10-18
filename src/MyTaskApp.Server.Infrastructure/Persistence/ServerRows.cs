using Microsoft.AspNetCore.Identity;

namespace MyTaskApp.Server.Infrastructure.Persistence;

/// <summary>A conta: só login externo (Google), sem senha local.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Um espaço de dados. Na v1, cada usuário tem um pessoal; a tabela já aceita membros.</summary>
internal sealed class WorkspaceRow
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public Guid OwnerUserId { get; set; }

    public bool IsPersonal { get; set; }

    /// <summary>A última posição do log: travada a cada push, é o que serializa as escritas.</summary>
    public long LastSeq { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class WorkspaceMemberRow
{
    public Guid WorkspaceId { get; set; }

    public Guid UserId { get; set; }

    public int Role { get; set; }

    public DateTimeOffset JoinedAt { get; set; }
}

internal sealed class DeviceRow
{
    public Guid UserId { get; set; }

    public Guid DeviceId { get; set; }

    public required string Nickname { get; set; }

    public string Platform { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public int SchemaVersion { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>A autorização OpenIddict viva deste dispositivo: revogar o dispositivo revoga ela.</summary>
    public string? CurrentAuthorizationId { get; set; }
}

/// <summary>Uma entidade sincronizada, com o JSON do contrato em <c>jsonb</c>.</summary>
internal sealed class SyncEntityRow
{
    public Guid WorkspaceId { get; set; }

    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    public long Version { get; set; }

    public long LastSeq { get; set; }

    public string Data { get; set; } = "{}";

    public string FieldVersions { get; set; } = "{}";

    /// <summary>O tamanho do <see cref="Data"/> em bytes UTF-8, para a cota do workspace sem ler o JSON (ADR-072).</summary>
    public int DataBytes { get; set; }

    public string? UniqueKey { get; set; }

    public bool IsDeleted { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Guid? CreatedByDeviceId { get; set; }

    public DateTimeOffset? OriginCreatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    public Guid? UpdatedByDeviceId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}

internal sealed class SyncReferenceRow
{
    public Guid WorkspaceId { get; set; }

    public required string FromType { get; set; }

    public Guid FromId { get; set; }

    public required string Field { get; set; }

    public required string ToType { get; set; }

    public Guid ToId { get; set; }

    public int OnDelete { get; set; }
}

internal sealed class ChangeLogRow
{
    public Guid WorkspaceId { get; set; }

    public long Seq { get; set; }

    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    public long Version { get; set; }

    public Guid? OpId { get; set; }

    public Guid UserId { get; set; }

    public Guid DeviceId { get; set; }

    public required string Kind { get; set; }

    public string[] ChangedFields { get; set; } = [];

    public DateTimeOffset At { get; set; }
}

internal sealed class ProcessedOperationRow
{
    public Guid WorkspaceId { get; set; }

    public Guid OpId { get; set; }

    public Guid UserId { get; set; }

    public Guid DeviceId { get; set; }

    public required byte[] RequestHash { get; set; }

    public required string Result { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}
