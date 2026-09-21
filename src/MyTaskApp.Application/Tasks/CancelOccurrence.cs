using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Application.Tasks;

public sealed record CancelOccurrence(Guid OccurrenceId);

public sealed class CancelOccurrenceHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<CancelOccurrenceHandler> logger)
{
    public async Task HandleAsync(
        CancelOccurrence command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        // Cancelar a ultima pendente pode concluir o checklist, quando ja havia
        // outra concluida. Por isso passa pelo mesmo caminho das demais.
        var wasConcluded = task.ConcludedAt is not null;

        task.CancelOccurrence(command.OccurrenceId);

        await ChecklistConclusionAudit.RecordIfChangedAsync(
            audit, task, wasConcluded, now, currentUser.Name, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TaskCancelled {TaskId} {OccurrenceId}", task.Id, command.OccurrenceId);
    }
}
