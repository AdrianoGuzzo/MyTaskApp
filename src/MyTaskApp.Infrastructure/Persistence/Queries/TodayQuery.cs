using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

internal sealed class TodayQuery(MyTaskAppDbContext context) : ITodayQuery
{
    public async Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        // Folga de um dia em cada ponta: o recorte exato por data local depende do
        // fuso do usuário e é decidido no domínio, não no SQL.
        var completedFrom = new DateTimeOffset(
            today.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var completedUntil = new DateTimeOffset(
            today.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var occurrences = await context.Occurrences
            .AsNoTracking()
            // Arquivado e na lixeira saem da listagem principal (§1, §4). O
            // filtro e explicito, e nao um query filter global: as areas de
            // arquivados e lixeira precisam justamente do que ele exclui, e um
            // filtro global obrigaria IgnoreQueryFilters() espalhado — inclusive
            // no caminho de restaurar, que passaria a nao encontrar o registro.
            .Where(occurrence => context.Tasks.Any(task =>
                task.Id == occurrence.TaskItemId
                && task.ArchivedAt == null
                && task.DeletedAt == null))
            .Where(occurrence =>
                (occurrence.Status == TaskItemStatus.Pending
                    && occurrence.ScheduledDate != null
                    && occurrence.ScheduledDate <= today)
                || (occurrence.Status == TaskItemStatus.Completed
                    && occurrence.CompletedAt >= completedFrom
                    && occurrence.CompletedAt < completedUntil))
            .ToListAsync(cancellationToken);

        if (occurrences.Count == 0)
        {
            return [];
        }

        var taskIds = occurrences.Select(occurrence => occurrence.TaskItemId).Distinct().ToArray();

        var definitions = await context.Tasks
            .AsNoTracking()
            .Where(task => taskIds.Contains(task.Id))
            // Tipo owned em table-sharing vira coluna simples: sem join novo.
            .Select(task => new
            {
                task.Id,
                task.Title,
                task.Priority,
                task.Description,
                Reminder = task.Reminder,
            })
            .ToListAsync(cancellationToken);

        var byId = definitions.ToDictionary(definition => definition.Id);

        // Terceira ida ao banco, qualquer que seja o tamanho da lista:
        // as etiquetas de todos os checklists de uma vez, nunca uma por linha.
        var tagLinks = await context.TaskItemTags
            .AsNoTracking()
            .Where(link => taskIds.Contains(link.TaskItemId))
            .Join(
                context.Tags,
                link => link.TagId,
                tag => tag.Id,
                (link, tag) => new { link.TaskItemId, tag.Id, tag.Name, tag.ColorHex })
            .OrderBy(row => row.Name)
            .ToListAsync(cancellationToken);

        var tagsByTask = tagLinks
            .GroupBy(row => row.TaskItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TagBadge>)group
                    .Select(row => new TagBadge(row.Id, row.Name, row.ColorHex))
                    .ToList());

        // O selo de agente em execução (ADR-029). O status vem do banco, que o
        // monitor mantém em dia com os processos — a lista não consulta o sistema.
        var agents = await context.AgentSessions
            .AsNoTracking()
            .Where(session => taskIds.Contains(session.TaskItemId)
                && session.Status == AgentSessionStatus.Running)
            .Select(session => new { session.TaskItemId, session.ProviderId })
            .ToListAsync(cancellationToken);

        var agentByTask = agents
            .GroupBy(row => row.TaskItemId)
            .ToDictionary(group => group.Key, group => group.First().ProviderId);

        return occurrences
            .Select(occurrence =>
            {
                var definition = byId[occurrence.TaskItemId];

                return new TodayOccurrenceRow(
                    occurrence.Id,
                    definition.Id,
                    definition.Title,
                    definition.Priority,
                    occurrence.ScheduledDate,
                    occurrence.ScheduledTime,
                    occurrence.Status,
                    occurrence.CompletedAt,
                    occurrence.Reminder.AcknowledgedAtUtc is null
                        ? occurrence.Reminder.WaitingSinceUtc
                        : null,
                    occurrence.Reminder.Attempt,
                    definition.Reminder,
                    definition.Description,
                    occurrence.Position,
                    tagsByTask.GetValueOrDefault(definition.Id),
                    agentByTask.GetValueOrDefault(definition.Id));
            })
            .ToList();
    }
}
