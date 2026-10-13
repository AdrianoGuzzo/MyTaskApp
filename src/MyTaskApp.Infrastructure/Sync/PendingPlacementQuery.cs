using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Devices;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Sync;

/// <summary>
/// O legado sem lugar que este computador criou, e o que está num id antigo
/// daqui (ADR-062). "Quem criou" vem do estado da sincronização: sem estado
/// (nunca sincronizou), não há outro computador para quem esconder.
/// </summary>
internal sealed class PendingPlacementQuery(MyTaskAppDbContext context) : IPendingPlacementQuery
{
    public async Task<PendingPlacements> FindAsync(Guid here, IReadOnlyCollection<Guid> hereIds, CancellationToken cancellationToken)
    {
        var ids = hereIds.ToArray();
        var formerIds = ids.Where(id => id != here).ToArray();

        var createdHere = context.SyncEntityStates
            .Where(state => state.CreatedByDeviceId != null && ids.Contains(state.CreatedByDeviceId.Value));

        var tasksHere = await context.Tasks
            .Where(task => task.PlacementKind == null
                && createdHere.Any(state => state.EntityType == SyncEntityTypes.Task && state.EntityId == task.Id))
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);

        // Uma tarefa que não é só daqui: noutro computador, em todos, ou legado
        // de outro computador (ou de origem desconhecida).
        var notOnlyHere = context.Tasks.Where(task =>
            task.PlacementKind == PlacementKind.Everywhere
            || (task.PlacementKind == PlacementKind.Device && !ids.Contains(task.PlacementDeviceId!.Value))
            || (task.PlacementKind == null
                && !createdHere.Any(state => state.EntityType == SyncEntityTypes.Task && state.EntityId == task.Id)));

        var legacyTags = await context.Tags
            .Where(tag => tag.PlacementKind == null
                && createdHere.Any(state => state.EntityType == SyncEntityTypes.Tag && state.EntityId == tag.Id))
            .Select(tag => new
            {
                tag.Id,
                Shared = context.TaskItemTags.Any(link => link.TagId == tag.Id
                    && notOnlyHere.Any(task => task.Id == link.TaskItemId)),
            })
            .ToListAsync(cancellationToken);

        var tasksToRehome = formerIds.Length == 0
            ? []
            : await context.Tasks
                .Where(task => task.PlacementKind == PlacementKind.Device && formerIds.Contains(task.PlacementDeviceId!.Value))
                .Select(task => task.Id)
                .ToListAsync(cancellationToken);

        var tagsToRehome = formerIds.Length == 0
            ? []
            : await context.Tags
                .Where(tag => tag.PlacementKind == PlacementKind.Device && formerIds.Contains(tag.PlacementDeviceId!.Value))
                .Select(tag => tag.Id)
                .ToListAsync(cancellationToken);

        return new PendingPlacements(
            tasksHere,
            [.. legacyTags.Where(tag => !tag.Shared).Select(tag => tag.Id)],
            [.. legacyTags.Where(tag => tag.Shared).Select(tag => tag.Id)],
            tasksToRehome,
            tagsToRehome);
    }
}
