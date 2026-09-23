using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class TaskItemRepository(MyTaskAppDbContext context) : ITaskItemRepository
{
    public async Task AddAsync(TaskItem task, CancellationToken cancellationToken = default) =>
        await context.Tasks.AddAsync(task, cancellationToken);

    public Task<TaskItem?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.Tasks
            .Include(task => task.Occurrences)
            .Include(task => task.Tags)
            .Include(task => task.Development)
            .SingleOrDefaultAsync(task => task.Id == id, cancellationToken);

    public Task<TaskItem?> FindByOccurrenceIdAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default) =>
        context.Tasks
            .Include(task => task.Occurrences)
            .Include(task => task.Tags)
            .Include(task => task.Development)
            .SingleOrDefaultAsync(
                task => task.Occurrences.Any(occurrence => occurrence.Id == occurrenceId),
                cancellationToken);

    /// <remarks>
    /// O <c>Include</c> traz a coleção <b>inteira</b> de cada agregado casado, e
    /// não só as ocorrências pedidas: agregado carrega inteiro, senão uma
    /// invariante da raiz passaria a raciocinar sobre meia coleção.
    /// </remarks>
    public async Task<IReadOnlyList<TaskItem>> FindByOccurrenceIdsAsync(
        IReadOnlyCollection<Guid> occurrenceIds,
        CancellationToken cancellationToken = default) =>
        await context.Tasks
            .Include(task => task.Occurrences)
            .Include(task => task.Tags)
            .Include(task => task.Development)
            .Where(task => task.Occurrences.Any(
                occurrence => occurrenceIds.Contains(occurrence.Id)))
            .ToListAsync(cancellationToken);

    public void Remove(TaskItem task) => context.Tasks.Remove(task);
}
