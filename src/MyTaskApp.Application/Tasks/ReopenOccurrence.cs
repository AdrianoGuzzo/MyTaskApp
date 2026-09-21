using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Application.Tasks;

public sealed record ReopenOccurrence(Guid OccurrenceId);

public sealed class ReopenOccurrenceHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<ReopenOccurrenceHandler> logger)
{
    public async Task HandleAsync(
        ReopenOccurrence command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        var wasConcluded = task.ConcludedAt is not null;

        var occurrence = task.ReopenOccurrence(command.OccurrenceId);

        // Voltou a ser pendente: o lembrete volta com ela, do zero.
        ReminderArming.Arm(task, occurrence, clock, now);

        await ChecklistConclusionAudit.RecordIfChangedAsync(
            audit, task, wasConcluded, now, currentUser.Name, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TaskReopened {TaskId} {OccurrenceId}", task.Id, command.OccurrenceId);
    }
}
