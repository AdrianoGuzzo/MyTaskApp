using System.Text.Json;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.Application;

/// <summary>
/// A página do pull montada em memória (ADR-071), sobre o que o
/// <see cref="SyncQueries"/> já carregou em lote: cada item do log entra com o
/// que ele exige do cliente, e a página corta antes de passar do teto.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pai de carona</b> só se o cliente pode não tê-lo: vivo, depois do
/// cursor, fora da página e sem filho vivo já entregue apontando para ele.
/// Quando esse filho foi entregue, o pai foi junto.
/// </para>
/// <para>
/// <b>Companheiro</b> (<see cref="SyncEntityDefinition.TravelsWithParent"/>):
/// a tarefa que o cliente pode não ter chega com <i>uma</i> ocorrência, a de
/// menor posição depois do cursor. Se houver alguma na janela da página, é
/// ela; as outras vêm pelo próprio log.
/// </para>
/// </remarks>
internal sealed class PullPageBuilder(
    long after,
    PullLimits limits,
    IReadOnlyDictionary<(string Type, Guid Id), StoredEntity> loaded,
    IReadOnlyDictionary<(string Companion, string ParentType, Guid ParentId), StoredEntity> firstCompanions,
    IReadOnlySet<EntityRef> delivered)
{
    private readonly Dictionary<(string Type, Guid Id), Entry> _entries = [];

    /// <summary>Os pais que já têm um companheiro de cada tipo na página.</summary>
    private readonly HashSet<(string Companion, string ParentType, Guid ParentId)> _accompanied = [];

    private long _bytes;

    /// <summary>
    /// Põe a entidade do log, com o que ela exige. Não põe nada, e devolve
    /// <c>false</c>, se a página já tem algo e passaria do teto.
    /// </summary>
    public bool TryAdd(StoredEntity entity)
    {
        var closure = new List<Entry>();
        var keys = new HashSet<(string, Guid)>();
        var accompanied = new HashSet<(string, string, Guid)>();
        Collect(entity, closure, keys, accompanied);

        if (_entries.Count > 0
            && (_entries.Count + closure.Count > limits.MaxEntities || _bytes + closure.Sum(entry => entry.Bytes) > limits.MaxBytes))
        {
            return false;
        }

        foreach (var entry in closure)
        {
            _entries.Add((entry.Entity.EntityType, entry.Entity.EntityId), entry);
            _bytes += entry.Bytes;
        }

        _accompanied.UnionWith(accompanied);
        return true;
    }

    /// <summary>Pai antes do filho (rank), e na ordem do log dentro do rank.</summary>
    public IReadOnlyList<SyncChange> Changes() =>
    [
        .. _entries.Values
            .OrderBy(entry => SyncRegistry.Find(entry.Entity.EntityType)?.Rank ?? int.MaxValue)
            .ThenBy(entry => entry.Entity.LastSeq)
            .Select(entry => entry.Change),
    ];

    /// <summary>Os tipos que andam com uma entidade deste tipo.</summary>
    public static IReadOnlyList<SyncEntityDefinition> CompanionsOf(string type) =>
        [.. SyncRegistry.All.Where(candidate => candidate.TravelsWithParent && candidate.References.Any(reference => reference.TargetType == type))];

    /// <summary>Os pais para os quais a entidade aponta.</summary>
    public static IEnumerable<EntityRef> ParentsOf(StoredEntity entity)
    {
        if (entity.IsDeleted || SyncRegistry.Find(entity.EntityType) is not { } definition)
        {
            yield break;
        }

        foreach (var reference in definition.References)
        {
            if (SyncValue.ReadGuid(entity.Data.GetValueOrDefault(reference.Field, SyncValue.Null)) is { } parentId)
            {
                yield return new EntityRef(reference.TargetType, parentId);
            }
        }
    }

    private void Collect(
        StoredEntity entity,
        List<Entry> closure,
        HashSet<(string, Guid)> keys,
        HashSet<(string, string, Guid)> accompanied)
    {
        var key = (entity.EntityType, entity.EntityId);

        if (_entries.ContainsKey(key) || !keys.Add(key))
        {
            return;
        }

        var change = entity.ToChange();
        closure.Add(new Entry(entity, change, JsonSerializer.SerializeToUtf8Bytes(change, SyncJson.Options).Length));

        if (entity.IsDeleted)
        {
            return;
        }

        if (SyncRegistry.Find(entity.EntityType) is { TravelsWithParent: true })
        {
            foreach (var parent in ParentsOf(entity))
            {
                accompanied.Add((entity.EntityType, parent.Type, parent.Id));
            }
        }

        foreach (var parent in ParentsOf(entity))
        {
            if (loaded.TryGetValue((parent.Type, parent.Id), out var stored) && MayBeMissing(stored))
            {
                Collect(stored, closure, keys, accompanied);
            }
        }

        if (delivered.Contains(new EntityRef(entity.EntityType, entity.EntityId)))
        {
            return;
        }

        foreach (var companion in CompanionsOf(entity.EntityType))
        {
            var slot = (companion.Type, entity.EntityType, entity.EntityId);

            if (!_accompanied.Contains(slot) && !accompanied.Contains(slot) && firstCompanions.TryGetValue(slot, out var first))
            {
                Collect(first, closure, keys, accompanied);
            }
        }
    }

    /// <summary>O pai que o cliente pode não ter: o que já está na página não conta de novo.</summary>
    private bool MayBeMissing(StoredEntity parent) =>
        !parent.IsDeleted && parent.LastSeq > after && !delivered.Contains(new EntityRef(parent.EntityType, parent.EntityId));

    private sealed record Entry(StoredEntity Entity, SyncChange Change, int Bytes);
}
