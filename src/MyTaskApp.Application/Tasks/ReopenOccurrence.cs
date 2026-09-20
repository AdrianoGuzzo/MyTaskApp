using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;

namespace MyTaskApp.Application.Tasks;

public sealed record ReopenOccurrence(Guid OccurrenceId);

public sealed class ReopenOccurrenceHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<ReopenOccurrenceHandler> logger)
{
    public async Task HandleAsync(
        ReopenOccurrence command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);

        var occurrence = task.GetOccurrence(command.OccurrenceId);

        occurrence.Reopen();

        // Voltou a ser pendente: o lembrete volta com ela, do zero.
        ReminderArming.Arm(task, occurrence, clock, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TaskReopened {TaskId} {OccurrenceId}", task.Id, command.OccurrenceId);
    }
}
