using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.TimeTracking;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

/// <summary>
/// Os períodos como relatório (ADR-059), num join e um lote de etiquetas. O
/// recorte exato por dia e o total são da Application; aqui só se escolhe o que
/// toca o intervalo, como o <see cref="ActivityHistoryQuery"/>.
/// </summary>
internal sealed class TimeEntryReportQuery(MyTaskAppDbContext context) : ITimeEntryReportQuery
{
    public async Task<IReadOnlyList<TimeEntryReportRow>> ListAsync(
        TimeEntryReportCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        var query =
            from entry in context.TimeEntries.AsNoTracking()
            join occurrence in context.Occurrences.AsNoTracking()
                on entry.TaskOccurrenceId equals occurrence.Id
            join task in context.Tasks.AsNoTracking()
                on occurrence.TaskItemId equals task.Id
            select new { Entry = entry, Task = task };

        if (!criteria.IncludeTrashed)
        {
            query = query.Where(row => row.Task.DeletedAt == null);
        }

        // Cai no índice de StartedAt. O que começou antes e terminou dentro conta.
        if (criteria.Until is { } until)
        {
            query = query.Where(row => row.Entry.StartedAt < until);
        }

        if (criteria.Since is { } since)
        {
            query = query.Where(row => row.Entry.EndedAt == null || row.Entry.EndedAt > since);
        }

        if (criteria.EntryId is { } entryId)
        {
            query = query.Where(row => row.Entry.Id == entryId);
        }

        if (criteria.TaskId is { } taskId)
        {
            query = query.Where(row => row.Task.Id == taskId);
        }

        if (criteria.TagId is { } tagId)
        {
            query = query.Where(row => context.TaskItemTags.Any(link =>
                link.TaskItemId == row.Task.Id && link.TagId == tagId));
        }

        if (criteria.Source is { } source)
        {
            query = query.Where(row => row.Entry.Source == source);
        }

        if (criteria.Running is { } running)
        {
            query = running
                ? query.Where(row => row.Entry.EndedAt == null)
                : query.Where(row => row.Entry.EndedAt != null);
        }

        if (criteria.ExternalKey is { } key)
        {
            var projectPrefix = key + "-%";

            query = key.Contains('-', StringComparison.Ordinal)
                ? query.Where(row => row.Task.External != null && row.Task.External.Id.ToUpper() == key)
                : query.Where(row => row.Task.External != null && EF.Functions.Like(row.Task.External.Id, projectPrefix));
        }

        var found = await query
            .OrderByDescending(row => row.Entry.StartedAt)
            .Select(row => new
            {
                row.Entry.Id,
                row.Entry.TaskOccurrenceId,
                TaskId = row.Task.Id,
                row.Task.Title,
                ExternalKey = row.Task.External == null ? null : row.Task.External.Id,
                row.Entry.StartedAt,
                row.Entry.EndedAt,
                row.Entry.Source,
                row.Entry.Note,
                row.Entry.CreatedAt,
                row.Entry.UpdatedAt,
                Archived = row.Task.ArchivedAt != null,
                Trashed = row.Task.DeletedAt != null,
            })
            .ToListAsync(cancellationToken);

        if (found.Count == 0)
        {
            return [];
        }

        // As etiquetas de todas as tarefas de uma vez, como no quadro.
        var taskIds = found.Select(row => row.TaskId).Distinct().ToArray();

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

        return found
            .Select(row => new TimeEntryReportRow(
                row.Id,
                row.TaskOccurrenceId,
                row.TaskId,
                row.Title,
                row.ExternalKey,
                tagsByTask.GetValueOrDefault(row.TaskId) ?? [],
                row.StartedAt,
                row.EndedAt,
                row.Source,
                row.Note,
                row.CreatedAt,
                row.UpdatedAt,
                row.Archived,
                row.Trashed))
            .ToList();
    }
}
