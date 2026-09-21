using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>Tira do arquivo e devolve à listagem ativa (§1).</summary>
public sealed record RestoreChecklist(Guid TaskId);

public sealed class RestoreChecklistHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<RestoreChecklistHandler> logger)
{
    public async Task HandleAsync(
        RestoreChecklist command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        task.RestoreFromArchive();

        // Arquivar calou os lembretes; voltar à lista principal os traz de volta.
        // Rearmados a partir de agora, e não do instante em que foram calados:
        // ressuscitar um horário vencido avisaria na hora, do nada.
        ReminderArming.ArmPending(task, clock, now);

        await audit.RecordAsync(
            TaskAuditEntry.ByUser(
                task.Id,
                task.Title,
                TaskAuditOperation.Restored,
                now,
                currentUser.Name),
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("ChecklistRestored {TaskId}", task.Id);
    }
}
