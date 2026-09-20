using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Tasks;

public sealed record DeleteTask(Guid TaskId);

public sealed class DeleteTaskHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ILogger<DeleteTaskHandler> logger)
{
    public async Task HandleAsync(DeleteTask command, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        tasks.Remove(task);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TaskDeleted {TaskId}", task.Id);
    }
}
