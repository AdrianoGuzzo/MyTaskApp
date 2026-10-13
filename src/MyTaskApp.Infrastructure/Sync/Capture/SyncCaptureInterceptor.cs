using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Sync.Mapping;
using MyTaskApp.Infrastructure.Sync.Persistence;

namespace MyTaskApp.Infrastructure.Sync.Capture;

/// <summary>
/// Transforma cada <c>SaveChanges</c> de negócio em operações no outbox, no
/// <b>mesmo</b> <c>SaveChanges</c> — e portanto na mesma transação (ADR-060).
/// Os ~110 casos de uso não mudam: quem grava não sabe que existe sincronização.
/// O que entra no outbox é decidido pelo <see cref="SyncRecorder"/>.
/// </summary>
internal sealed class SyncCaptureInterceptor(
    SyncCaptureSwitch captureSwitch,
    IDeviceIdentity device,
    ISyncTrigger trigger,
    TimeProvider timeProvider) : SaveChangesInterceptor
{
    private static readonly object Captured = new();

    private readonly ConditionalWeakTable<DbContext, object> _captured = new();

    private readonly SyncRecorder _recorder = new(device, timeProvider);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (ShouldCapture(eventData.Context) is { } context)
        {
            var touched = CollectTouched(context);

            if (touched.Count > 0)
            {
                var ids = touched.Select(item => item.Id).Distinct().ToList();
                Remember(context, _recorder.Record(context, touched, LoadStates(context, ids), LoadOutbox(context, ids)));
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
                Remember(context, _recorder.Record(context, touched, states, outbox));
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

    private void Remember(DbContext context, bool recorded)
    {
        if (recorded)
        {
            _captured.AddOrUpdate(context, Captured);
        }
    }

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
    private static List<SyncTouched> CollectTouched(MyTaskAppDbContext context)
    {
        context.ChangeTracker.DetectChanges();

        var touched = new Dictionary<(string Type, Guid Id), SyncTouched>();

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
            touched.TryAdd((mapper.Definition.Type, id), new SyncTouched(mapper, root, id));
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
}
