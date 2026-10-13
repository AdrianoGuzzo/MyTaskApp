using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Sync.Capture;
using MyTaskApp.Infrastructure.Sync.Mapping;
using MyTaskApp.Infrastructure.Sync.Persistence;

namespace MyTaskApp.Infrastructure.Sync.Apply;

/// <summary>
/// Compara tudo o que existe no banco com o que a sincronização sabe
/// (ADR-061). Na primeira conexão, é o que manda as tarefas que já existiam;
/// depois de um pull completo, só o que nunca subiu vira operação — o que
/// veio do servidor já tem estado e não é enviado de volta.
/// </summary>
internal sealed class SyncReconciler(
    MyTaskAppDbContext context,
    IDeviceIdentity device,
    TimeProvider timeProvider,
    ISyncTrigger trigger,
    ILogger<SyncReconciler> logger) : ISyncReconciler
{
    public async Task<int> ScanAsync(CancellationToken cancellationToken = default)
    {
        // As operações entram pelo recorder; o SaveChanges no fim não deve capturar de novo.
        using var suppression = SyncSuppression.Enter();

        await context.Tasks.LoadAsync(cancellationToken);
        await context.Occurrences.LoadAsync(cancellationToken);
        await context.TaskItemTags.LoadAsync(cancellationToken);
        await context.Tags.LoadAsync(cancellationToken);
        await context.TagDirectories.LoadAsync(cancellationToken);
        await context.TagDirectoryCommands.LoadAsync(cancellationToken);
        await context.DevelopmentCommands.Include(command => command.Parameters).LoadAsync(cancellationToken);
        await context.TaskDevelopments.Include(development => development.Commands).LoadAsync(cancellationToken);
        await context.TimeEntries.LoadAsync(cancellationToken);
        await context.StickyNotes.LoadAsync(cancellationToken);

        var present = new List<SyncTouched>();

        foreach (var mapper in SyncMappers.All)
        {
            present.AddRange(context.ChangeTracker.Entries()
                .Where(entry => entry.Metadata.ClrType == mapper.ClrType)
                .Select(entry => new SyncTouched(mapper, entry, mapper.IdOf(entry))));
        }

        var states = await context.SyncEntityStates.ToListAsync(cancellationToken);
        var outbox = await context.SyncOutbox.ToListAsync(cancellationToken);

        // Conhecidas pela sincronização e que não estão mais no banco: saíram por cascata.
        var known = present.Select(item => (item.Mapper.Definition.Type, item.Id)).ToHashSet();
        var gone = states
            .Where(state => !state.IsDeleted && !known.Contains((state.EntityType, state.EntityId)))
            .Select(state => SyncMappers.ForEntityType(state.EntityType) is { } mapper ? new SyncTouched(mapper, null, state.EntityId) : null)
            .OfType<SyncTouched>();

        new SyncRecorder(device, timeProvider).Record(context, [.. present, .. gone], states, outbox);

        var created = context.ChangeTracker.Entries<SyncOutboxRow>().Count(entry => entry.State == EntityState.Added);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("SyncScanCompleted {Operations}", created);

        if (created > 0)
        {
            trigger.Signal();
        }

        return created;
    }
}
