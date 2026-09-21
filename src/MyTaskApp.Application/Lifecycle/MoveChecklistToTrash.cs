using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>
/// Exclusão reversível (§4, §5): nada sai do banco. O registro é marcado, a
/// data e o autor ficam gravados, e o prazo de retenção começa a correr.
/// </summary>
public sealed record MoveChecklistToTrash(Guid TaskId);

public sealed class MoveChecklistToTrashHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<MoveChecklistToTrashHandler> logger)
{
    public async Task HandleAsync(
        MoveChecklistToTrash command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        task.MoveToTrash(now, currentUser.Name);

        await audit.RecordAsync(
            TaskAuditEntry.ByUser(
                task.Id,
                task.Title,
                TaskAuditOperation.MovedToTrash,
                now,
                currentUser.Name),
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("ChecklistMovedToTrash {TaskId}", task.Id);
    }
}
