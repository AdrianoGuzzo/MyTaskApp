using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// Repositório em memória. Preferido a um mock porque os testes afirmam sobre o
/// estado resultante, não sobre quais métodos foram chamados — sobrevive a refactor.
/// </summary>
internal sealed class FakeTaskItemRepository : ITaskItemRepository, IUnitOfWork
{
    private readonly Dictionary<Guid, TaskItem> _tasks = [];

    public int SaveCount { get; private set; }

    public IReadOnlyCollection<TaskItem> Tasks => _tasks.Values;

    /// <summary>Simula falha de infraestrutura ao gravar.</summary>
    public Exception? SaveFailure { get; set; }

    public void Seed(params TaskItem[] tasks)
    {
        foreach (var task in tasks)
        {
            _tasks[task.Id] = task;
        }
    }

    public Task AddAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        _tasks[task.Id] = task;
        return Task.CompletedTask;
    }

    public Task<TaskItem?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tasks.GetValueOrDefault(id));

    public Task<TaskItem?> FindByOccurrenceIdAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            _tasks.Values.FirstOrDefault(
                task => task.Occurrences.Any(occurrence => occurrence.Id == occurrenceId)));

    public void Remove(TaskItem task) => _tasks.Remove(task.Id);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (SaveFailure is not null)
        {
            return Task.FromException(SaveFailure);
        }

        SaveCount++;
        return Task.CompletedTask;
    }
}
