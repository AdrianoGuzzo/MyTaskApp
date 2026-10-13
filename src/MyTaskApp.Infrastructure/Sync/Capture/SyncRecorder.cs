using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Sync.Mapping;
using MyTaskApp.Infrastructure.Sync.Persistence;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Sync.Capture;

/// <summary>
/// Uma entidade sincronizada a registrar. Sem <see cref="Entry"/>, ela sumiu
/// do banco sem passar pelo change tracker (cascata) e só a exclusão vale.
/// </summary>
internal sealed record SyncTouched(SyncEntityMapper Mapper, EntityEntry? Entry, Guid Id)
{
    public bool IsDeleted => Entry is null || Entry.State == EntityState.Deleted;
}

/// <summary>
/// O diff entre o estado atual e o último conhecido, transformado em
/// operações no outbox. Usado pela captura de cada <c>SaveChanges</c> e pela
/// varredura da primeira conexão (ADR-060).
/// </summary>
/// <remarks>
/// <para>
/// <b>Diff contra o último estado conhecido, e não contra os valores originais
/// do EF.</b> Trocar um owned (<c>Reminder = policy with { }</c>) aparece como
/// uma linha excluída e outra incluída, e as listas filhas reaproveitam linhas
/// por posição. O change tracker só diz <i>quais</i> entidades foram tocadas;
/// o que mudou sai da projeção pela lista de permissão do mapper comparada ao
/// <see cref="SyncEntityStateRow.Snapshot"/>. Mexer só em campo local (a
/// posição na lista, o lembrete que tocou) não gera nada.
/// </para>
/// <para>
/// <b>Só a raiz da exclusão viaja.</b> Apagar uma tarefa leva as ocorrências e
/// os vínculos pela cascata do banco; o servidor faz a mesma cascata pelas
/// referências do contrato, então mandar a exclusão de cada filho seria só
/// ruído.
/// </para>
/// </remarks>
internal sealed class SyncRecorder(IDeviceIdentity device, TimeProvider timeProvider)
{
    /// <returns>Se alguma operação entrou no outbox.</returns>
    public bool Record(
        MyTaskAppDbContext context,
        IReadOnlyList<SyncTouched> touched,
        IEnumerable<SyncEntityStateRow> loadedStates,
        IEnumerable<SyncOutboxRow> loadedOutbox)
    {
        var now = timeProvider.GetUtcNow();
        var deviceId = device.DeviceId;
        var states = loadedStates
            .GroupBy(state => (state.EntityType, state.EntityId))
            .ToDictionary(group => group.Key, group => group.First());
        var outbox = loadedOutbox.ToList();
        var recorded = false;

        var deleted = touched
            .Where(item => item.IsDeleted)
            .Select(item => (item.Mapper.Definition.Type, item.Id))
            .ToHashSet();

        // Pai antes do filho na criação; filho antes do pai na exclusão.
        var ordered = touched
            .OrderBy(item => item.IsDeleted)
            .ThenBy(item => item.IsDeleted ? -item.Mapper.Definition.Rank : item.Mapper.Definition.Rank);

        foreach (var item in ordered)
        {
            var type = item.Mapper.Definition.Type;
            states.TryGetValue((type, item.Id), out var state);
            var entityOutbox = outbox.Where(row => row.EntityType == type && row.EntityId == item.Id).ToList();
            var unsent = entityOutbox.Where(row => row is { Attempted: false, State: SyncOutboxState.Pending }).ToList();

            if (item.IsDeleted)
            {
                if (state is null || state.IsDeleted)
                {
                    continue;
                }

                context.SyncOutbox.RemoveRange(unsent);

                // Nunca chegou ao servidor: some daqui sem dizer nada a ninguém.
                if (state.ServerVersion == 0 && !entityOutbox.Any(row => row.Attempted))
                {
                    context.SyncEntityStates.Remove(state);
                    continue;
                }

                if (!ParentIsGoingToo(item, deleted))
                {
                    Enqueue(context, type, item.Id, SyncOperationKind.Delete, state.ServerVersion, [], null, now);
                    recorded = true;
                }

                state.IsDeleted = true;
                state.Snapshot = null;
                state.UpdatedByDeviceId = deviceId;
                state.UpdatedAt = now;
                continue;
            }

            if (!item.Mapper.IsSyncable(item.Entry!))
            {
                continue;
            }

            var projection = item.Mapper.Project(item.Entry!);
            Dictionary<string, JsonElement> fields;

            if (state is null || state.IsDeleted)
            {
                fields = projection;

                if (state is null)
                {
                    state = new SyncEntityStateRow
                    {
                        EntityType = type,
                        EntityId = item.Id,
                        CreatedByDeviceId = deviceId,
                        OriginCreatedAt = item.Mapper.OriginCreatedAt(item.Entry!) ?? now,
                    };
                    context.SyncEntityStates.Add(state);
                    states[(type, item.Id)] = state;
                }

                state.IsDeleted = false;
            }
            else
            {
                var snapshot = SyncSnapshot.Read(state.Snapshot);
                fields = projection
                    .Where(field => !snapshot.TryGetValue(field.Key, out var known) || !SyncValue.AreEqual(known, field.Value))
                    .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);

                if (fields.Count == 0)
                {
                    continue;
                }
            }

            var pending = unsent.LastOrDefault(row => row.Kind == SyncOperationKind.Upsert);

            if (pending is not null)
            {
                // Ainda não saiu do computador: a alteração nova entra na mesma operação.
                var merged = SyncSnapshot.Read(pending.Fields);

                foreach (var (name, value) in fields)
                {
                    merged[name] = value;
                }

                pending.Fields = SyncSnapshot.Write(merged);
            }
            else
            {
                Enqueue(context, type, item.Id, SyncOperationKind.Upsert, state.ServerVersion, fields, state.OriginCreatedAt, now);
            }

            state.Snapshot = SyncSnapshot.Write(projection);
            state.UpdatedByDeviceId = deviceId;
            state.UpdatedAt = now;
            recorded = true;
        }

        // O EF pode já ter detectado as mudanças antes de chamar o interceptor:
        // os estados alterados aqui precisam entrar no mesmo SaveChanges.
        context.ChangeTracker.DetectChanges();

        return recorded;
    }

    /// <summary>O pai também está sendo excluído: a cascata do servidor leva este junto.</summary>
    private static bool ParentIsGoingToo(SyncTouched item, HashSet<(string Type, Guid Id)> deleted) =>
        item.Entry is not null
        && item.Mapper.Definition.References
            .Where(reference => reference.OnDelete == SyncDeleteBehavior.Cascade)
            .Any(reference =>
            {
                var column = item.Mapper.Columns.FirstOrDefault(candidate => candidate.Field == reference.Field && candidate.Member is null);

                return column is not null
                    && SyncEntityMapper.Read(item.Entry, column.Property) is Guid parentId
                    && deleted.Contains((reference.TargetType, parentId));
            });

    private static void Enqueue(
        MyTaskAppDbContext context,
        string type,
        Guid id,
        SyncOperationKind kind,
        long baseVersion,
        Dictionary<string, JsonElement> fields,
        DateTimeOffset? originCreatedAt,
        DateTimeOffset now) =>
        context.SyncOutbox.Add(new SyncOutboxRow
        {
            OpId = Guid.CreateVersion7(now),
            EntityType = type,
            EntityId = id,
            Kind = kind,
            BaseVersion = baseVersion,
            Fields = SyncSnapshot.Write(fields),
            OriginCreatedAt = originCreatedAt,
            CreatedAt = now,
            State = SyncOutboxState.Pending,
        });
}

/// <summary>Os campos de um snapshot ou de uma operação, em JSON.</summary>
internal static class SyncSnapshot
{
    public static Dictionary<string, JsonElement> Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, SyncJson.Options)
            ?? new Dictionary<string, JsonElement>();

        return new Dictionary<string, JsonElement>(fields, StringComparer.Ordinal);
    }

    public static string Write(IReadOnlyDictionary<string, JsonElement> fields) =>
        JsonSerializer.Serialize(fields, SyncJson.Options);
}
