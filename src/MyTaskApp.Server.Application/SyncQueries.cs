using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.Application;

/// <summary>
/// O pull (ADR-060): o estado atual do que mudou depois do cursor, em ordem
/// do log, uma vez cada. Não é um replay de operações — é o log compactado:
/// a entidade alterada dez vezes vem uma vez, na última versão.
/// </summary>
/// <remarks>
/// <para>
/// <b>Página fechada.</b> O filho não chega sem o pai: se o pai também mudou
/// depois do cursor e ficou fora do limite, ele entra junto. E a ocorrência
/// anda com a tarefa nos dois sentidos — o banco local não tem tarefa sem
/// ocorrência. O cursor seguinte é o do último item <i>da página</i>; o que
/// veio de carona volta depois, e o cliente o ignora pela versão.
/// </para>
/// <para>
/// <b>Do zero não há tombstone.</b> Quem nunca sincronizou não tem o que
/// apagar.
/// </para>
/// </remarks>
public sealed class SyncQueries(ISyncStore store, IWorkspaceAccess access, IDeviceRegistry devices)
{
    public async Task<PullResponse> PullAsync(
        SyncCaller caller,
        Guid workspaceId,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        await EnsureMemberAsync(caller, workspaceId, cancellationToken);

        if (!SyncCursor.TryDecode(cursor, out var after))
        {
            throw SyncServerException.BadRequest("Cursor inválido.");
        }

        var take = Math.Clamp(limit ?? SyncSchema.DefaultPullLimit, 1, SyncSchema.MaxPullLimit);
        var last = await store.GetLastSeqAsync(workspaceId, cancellationToken);

        if (after > last)
        {
            throw SyncServerException.CursorAhead();
        }

        var rows = await store.ReadChangesAsync(workspaceId, after, take + 1, includeDeleted: after > 0, cancellationToken);
        var hasMore = rows.Count > take;
        var primary = rows.Take(take).ToList();
        var next = hasMore ? primary[^1].LastSeq : last;

        var page = primary.ToDictionary(entity => (entity.EntityType, entity.EntityId));
        var queue = new Queue<StoredEntity>(primary);

        while (queue.TryDequeue(out var entity))
        {
            foreach (var related in await RelatedAsync(workspaceId, entity, cancellationToken))
            {
                if (!related.IsDeleted && related.LastSeq > after && page.TryAdd((related.EntityType, related.EntityId), related))
                {
                    queue.Enqueue(related);
                }
            }
        }

        var changes = page.Values
            .OrderBy(entity => SyncRegistry.Find(entity.EntityType)?.Rank ?? int.MaxValue)
            .ThenBy(entity => entity.LastSeq)
            .Select(entity => entity.ToChange())
            .ToList();

        return new PullResponse(changes, SyncCursor.Encode(next), hasMore);
    }

    public async Task<FetchResponse> FetchAsync(
        SyncCaller caller,
        Guid workspaceId,
        FetchRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureMemberAsync(caller, workspaceId, cancellationToken);

        if (request.Entities.Count > SyncSchema.MaxFetchEntities)
        {
            throw SyncServerException.BadRequest($"No máximo {SyncSchema.MaxFetchEntities} entidades por pedido.");
        }

        var found = await store.FindManyAsync(workspaceId, request.Entities, cancellationToken);

        return new FetchResponse([.. found.Select(entity => entity.ToChange())]);
    }

    public async Task<SyncStatusResponse> StatusAsync(SyncCaller caller, Guid workspaceId, CancellationToken cancellationToken)
    {
        await EnsureMemberAsync(caller, workspaceId, cancellationToken);

        var last = await store.GetLastSeqAsync(workspaceId, cancellationToken);
        var count = await store.CountAsync(workspaceId, cancellationToken);
        var list = await devices.ListAsync(caller.UserId, cancellationToken);

        return new SyncStatusResponse(SyncCursor.Encode(last), count, [.. list.Select(device => device.ToInfo(caller.DeviceId))]);
    }

    public Task<IReadOnlyList<WorkspaceInfo>> WorkspacesAsync(SyncCaller caller, CancellationToken cancellationToken) =>
        access.ListAsync(caller.UserId, cancellationToken);

    private async Task EnsureMemberAsync(SyncCaller caller, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (await access.RoleOfAsync(caller.UserId, workspaceId, cancellationToken) is null)
        {
            throw SyncServerException.NotFound();
        }
    }

    /// <summary>Os pais da entidade e, se ela for pai de quem anda junto, esses filhos.</summary>
    private async Task<IReadOnlyList<StoredEntity>> RelatedAsync(Guid workspaceId, StoredEntity entity, CancellationToken cancellationToken)
    {
        var related = new List<StoredEntity>();

        if (entity.IsDeleted || SyncRegistry.Find(entity.EntityType) is not { } definition)
        {
            return related;
        }

        foreach (var reference in definition.References)
        {
            if (SyncValue.ReadGuid(entity.Data.GetValueOrDefault(reference.Field, SyncValue.Null)) is { } parentId
                && await store.FindAsync(workspaceId, reference.TargetType, parentId, cancellationToken) is { } parent)
            {
                related.Add(parent);
            }
        }

        var companions = SyncRegistry.All
            .Where(candidate => candidate.TravelsWithParent && candidate.References.Any(reference => reference.TargetType == entity.EntityType))
            .Select(candidate => candidate.Type)
            .ToHashSet(StringComparer.Ordinal);

        if (companions.Count > 0)
        {
            foreach (var reference in await store.FindReferencesToAsync(workspaceId, entity.EntityType, entity.EntityId, cancellationToken))
            {
                if (companions.Contains(reference.FromType)
                    && await store.FindAsync(workspaceId, reference.FromType, reference.FromId, cancellationToken) is { } child)
                {
                    related.Add(child);
                }
            }
        }

        return related;
    }
}
