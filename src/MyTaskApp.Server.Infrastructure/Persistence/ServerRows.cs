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

/// <summary>
/// Uma chave pública de dispositivo na orquestração remota (ADR-087). Trocar de
/// chave revoga a anterior, que fica para a auditoria; só uma vale por vez.
/// </summary>
internal sealed class DeviceKeyRow
{
    public Guid UserId { get; set; }

    public Guid DeviceId { get; set; }

    /// <summary>SHA-256 do SPKI, em hexadecimal.</summary>
    public required string Fingerprint { get; set; }

    /// <summary>O SPKI em base64url, como o dispositivo mandou.</summary>
    public required string SubjectPublicKeyInfo { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Papéis, capacidades e sinal de vida de um dispositivo na orquestração: o último registro.</summary>
internal sealed class DeviceCapabilitiesRow
{
    public Guid UserId { get; set; }

    public Guid DeviceId { get; set; }

    public int Roles { get; set; }

    /// <summary>O <c>DeviceCapabilities</c> do contrato, em <c>jsonb</c>.</summary>
    public string Capabilities { get; set; } = "{}";

    public DateTimeOffset LastHeartbeatAt { get; set; }

    /// <summary>
    /// O último relato do agente (ADR-091), que o desktop da mesma instalação
    /// não renova: a máquina pode estar ligada com o agente parado.
    /// </summary>
    public DateTimeOffset? AgentSeenAt { get; set; }
}

/// <summary>
/// Um pedido de pareamento (ADR-087): as duas chaves do momento do pedido e o
/// nonce do código. Fica depois de aprovado ou expirado, para a auditoria.
/// </summary>
internal sealed class RemotePairingRow
{
    public Guid PairingId { get; set; }

    public Guid UserId { get; set; }

    public Guid ControllerDeviceId { get; set; }

    public required string ControllerFingerprint { get; set; }

    public required string ControllerSubjectPublicKeyInfo { get; set; }

    public Guid ExecutorDeviceId { get; set; }

    public required string ExecutorFingerprint { get; set; }

    public required string Nonce { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }
}

/// <summary>
/// Uma operação remota (ADR-086): o JWS do controlador como chegou, e o estado
/// que o servidor e o agente vão mudando.
/// </summary>
internal sealed class RemoteOperationRow
{
    public Guid OperationId { get; set; }

    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid TargetDeviceId { get; set; }

    public Guid RequestedByDeviceId { get; set; }

    public required string Kind { get; set; }

    public Guid? TaskId { get; set; }

    public int Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset NotAfter { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }

    public DateTimeOffset? CancelRequestedAt { get; set; }

    public string? ResultCode { get; set; }

    public string? ResultSummary { get; set; }

    public long LastContiguousSeq { get; set; }

    /// <summary>Os eventos guardados: acima do teto, a saída é descartada.</summary>
    public int EventCount { get; set; }

    /// <summary>O JWS compacto, byte a byte.</summary>
    public required string Request { get; set; }

    public required string RequestHash { get; set; }

    /// <summary>A conversa do Claude a que esta operação pertence (ADR-091), quando há.</summary>
    public Guid? AgentSessionId { get; set; }
}

/// <summary>
/// Uma conversa do Claude sem terminal (ADR-091): o último relato aceito do
/// agente da máquina dela. Só ids e estado; nenhum caminho nem texto de prompt.
/// </summary>
internal sealed class RemoteAgentSessionRow
{
    public Guid UserId { get; set; }

    public Guid SessionId { get; set; }

    public Guid DeviceId { get; set; }

    public long Version { get; set; }

    public Guid TaskId { get; set; }

    public Guid? DevelopmentId { get; set; }

    public required string ProfileId { get; set; }

    public string? Model { get; set; }

    public int Status { get; set; }

    public int Activity { get; set; }

    public string? ActivityMessage { get; set; }

    public Guid? CurrentOperationId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>A hora do agente.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>A hora do servidor: a retenção conta daqui.</summary>
    public DateTimeOffset ReportedAt { get; set; }
}

/// <summary>Um evento de uma operação, por <c>(OperationId, Seq)</c>: o repetido não entra de novo.</summary>
internal sealed class RemoteOperationEventRow
{
    public Guid OperationId { get; set; }

    public long Seq { get; set; }

    /// <summary>A hora do agente.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>A hora do servidor: a retenção conta daqui.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public int Kind { get; set; }

    public string? Step { get; set; }

    public int? Status { get; set; }

    public string? Text { get; set; }

    /// <summary>O <c>Data</c> do contrato, em <c>jsonb</c>.</summary>
    public string? Data { get; set; }
}

/// <summary>
/// Uma aprovação (ADR-086): o pedido como o agente selou, em texto e não em
/// <c>jsonb</c> (que reordena as chaves e mudaria o hash), e a resposta
/// assinada pelo controlador.
/// </summary>
internal sealed class RemoteApprovalRow
{
    public Guid ApprovalId { get; set; }

    public Guid UserId { get; set; }

    public Guid OperationId { get; set; }

    public Guid TargetDeviceId { get; set; }

    public int Kind { get; set; }

    /// <summary>O <c>ApprovalRequest</c> do contrato, serializado.</summary>
    public required string Request { get; set; }

    public required string RequestHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public int State { get; set; }

    /// <summary>O JWS da resposta, byte a byte.</summary>
    public string? Response { get; set; }

    public Guid? RespondedByDeviceId { get; set; }

    public DateTimeOffset? RespondedAt { get; set; }
}

/// <summary>O catálogo publicado por uma máquina executora (ADR-088): o último vale.</summary>
internal sealed class AgentCatalogRow
{
    public Guid UserId { get; set; }

    public Guid DeviceId { get; set; }

    public required string Hash { get; set; }

    /// <summary>O <c>AgentCatalog</c> do contrato, em <c>jsonb</c>.</summary>
    public string Catalog { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; }
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
