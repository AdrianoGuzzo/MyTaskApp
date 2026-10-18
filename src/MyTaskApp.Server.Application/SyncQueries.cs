using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.Application;

/// <summary>
/// O pull (ADR-061): o estado atual do que mudou depois do cursor, em ordem
/// do log, uma vez cada. Não é um replay de operações — é o log compactado:
/// a entidade alterada dez vezes vem uma vez, na última versão.
/// </summary>
/// <remarks>
/// <para>
/// <b>Página fechada.</b> O filho não chega sem o pai: se o cliente pode não
/// ter o pai, ele vem de carona. E a tarefa que o cliente pode não ter chega
/// com uma ocorrência — o banco local não tem tarefa sem ocorrência. As outras
/// ocorrências vêm pelo próprio log, e não a família inteira a cada página
/// (ADR-071). O cursor seguinte é o do último item do log <i>na página</i>; o
/// que veio de carona volta depois, e o cliente o ignora pela versão.
/// </para>
/// <para>
/// <b>Teto.</b> O <c>limit</c> conta o log; <see cref="PullLimits"/> conta a
/// resposta inteira. A página corta antes do item que passaria do teto, e o
/// cliente segue pelo <c>hasMore</c>. Os relacionados vêm em lote, sem uma
/// consulta por referência.
/// </para>
/// <para>
/// <b>Do zero não há tombstone.</b> Quem nunca sincronizou não tem o que
/// apagar.
/// </para>
/// </remarks>
public sealed class SyncQueries(ISyncStore store, IWorkspaceAccess access, IDeviceRegistry devices, PullLimits limits)
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
        var primary = rows.Take(take).ToList();
        var page = await BuildPageAsync(workspaceId, primary, after, cancellationToken);

        // O primeiro sempre cabe: o cursor anda, mesmo com um item maior que o teto.
        var taken = 0;

        while (taken < primary.Count && page.TryAdd(primary[taken]))
        {
            taken++;
        }

        var hasMore = rows.Count > take || taken < primary.Count;
        var next = hasMore ? primary[taken - 1].LastSeq : last;

        return new PullResponse(page.Changes(), SyncCursor.Encode(next), hasMore);
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

    /// <summary>
    /// Carrega, em lote e por nível, tudo o que a página pode exigir: os pais
    /// (e os pais deles), a primeira ocorrência de cada tarefa e o que o
    /// cliente já tem. Algumas consultas por página, qualquer que seja o tamanho
    /// da família.
    /// </summary>
    private async Task<PullPageBuilder> BuildPageAsync(
        Guid workspaceId,
        IReadOnlyList<StoredEntity> primary,
        long after,
        CancellationToken cancellationToken)
    {
        var loaded = primary.ToDictionary(entity => (entity.EntityType, entity.EntityId));
        var firstCompanions = new Dictionary<(string, string, Guid), StoredEntity>();
        var asked = new HashSet<(string, string, Guid)>();
        var frontier = primary.Where(entity => !entity.IsDeleted).ToList();

        while (frontier.Count > 0)
        {
            var found = new List<StoredEntity>();
            var parents = frontier
                .SelectMany(PullPageBuilder.ParentsOf)
                .Where(parent => !loaded.ContainsKey((parent.Type, parent.Id)))
                .Distinct()
                .ToList();

            if (parents.Count > 0)
            {
                found.AddRange(await store.FindManyAsync(workspaceId, parents, cancellationToken));
            }

            var slots = frontier
                .SelectMany(entity => PullPageBuilder.CompanionsOf(entity.EntityType)
                    .Select(companion => (Companion: companion.Type, entity.EntityType, entity.EntityId)))
                .Where(asked.Add)
                .GroupBy(slot => (slot.Companion, slot.EntityType));

            foreach (var group in slots)
            {
                var (companion, parentType) = group.Key;
                var reference = SyncRegistry.Find(companion)!.References.First(reference => reference.TargetType == parentType);
                var firsts = await store.FindFirstReferrersAsync(
                    workspaceId,
                    companion,
                    parentType,
                    [.. group.Select(slot => slot.EntityId)],
                    after,
                    cancellationToken);

                foreach (var first in firsts)
                {
                    if (SyncValue.ReadGuid(first.Data.GetValueOrDefault(reference.Field, SyncValue.Null)) is { } parentId)
                    {
                        firstCompanions[(companion, parentType, parentId)] = first;
                        found.Add(first);
                    }
                }
            }

            // Só o que pode vir de carona precisa dos pais dele.
            frontier = [.. found.Where(entity => loaded.TryAdd((entity.EntityType, entity.EntityId), entity) && !entity.IsDeleted && entity.LastSeq > after)];
        }

        IReadOnlySet<EntityRef> delivered = after > 0
            ? await store.FindDeliveredAsync(
                workspaceId,
                [.. loaded.Values.Where(entity => !entity.IsDeleted).Select(entity => new EntityRef(entity.EntityType, entity.EntityId))],
                after,
                cancellationToken)
            : new HashSet<EntityRef>();

        return new PullPageBuilder(after, limits, loaded, firstCompanions, delivered);
    }
}
