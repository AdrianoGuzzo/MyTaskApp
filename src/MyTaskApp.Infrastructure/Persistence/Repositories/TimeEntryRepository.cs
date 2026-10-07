using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class TimeEntryRepository(MyTaskAppDbContext context) : ITimeEntryRepository
{
    public async Task AddAsync(TimeEntry entry, CancellationToken cancellationToken = default) =>
        await context.TimeEntries.AddAsync(entry, cancellationToken);

    public Task<TimeEntry?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.TimeEntries.SingleOrDefaultAsync(entry => entry.Id == id, cancellationToken);

    /// <remarks>
    /// <c>FirstOrDefault</c>, e não <c>Single</c>: o índice garante um só, e se
    /// algum dia não garantir, travar a tela inteira por isso seria pior do que
    /// mostrar o mais antigo.
    /// </remarks>
    public Task<TimeEntry?> FindActiveAsync(CancellationToken cancellationToken = default) =>
        context.TimeEntries
            .Where(entry => entry.EndedAt == null)
            .OrderBy(entry => entry.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <remarks>O id é v7: desempata dois períodos no mesmo tique, na ordem em que nasceram.</remarks>
    public async Task<IReadOnlyList<TimeEntry>> ListForOccurrenceAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default) =>
        await context.TimeEntries
            .Where(entry => entry.TaskOccurrenceId == occurrenceId)
            .OrderBy(entry => entry.StartedAt)
            .ThenBy(entry => entry.Id)
            .ToListAsync(cancellationToken);

    public void Remove(TimeEntry entry) => context.TimeEntries.Remove(entry);
}
