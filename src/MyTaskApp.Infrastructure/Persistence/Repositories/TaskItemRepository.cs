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
            .SingleOrDefaultAsync(task => task.Id == id, cancellationToken);

    public Task<TaskItem?> FindByOccurrenceIdAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default) =>
        context.Tasks
            .Include(task => task.Occurrences)
            .SingleOrDefaultAsync(
                task => task.Occurrences.Any(occurrence => occurrence.Id == occurrenceId),
                cancellationToken);

    public void Remove(TaskItem task) => context.Tasks.Remove(task);
}
