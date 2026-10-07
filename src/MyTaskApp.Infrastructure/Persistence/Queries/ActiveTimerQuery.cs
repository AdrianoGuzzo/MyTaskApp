using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.TimeTracking;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

/// <summary>
/// O cronômetro ativo com o título da tarefa, numa ida ao banco (ADR-052). Cai no
/// índice <c>IX_TimeEntries_SingleActive</c>, cujo filtro é o mesmo predicado.
/// </summary>
internal sealed class ActiveTimerQuery(MyTaskAppDbContext context) : IActiveTimerQuery
{
    public Task<ActiveTimerView?> FindAsync(CancellationToken cancellationToken = default) =>
        (from entry in context.TimeEntries.AsNoTracking()
         where entry.EndedAt == null
         join occurrence in context.Occurrences.AsNoTracking()
             on entry.TaskOccurrenceId equals occurrence.Id
         join task in context.Tasks.AsNoTracking()
             on occurrence.TaskItemId equals task.Id
         orderby entry.StartedAt
         select new ActiveTimerView(entry.Id, occurrence.Id, task.Id, task.Title, entry.StartedAt))
        .FirstOrDefaultAsync(cancellationToken);
}
