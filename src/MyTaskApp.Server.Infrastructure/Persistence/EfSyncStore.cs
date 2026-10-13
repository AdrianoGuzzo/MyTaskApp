using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Server.Application;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.Infrastructure.Persistence;

/// <summary>
/// As portas da sincronização sobre o PostgreSQL. Cada escrita grava na hora
/// (dentro da transação do push), para a leitura seguinte do mesmo push — a
/// chave única, o pai que acabou de entrar — ver o que veio antes.
/// </summary>
internal sealed class EfSyncStore(ServerDbContext context) : ISyncStore, ISyncUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            return await work(cancellationToken);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var result = await work(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return result;
    }

    /// <summary>
    /// <c>SELECT … FOR UPDATE</c> na linha do workspace: o segundo push espera o
    /// primeiro terminar, e as posições do log saem em ordem e sem buraco — um
    /// leitor nunca passa por cima de uma transação que confirmou atrasada.
    /// </summary>
    public async Task<long> LockWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var values = await context.Database
            .SqlQuery<long>($"SELECT \"LastSeq\" AS \"Value\" FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        return values.Count == 1 ? values[0] : throw SyncServerException.NotFound();
    }

    public async Task SetLastSeqAsync(Guid workspaceId, long seq, CancellationToken cancellationToken) =>
        await context.Workspaces
            .Where(workspace => workspace.Id == workspaceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(workspace => workspace.LastSeq, seq), cancellationToken);

    public async Task<long> GetLastSeqAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await context.Workspaces.AsNoTracking()
            .Where(workspace => workspace.Id == workspaceId)
            .Select(workspace => workspace.LastSeq)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ProcessedOperation?> FindProcessedAsync(Guid workspaceId, Guid opId, CancellationToken cancellationToken)
    {
        var row = await context.ProcessedOperations.AsNoTracking()
            .SingleOrDefaultAsync(processed => processed.WorkspaceId == workspaceId && processed.OpId == opId, cancellationToken);

        return row is null
            ? null
            : new ProcessedOperation(row.OpId, row.RequestHash, JsonSerializer.Deserialize<OperationResult>(row.Result, SyncJson.Options)!);
    }

    public async Task SaveProcessedAsync(
        Guid workspaceId,
        SyncCaller caller,
        ProcessedOperation operation,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        context.ProcessedOperations.Add(new ProcessedOperationRow
        {
            WorkspaceId = workspaceId,
            OpId = operation.OpId,
            UserId = caller.UserId,
            DeviceId = caller.DeviceId,
            RequestHash = operation.RequestHash,
            Result = JsonSerializer.Serialize(operation.Result, SyncJson.Options),
            ProcessedAt = at,
        });

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<StoredEntity?> FindAsync(Guid workspaceId, string entityType, Guid entityId, CancellationToken cancellationToken) =>
        await context.SyncEntities.FindAsync([workspaceId, entityType, entityId], cancellationToken) is { } row ? ToModel(row) : null;

    public async Task<IReadOnlyList<StoredEntity>> FindManyAsync(
        Guid workspaceId,
        IReadOnlyCollection<EntityRef> entities,
        CancellationToken cancellationToken)
    {
        var ids = entities.Select(entity => entity.Id).Distinct().ToList();
        var wanted = entities.Select(entity => (entity.Type, entity.Id)).ToHashSet();

        var rows = await context.SyncEntities.AsNoTracking()
            .Where(row => row.WorkspaceId == workspaceId && ids.Contains(row.EntityId))
            .ToListAsync(cancellationToken);

        return [.. rows.Where(row => wanted.Contains((row.EntityType, row.EntityId))).Select(ToModel)];
    }

    public async Task<Guid?> FindByUniqueKeyAsync(
        Guid workspaceId,
        string entityType,
        string uniqueKey,
        Guid excluding,
        CancellationToken cancellationToken) =>
        await context.SyncEntities.AsNoTracking()
            .Where(row => row.WorkspaceId == workspaceId && row.EntityType == entityType && row.UniqueKey == uniqueKey
                && !row.IsDeleted && row.EntityId != excluding)
            .Select(row => (Guid?)row.EntityId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task SaveAsync(StoredEntity entity, CancellationToken cancellationToken)
    {
        var row = await context.SyncEntities.FindAsync([entity.WorkspaceId, entity.EntityType, entity.EntityId], cancellationToken);

        if (row is null)
        {
            row = new SyncEntityRow { WorkspaceId = entity.WorkspaceId, EntityType = entity.EntityType, EntityId = entity.EntityId };
            context.SyncEntities.Add(row);
        }

        row.Version = entity.Version;
        row.LastSeq = entity.LastSeq;
        row.Data = JsonSerializer.Serialize(entity.Data, SyncJson.Options);
        row.FieldVersions = JsonSerializer.Serialize(entity.FieldVersions, SyncJson.Options);
        row.UniqueKey = entity.UniqueKey;
        row.IsDeleted = entity.IsDeleted;
        row.CreatedByUserId = entity.CreatedByUserId;
        row.CreatedByDeviceId = entity.CreatedByDeviceId;
        row.OriginCreatedAt = entity.OriginCreatedAt;
        row.UpdatedByUserId = entity.UpdatedByUserId;
        row.UpdatedByDeviceId = entity.UpdatedByDeviceId;
        row.UpdatedAt = entity.UpdatedAt;
        row.DeletedAt = entity.DeletedAt;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ReplaceReferencesAsync(
        Guid workspaceId,
        string fromType,
        Guid fromId,
        IReadOnlyList<StoredReference> references,
        CancellationToken cancellationToken)
    {
        await context.SyncReferences
            .Where(row => row.WorkspaceId == workspaceId && row.FromType == fromType && row.FromId == fromId)
            .ExecuteDeleteAsync(cancellationToken);

        context.SyncReferences.AddRange(references.Select(reference => new SyncReferenceRow
        {
            WorkspaceId = workspaceId,
            FromType = reference.FromType,
            FromId = reference.FromId,
            Field = reference.Field,
            ToType = reference.ToType,
            ToId = reference.ToId,
            OnDelete = (int)reference.OnDelete,
        }));

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StoredReference>> FindReferencesToAsync(
        Guid workspaceId,
        string toType,
        Guid toId,
        CancellationToken cancellationToken) =>
        [
            .. (await context.SyncReferences.AsNoTracking()
                    .Where(row => row.WorkspaceId == workspaceId && row.ToType == toType && row.ToId == toId)
                    .ToListAsync(cancellationToken))
                .Select(row => new StoredReference(row.FromType, row.FromId, row.Field, row.ToType, row.ToId, (SyncDeleteBehavior)row.OnDelete)),
        ];

    public async Task AppendLogAsync(ChangeLogEntry entry, CancellationToken cancellationToken)
    {
        context.ChangeLog.Add(new ChangeLogRow
        {
            WorkspaceId = entry.WorkspaceId,
            Seq = entry.Seq,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            Version = entry.Version,
            OpId = entry.OpId,
            UserId = entry.UserId,
            DeviceId = entry.DeviceId,
            Kind = entry.Kind,
            ChangedFields = [.. entry.ChangedFields],
            At = entry.At,
        });

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StoredEntity>> ReadChangesAsync(
        Guid workspaceId,
        long afterSeq,
        int limit,
        bool includeDeleted,
        CancellationToken cancellationToken) =>
        [
            .. (await context.SyncEntities.AsNoTracking()
                    .Where(row => row.WorkspaceId == workspaceId && row.LastSeq > afterSeq && (includeDeleted || !row.IsDeleted))
                    .OrderBy(row => row.LastSeq)
                    .Take(limit)
                    .ToListAsync(cancellationToken))
                .Select(ToModel),
        ];

    public async Task<long> CountAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await context.SyncEntities.LongCountAsync(row => row.WorkspaceId == workspaceId && !row.IsDeleted, cancellationToken);

    private static StoredEntity ToModel(SyncEntityRow row) => new()
    {
        WorkspaceId = row.WorkspaceId,
        EntityType = row.EntityType,
        EntityId = row.EntityId,
        Version = row.Version,
        LastSeq = row.LastSeq,
        Data = new Dictionary<string, JsonElement>(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Data, SyncJson.Options) ?? [],
            StringComparer.Ordinal),
        FieldVersions = new Dictionary<string, FieldStamp>(
            JsonSerializer.Deserialize<Dictionary<string, FieldStamp>>(row.FieldVersions, SyncJson.Options) ?? [],
            StringComparer.Ordinal),
        UniqueKey = row.UniqueKey,
        IsDeleted = row.IsDeleted,
        CreatedByUserId = row.CreatedByUserId,
        CreatedByDeviceId = row.CreatedByDeviceId,
        OriginCreatedAt = row.OriginCreatedAt,
        UpdatedByUserId = row.UpdatedByUserId,
        UpdatedByDeviceId = row.UpdatedByDeviceId,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
    };
}
