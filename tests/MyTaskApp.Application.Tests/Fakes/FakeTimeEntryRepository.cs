using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// Os períodos em memória, com os mesmos filtros e a mesma ordem do SQL
/// (ADR-052). Sobreviver a um handler novo é o que deixa o teste de "fechar e
/// abrir o app" dizer algo: o estado mora aqui, não no handler.
/// </summary>
internal sealed class FakeTimeEntryRepository : ITimeEntryRepository
{
    private readonly List<TimeEntry> _entries = [];

    public IReadOnlyList<TimeEntry> Entries => _entries;

    public void Seed(params TimeEntry[] entries) => _entries.AddRange(entries);

    public Task AddAsync(TimeEntry entry, CancellationToken cancellationToken = default)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<TimeEntry?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.Find(entry => entry.Id == id));

    public Task<TimeEntry?> FindActiveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.Where(entry => entry.IsActive).OrderBy(entry => entry.StartedAt).FirstOrDefault());

    public Task<IReadOnlyList<TimeEntry>> ListForOccurrenceAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TimeEntry>>(
        [
            .. _entries
                .Where(entry => entry.TaskOccurrenceId == occurrenceId)
                .OrderBy(entry => entry.StartedAt)
                .ThenBy(entry => entry.Id),
        ]);

    public void Remove(TimeEntry entry) => _entries.Remove(entry);
}

/// <summary>O cronômetro ativo com o título, montado dos dois fakes como o SQL faz o join.</summary>
internal sealed class FakeActiveTimerQuery(FakeTimeEntryRepository entries, FakeTaskItemRepository tasks)
    : IActiveTimerQuery
{
    public async Task<ActiveTimerView?> FindAsync(CancellationToken cancellationToken = default)
    {
        var active = await entries.FindActiveAsync(cancellationToken);

        if (active is null)
        {
            return null;
        }

        var task = await tasks.FindByOccurrenceIdAsync(active.TaskOccurrenceId, cancellationToken);

        return task is null
            ? null
            : new ActiveTimerView(active.Id, active.TaskOccurrenceId, task.Id, task.Title, active.StartedAt);
    }
}
