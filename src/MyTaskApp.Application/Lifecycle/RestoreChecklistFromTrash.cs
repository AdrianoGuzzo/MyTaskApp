using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>Tira da lixeira dentro do prazo (§5).</summary>
public sealed record RestoreChecklistFromTrash(Guid TaskId);

public sealed class RestoreChecklistFromTrashHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<RestoreChecklistFromTrashHandler> logger)
{
    public async Task HandleAsync(
        RestoreChecklistFromTrash command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        task.RestoreFromTrash();

        // Volta para onde estava. Quem foi excluído já arquivado continua
        // arquivado — e continua calado, porque arquivado não cobra atenção.
        if (!task.IsArchived)
        {
            ReminderArming.ArmPending(task, clock, now);
        }

        await audit.RecordAsync(
            TaskAuditEntry.ByUser(
                task.Id,
                task.Title,
                TaskAuditOperation.RestoredFromTrash,
                now,
                currentUser.Name),
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "ChecklistRestoredFromTrash {TaskId} {StillArchived}",
            task.Id,
            task.IsArchived);
    }
}
