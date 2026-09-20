using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Tasks;

public sealed record CancelOccurrence(Guid OccurrenceId);

public sealed class CancelOccurrenceHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ILogger<CancelOccurrenceHandler> logger)
{
    public async Task HandleAsync(
        CancelOccurrence command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);

        task.GetOccurrence(command.OccurrenceId).Cancel();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TaskCancelled {TaskId} {OccurrenceId}", task.Id, command.OccurrenceId);
    }
}
