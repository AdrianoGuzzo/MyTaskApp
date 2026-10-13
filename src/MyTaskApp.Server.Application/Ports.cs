using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Application;

/// <summary>
/// A persistência da sincronização. Toda escrita acontece dentro de
/// <see cref="ISyncUnitOfWork.ExecuteAsync{T}"/>, com o workspace travado por
/// <see cref="LockWorkspaceAsync"/>.
/// </summary>
public interface ISyncStore
{
    /// <summary>
    /// Trava o workspace até o fim da transação e devolve a última posição do
    /// log. É o que serializa os pushes e deixa a sequência densa.
    /// </summary>
    Task<long> LockWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken);

    Task SetLastSeqAsync(Guid workspaceId, long seq, CancellationToken cancellationToken);

    /// <summary>A última posição, sem travar (leitura do pull).</summary>
    Task<long> GetLastSeqAsync(Guid workspaceId, CancellationToken cancellationToken);

    Task<ProcessedOperation?> FindProcessedAsync(Guid workspaceId, Guid opId, CancellationToken cancellationToken);

    Task SaveProcessedAsync(
        Guid workspaceId,
        SyncCaller caller,
        ProcessedOperation operation,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    Task<StoredEntity?> FindAsync(Guid workspaceId, string entityType, Guid entityId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredEntity>> FindManyAsync(
        Guid workspaceId,
        IReadOnlyCollection<EntityRef> entities,
        CancellationToken cancellationToken);

    /// <summary>Outra entidade viva com a mesma chave única.</summary>
    Task<Guid?> FindByUniqueKeyAsync(
        Guid workspaceId,
        string entityType,
        string uniqueKey,
        Guid excluding,
        CancellationToken cancellationToken);

    /// <summary>Grava a entidade (nova ou alterada).</summary>
    Task SaveAsync(StoredEntity entity, CancellationToken cancellationToken);

    /// <summary>Troca as referências que saem da entidade.</summary>
    Task ReplaceReferencesAsync(
        Guid workspaceId,
        string fromType,
        Guid fromId,
        IReadOnlyList<StoredReference> references,
        CancellationToken cancellationToken);

    /// <summary>Quem aponta para a entidade.</summary>
    Task<IReadOnlyList<StoredReference>> FindReferencesToAsync(
        Guid workspaceId,
        string toType,
        Guid toId,
        CancellationToken cancellationToken);

    Task AppendLogAsync(ChangeLogEntry entry, CancellationToken cancellationToken);

    /// <summary>As entidades com <c>LastSeq</c> depois do cursor, em ordem de <c>LastSeq</c>.</summary>
    Task<IReadOnlyList<StoredEntity>> ReadChangesAsync(
        Guid workspaceId,
        long afterSeq,
        int limit,
        bool includeDeleted,
        CancellationToken cancellationToken);

    Task<long> CountAsync(Guid workspaceId, CancellationToken cancellationToken);
}

/// <summary>Uma transação: ou tudo do push entra, ou nada entra.</summary>
public interface ISyncUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

/// <summary>Quem é membro de qual workspace, e com que papel.</summary>
public interface IWorkspaceAccess
{
    Task<WorkspaceRole?> RoleOfAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkspaceInfo>> ListAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IDeviceRegistry
{
    Task<DeviceRecord?> FindAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceRecord>> ListAsync(Guid userId, CancellationToken cancellationToken);

    Task SaveAsync(DeviceRecord device, CancellationToken cancellationToken);
}

/// <summary>Revoga as autorizações e os tokens de um dispositivo no servidor OAuth.</summary>
public interface IAuthorizationRevoker
{
    Task RevokeDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);
}
