using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>Arquivar manualmente (§1). Arquivar não é excluir.</summary>
public sealed record ArchiveChecklist(Guid TaskId);

public sealed class ArchiveChecklistHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    ITimeEntryRepository timeEntries,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<ArchiveChecklistHandler> logger)
{
    public async Task HandleAsync(
        ArchiveChecklist command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        task.Archive(now);

        // O que foi guardado sai da lista, e o cronômetro dele não pode
        // continuar correndo onde ninguém o vê (ADR-052).
        await RunningTimer.StopIfOnAsync(
            timeEntries,
            task.Occurrences.Select(occurrence => occurrence.Id).ToList(),
            now,
            cancellationToken);

        await audit.RecordAsync(
            TaskAuditEntry.ByUser(
                task.Id,
                task.Title,
                TaskAuditOperation.Archived,
                now,
                currentUser.Name),
            cancellationToken);

        // Um único SaveChanges: o arquivamento e o seu registro entram juntos,
        // ou não entra nenhum dos dois.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("ChecklistArchived {TaskId}", task.Id);
    }
}
