using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

internal sealed class DueReminderQuery(MyTaskAppDbContext context) : IDueReminderQuery
{
    public async Task<IReadOnlyList<DueReminderRow>> GetDueAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        // Cai no índice parcial: só ocorrências armadas e ainda sem resposta.
        var due = await context.Occurrences
            .AsNoTracking()
            // Cinto e suspensório: arquivar e excluir já desarmam os lembretes
            // no agregado, mas um checklist guardado não pode voltar a tocar
            // nem por uma linha que tenha escapado — por migração, por edição
            // manual do banco, por um caminho futuro que esqueça de desarmar.
            .Where(occurrence => context.Tasks.Any(task =>
                task.Id == occurrence.TaskItemId
                && task.ArchivedAt == null
                && task.DeletedAt == null))
            .Where(occurrence =>
                occurrence.Status == TaskItemStatus.Pending
                && occurrence.Reminder.NextFireAtUtc != null
                && occurrence.Reminder.NextFireAtUtc <= nowUtc
                && occurrence.Reminder.AcknowledgedAtUtc == null)
            // Mais antigo primeiro: quem espera há mais tempo é avisado antes.
            .OrderBy(occurrence => occurrence.Reminder.NextFireAtUtc)
            .Take(limit)
            .Select(occurrence => new
            {
                occurrence.Id,
                occurrence.TaskItemId,
                occurrence.ScheduledDate,
                occurrence.ScheduledTime,
                occurrence.Reminder.NextFireAtUtc,
                occurrence.Reminder.Attempt,
                occurrence.Reminder.WaitingSinceUtc,
            })
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return [];
        }

        var taskIds = due.Select(occurrence => occurrence.TaskItemId).Distinct().ToArray();

        var definitions = await context.Tasks
            .AsNoTracking()
            .Where(task => taskIds.Contains(task.Id))
            .Select(task => new
            {
                task.Id,
                task.Title,
                task.Priority,
                task.Reminder.Channels,
            })
            .ToListAsync(cancellationToken);

        var byId = definitions.ToDictionary(definition => definition.Id);

        return due
            .Select(occurrence =>
            {
                var definition = byId[occurrence.TaskItemId];

                return new DueReminderRow(
                    occurrence.Id,
                    definition.Id,
                    definition.Title,
                    definition.Priority,
                    occurrence.ScheduledDate,
                    occurrence.ScheduledTime,
                    occurrence.NextFireAtUtc!.Value,
                    occurrence.Attempt,
                    occurrence.WaitingSinceUtc,
                    definition.Channels);
            })
            .ToList();
    }
}
