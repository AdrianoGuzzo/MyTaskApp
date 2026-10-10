using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

/// <summary>
/// A busca de tarefas (ADR-059). Filtra no banco o que o banco sabe responder e
/// devolve a mesma linha do quadro Hoje, montada pelo mesmo
/// <see cref="TodayQuery.DescribeAsync"/> — nunca uma segunda tradução da tarefa.
/// </summary>
internal sealed class TaskSearchQuery(MyTaskAppDbContext context) : ITaskSearchQuery
{
    public async Task<IReadOnlyList<TaskSearchRow>> SearchAsync(
        TaskSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        var tasks = context.Tasks.AsNoTracking();

        // A lixeira vence o arquivo, e o arquivo vence a conclusão — a mesma
        // precedência de TaskItem.Lifecycle e de ChecklistArchiveQuery.
        var active = criteria.Lifecycles.Contains(TaskLifecycle.Active);
        var completed = criteria.Lifecycles.Contains(TaskLifecycle.Completed);
        var archived = criteria.Lifecycles.Contains(TaskLifecycle.Archived);
        var trashed = criteria.Lifecycles.Contains(TaskLifecycle.Trashed);

        tasks = tasks.Where(task =>
            (trashed && task.DeletedAt != null)
            || (archived && task.ArchivedAt != null && task.DeletedAt == null)
            || (completed && task.ConcludedAt != null && task.ArchivedAt == null && task.DeletedAt == null)
            || (active && task.ConcludedAt == null && task.ArchivedAt == null && task.DeletedAt == null));

        if (criteria.TaskIds is { Count: > 0 } taskIds)
        {
            tasks = tasks.Where(task => taskIds.Contains(task.Id));
        }

        if (ChecklistArchiveQuery.BuildPattern(criteria.Text) is { } pattern)
        {
            tasks = tasks.Where(task =>
                EF.Functions.Like(task.Title, pattern, ChecklistArchiveQuery.LikeEscape)
                || (task.Description != null
                    && EF.Functions.Like(task.Description, pattern, ChecklistArchiveQuery.LikeEscape))
                || (task.NextAction != null
                    && EF.Functions.Like(task.NextAction, pattern, ChecklistArchiveQuery.LikeEscape))
                || (task.External != null
                    && (EF.Functions.Like(task.External.Id, pattern, ChecklistArchiveQuery.LikeEscape)
                        || EF.Functions.Like(task.External.Title, pattern, ChecklistArchiveQuery.LikeEscape))));
        }

        if (criteria.TagIds is { Count: > 0 } tagIds)
        {
            tasks = tasks.Where(task => context.TaskItemTags.Any(link =>
                link.TaskItemId == task.Id && tagIds.Contains(link.TagId)));
        }

        if (criteria.Priorities is { Count: > 0 } priorities)
        {
            tasks = tasks.Where(task => priorities.Contains(task.Priority));
        }

        if (NormalizeKey(criteria.ExternalKey) is { } key)
        {
            // Com hífen é a issue; sem, o projeto inteiro: "ECO" casa "ECO-1" e "ECO-42".
            var projectPrefix = key + "-%";

            tasks = key.Contains('-', StringComparison.Ordinal)
                ? tasks.Where(task => task.External != null && task.External.Id.ToUpper() == key)
                : tasks.Where(task => task.External != null && EF.Functions.Like(task.External.Id, projectPrefix));
        }

        var query =
            from occurrence in context.Occurrences.AsNoTracking()
            join task in tasks on occurrence.TaskItemId equals task.Id
            select new
            {
                Occurrence = occurrence,
                task.CreatedAt,
                task.ConcludedAt,
                task.ArchivedAt,
                task.DeletedAt,
                task.DeletedBy,
            };

        if (criteria.Statuses is { Count: > 0 } statuses)
        {
            query = query.Where(row => statuses.Contains(row.Occurrence.Status));
        }

        if (criteria.HasSchedule is { } hasSchedule)
        {
            query = hasSchedule
                ? query.Where(row => row.Occurrence.ScheduledDate != null)
                : query.Where(row => row.Occurrence.ScheduledDate == null);
        }

        if (criteria.ScheduledFrom is { } scheduledFrom)
        {
            query = query.Where(row => row.Occurrence.ScheduledDate >= scheduledFrom);
        }

        if (criteria.ScheduledTo is { } scheduledTo)
        {
            query = query.Where(row => row.Occurrence.ScheduledDate <= scheduledTo);
        }

        if (criteria.HasDeadline is { } hasDeadline)
        {
            query = hasDeadline
                ? query.Where(row => row.Occurrence.DeadlineDate != null)
                : query.Where(row => row.Occurrence.DeadlineDate == null);
        }

        if (criteria.DeadlineFrom is { } deadlineFrom)
        {
            query = query.Where(row => row.Occurrence.DeadlineDate >= deadlineFrom);
        }

        if (criteria.DeadlineTo is { } deadlineTo)
        {
            query = query.Where(row => row.Occurrence.DeadlineDate <= deadlineTo);
        }

        if (criteria.OverdueCandidatesOn is { } day)
        {
            query = query.Where(row => row.Occurrence.Status == TaskItemStatus.Pending
                && ((row.Occurrence.ScheduledDate != null && row.Occurrence.ScheduledDate <= day)
                    || (row.Occurrence.DeadlineDate != null && row.Occurrence.DeadlineDate <= day)));
        }

        // Um a mais que o teto: é ele que diz a quem chamou que a busca cortou.
        var found = await query
            .OrderByDescending(row => row.CreatedAt)
            .Take(TaskSearchCriteria.MaxRows + 1)
            .ToListAsync(cancellationToken);

        if (found.Count == 0)
        {
            return [];
        }

        var described = await new TodayQuery(context).DescribeAsync(
            found.Select(row => row.Occurrence).ToList(),
            cancellationToken);

        // DescribeAsync preserva a ordem de entrada: o índice casa as duas listas.
        return described
            .Select((row, index) => new TaskSearchRow(
                row,
                found[index].CreatedAt,
                found[index].ConcludedAt,
                found[index].ArchivedAt,
                found[index].DeletedAt,
                found[index].DeletedBy))
            .ToList();
    }

    public async Task<IReadOnlyList<TaskCountRow>> ListForCountingAsync(
        CancellationToken cancellationToken = default) =>
        await (
                from occurrence in context.Occurrences.AsNoTracking()
                join task in context.Tasks.AsNoTracking() on occurrence.TaskItemId equals task.Id
                select new TaskCountRow(
                    task.Id,
                    occurrence.Id,
                    task.Priority,
                    task.CreatedAt,
                    task.ConcludedAt,
                    task.ArchivedAt,
                    task.DeletedAt,
                    occurrence.Status,
                    occurrence.ScheduledDate,
                    occurrence.ScheduledTime,
                    occurrence.CompletedAt,
                    occurrence.DeadlineDate,
                    occurrence.DeadlineTime))
            .ToListAsync(cancellationToken);

    private static string? NormalizeKey(string? key)
    {
        var trimmed = key?.Trim();

        // Só letras, dígitos e hífen: o resto não é chave de issue, e nem chega ao LIKE.
        return string.IsNullOrEmpty(trimmed) || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? null
            : trimmed.ToUpperInvariant();
    }
}
