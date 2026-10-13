using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Sync.Mapping;
using MyTaskApp.Infrastructure.Sync.Persistence;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Sync.Capture;

/// <summary>
/// Transforma cada <c>SaveChanges</c> de negócio em operações no outbox, no
/// <b>mesmo</b> <c>SaveChanges</c> — e portanto na mesma transação (ADR-060).
/// Os ~110 casos de uso não mudam: quem grava não sabe que existe sincronização.
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
internal sealed class SyncCaptureInterceptor(
    SyncCaptureSwitch captureSwitch,
    IDeviceIdentity device,
    ISyncTrigger trigger,
    TimeProvider timeProvider) : SaveChangesInterceptor
{
    private static readonly object Captured = new();

    private readonly ConditionalWeakTable<DbContext, object> _captured = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (ShouldCapture(eventData.Context) is { } context)
        {
            var touched = CollectTouched(context);

            if (touched.Count > 0)
            {
                var ids = touched.Select(item => item.Id).Distinct().ToList();
                Record(context, touched, LoadStates(context, ids), LoadOutbox(context, ids));
            }
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (ShouldCapture(eventData.Context) is { } context)
        {
            var touched = CollectTouched(context);

            if (touched.Count > 0)
            {
                var ids = touched.Select(item => item.Id).Distinct().ToList();
                var states = await LoadStates(context, ids).ToListAsync(cancellationToken);
                var outbox = await LoadOutbox(context, ids).ToListAsync(cancellationToken);
                Record(context, touched, states, outbox);
            }
        }

        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        SignalIfCaptured(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        SignalIfCaptured(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Forget(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    private MyTaskAppDbContext? ShouldCapture(DbContext? context) =>
        context is MyTaskAppDbContext app && captureSwitch.IsLinked && !SyncSuppression.IsActive ? app : null;

    private void SignalIfCaptured(DbContext? context)
    {
        if (context is not null && _captured.Remove(context))
        {
            trigger.Signal();
        }
    }

    private void Forget(DbContext? context)
    {
        if (context is not null)
        {
            _captured.Remove(context);
        }
    }

    private static IQueryable<SyncEntityStateRow> LoadStates(MyTaskAppDbContext context, List<Guid> ids) =>
        context.SyncEntityStates.Where(state => ids.Contains(state.EntityId));

    private static IQueryable<SyncOutboxRow> LoadOutbox(MyTaskAppDbContext context, List<Guid> ids) =>
        context.SyncOutbox.Where(row => ids.Contains(row.EntityId));

    /// <summary>
    /// As entidades sincronizadas que este <c>SaveChanges</c> toca: a própria,
    /// o dono de um owned alterado e o pai de uma linha de lista.
    /// </summary>
    private static List<Touched> CollectTouched(MyTaskAppDbContext context)
    {
        context.ChangeTracker.DetectChanges();

        var touched = new Dictionary<(string Type, Guid Id), Touched>();

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var (mapper, root) = Resolve(context, entry);

            if (mapper is null || root is null)
            {
                continue;
            }

            var id = mapper.IdOf(root);
            touched.TryAdd((mapper.Definition.Type, id), new Touched(mapper, root, id));
        }

        return [.. touched.Values];
    }

    private static (SyncEntityMapper? Mapper, EntityEntry? Root) Resolve(MyTaskAppDbContext context, EntityEntry entry)
    {
        var type = entry.Metadata.ClrType;

        if (SyncMappers.ForClrType(type) is { } direct)
        {
            return (direct, entry);
        }

        if (entry.Metadata.IsOwned() && entry.Metadata.FindOwnership() is { } ownership)
        {
            var key = ownership.Properties.Select(property => entry.Property(property.Name).CurrentValue).ToArray();
            var owner = FindTracked(context, ownership.PrincipalEntityType.ClrType, key);

            return owner is not null && SyncMappers.ForClrType(owner.Metadata.ClrType) is { } ownerMapper
                ? (ownerMapper, owner)
                : (null, null);
        }

        if (SyncMappers.ForChildType(type) is { } child)
        {
            var parentId = entry.Property(child.List.ParentKey).CurrentValue;
            var parent = FindTracked(context, child.Parent.ClrType, [parentId]);

            return parent is not null ? (child.Parent, parent) : (null, null);
        }

        return (null, null);
    }

    private static EntityEntry? FindTracked(MyTaskAppDbContext context, Type type, object?[] key) =>
        context.ChangeTracker.Entries()
            .FirstOrDefault(candidate => candidate.Metadata.ClrType == type
                && candidate.Metadata.FindPrimaryKey()!.Properties
                    .Select(property => candidate.Property(property.Name).CurrentValue)
                    .SequenceEqual(key));

    private void Record(
        MyTaskAppDbContext context,
        List<Touched> touched,
        IEnumerable<SyncEntityStateRow> loadedStates,
        IEnumerable<SyncOutboxRow> loadedOutbox)
    {
        var now = timeProvider.GetUtcNow();
        var deviceId = device.DeviceId;
        var states = loadedStates.ToDictionary(state => (state.EntityType, state.EntityId));
        var outbox = loadedOutbox.ToList();
        var recorded = false;

        var deleted = touched
            .Where(item => item.Entry.State == EntityState.Deleted)
            .Select(item => (item.Mapper.Definition.Type, item.Id))
            .ToHashSet();

        // Pai antes do filho na criação; filho antes do pai na exclusão.
        var ordered = touched
            .OrderBy(item => item.Entry.State == EntityState.Deleted)
            .ThenBy(item => item.Entry.State == EntityState.Deleted
                ? -item.Mapper.Definition.Rank
                : item.Mapper.Definition.Rank);

        foreach (var item in ordered)
        {
            var type = item.Mapper.Definition.Type;
            states.TryGetValue((type, item.Id), out var state);
            var entityOutbox = outbox.Where(row => row.EntityType == type && row.EntityId == item.Id).ToList();
            var unsent = entityOutbox.Where(row => row is { Attempted: false, State: SyncOutboxState.Pending }).ToList();

            if (item.Entry.State == EntityState.Deleted)
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

            if (!item.Mapper.IsSyncable(item.Entry))
            {
                continue;
            }

            var projection = item.Mapper.Project(item.Entry);
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
                        OriginCreatedAt = item.Mapper.OriginCreatedAt(item.Entry) ?? now,
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

        if (recorded)
        {
            _captured.AddOrUpdate(context, Captured);
        }
    }

    /// <summary>O pai também está sendo excluído: a cascata do servidor leva este junto.</summary>
    private static bool ParentIsGoingToo(Touched item, HashSet<(string Type, Guid Id)> deleted) =>
        item.Mapper.Definition.References
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

    private sealed record Touched(SyncEntityMapper Mapper, EntityEntry Entry, Guid Id);
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
