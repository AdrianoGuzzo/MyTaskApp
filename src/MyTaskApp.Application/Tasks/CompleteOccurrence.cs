using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Application.Tasks;

public sealed record CompleteOccurrence(Guid OccurrenceId);

public sealed class CompleteOccurrenceHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ITimeEntryRepository timeEntries,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<CompleteOccurrenceHandler> logger)
{
    public async Task HandleAsync(
        CompleteOccurrence command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        // Pela raiz, e nao direto na ocorrencia: e ela que mantem a data de
        // conclusao do checklist coerente com as ocorrencias, e que recusa
        // mexer no que ja foi arquivado ou excluido.
        var wasConcluded = task.ConcludedAt is not null;

        task.CompleteOccurrence(command.OccurrenceId, now);

        // Trabalho concluído não continua contando (ADR-052): o período fecha
        // no instante da conclusão, no mesmo SaveChanges.
        await RunningTimer.StopIfOnAsync(timeEntries, [command.OccurrenceId], now, cancellationToken);

        await ChecklistConclusionAudit.RecordIfChangedAsync(
            audit, task, wasConcluded, now, currentUser.Name, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TaskCompleted {TaskId} {OccurrenceId}", task.Id, command.OccurrenceId);
    }
}
