using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tasks;

public sealed record UpdateTask(
    Guid TaskId,
    string Title,
    string? Description,
    TaskPriority Priority);

public sealed class UpdateTaskHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ILogger<UpdateTaskHandler> logger)
{
    public async Task HandleAsync(UpdateTask command, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        task.Update(command.Title, command.Description, command.Priority);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TaskUpdated {TaskId}", task.Id);
    }
}
