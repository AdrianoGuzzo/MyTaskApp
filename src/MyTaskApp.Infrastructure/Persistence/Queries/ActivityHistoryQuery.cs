using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.History;
using MyTaskApp.Domain.Planning;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

/// <summary>
/// O histórico dos últimos dias (ADR-053) em duas idas ao banco, qualquer que
/// seja o tamanho dele: as conclusões e os períodos, cada um com o título num
/// join. Nada é lido fora do intervalo.
/// </summary>
/// <remarks>
/// O arquivado entra: foi feito naquela semana, e arquivar é guardar, não
/// desfazer. A lixeira não entra — é o que o usuário mandou embora (ADR-020).
/// </remarks>
internal sealed class ActivityHistoryQuery(MyTaskAppDbContext context) : IActivityHistoryQuery
{
    public async Task<ActivityHistoryRows> GetAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        // Cai no índice de CompletedAt.
        var completions = await (
                from occurrence in context.Occurrences.AsNoTracking()
                where occurrence.Status == TaskItemStatus.Completed
                    && occurrence.CompletedAt >= since
                    && occurrence.CompletedAt < until
                join task in context.Tasks.AsNoTracking()
                    on occurrence.TaskItemId equals task.Id
                where task.DeletedAt == null
                select new ActivityCompletion(task.Id, occurrence.Id, task.Title, occurrence.CompletedAt!.Value))
            .ToListAsync(cancellationToken);

        // Cai no índice de StartedAt. O período que começou antes do intervalo e
        // terminou dentro dele também conta; o recorte exato é do domínio.
        var periods = await (
                from entry in context.TimeEntries.AsNoTracking()
                where entry.StartedAt < until
                    && (entry.EndedAt == null || entry.EndedAt > since)
                join occurrence in context.Occurrences.AsNoTracking()
                    on entry.TaskOccurrenceId equals occurrence.Id
                join task in context.Tasks.AsNoTracking()
                    on occurrence.TaskItemId equals task.Id
                where task.DeletedAt == null
                select new ActivityPeriod(task.Id, occurrence.Id, task.Title, entry.StartedAt, entry.EndedAt))
            .ToListAsync(cancellationToken);

        return new ActivityHistoryRows(completions, periods);
    }
}
