using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Tasks;

public sealed record CompleteOccurrence(Guid OccurrenceId);

public sealed class CompleteOccurrenceHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<CompleteOccurrenceHandler> logger)
{
    public async Task HandleAsync(
        CompleteOccurrence command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);

        task.GetOccurrence(command.OccurrenceId).Complete(timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TaskCompleted {TaskId} {OccurrenceId}", task.Id, command.OccurrenceId);
    }
}
