using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Devices;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

internal sealed class DeadlineAlertQuery(MyTaskAppDbContext context, DevicePlacementState? placements = null) : IDeadlineAlertQuery
{
    public async Task<IReadOnlyList<DeadlineCandidateRow>> GetCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        // Tarefa de outro computador cobra lá, não aqui (ADR-062).
        var tasks = context.Tasks.VisibleIn(placements?.HereFilter ?? PlacementFilter.All);

        // Cai no índice parcial IX_TaskOccurrences_Deadline.
        var occurrences = await context.Occurrences
            .AsNoTracking()
            // Guardado não cobra prazo, pelo mesmo motivo do DueReminderQuery.
            .Where(occurrence => tasks.Any(task =>
                task.Id == occurrence.TaskItemId
                && task.ArchivedAt == null
                && task.DeletedAt == null))
            .Where(occurrence =>
                occurrence.Status == TaskItemStatus.Pending
                && occurrence.DeadlineDate != null
                && occurrence.DeadlineTime != null)
            .ToListAsync(cancellationToken);

        if (occurrences.Count == 0)
        {
            return [];
        }

        var taskIds = occurrences.Select(occurrence => occurrence.TaskItemId).Distinct().ToArray();

        var definitions = await context.Tasks
            .AsNoTracking()
            .Where(task => taskIds.Contains(task.Id))
            .Select(task => new { task.Id, task.Title, task.DeadlineAlerts })
            .ToDictionaryAsync(task => task.Id, cancellationToken);

        return occurrences
            // Prazo mais próximo primeiro: se o lote virar resumo, a ordem já é a certa.
            .OrderBy(occurrence => occurrence.DeadlineDate)
            .ThenBy(occurrence => occurrence.DeadlineTime)
            .Select(occurrence =>
            {
                var definition = definitions[occurrence.TaskItemId];

                return new DeadlineCandidateRow(
                    occurrence.Id,
                    definition.Id,
                    definition.Title,
                    occurrence.Deadline!,
                    definition.DeadlineAlerts,
                    occurrence.DeadlineAlert);
            })
            .ToList();
    }
}
