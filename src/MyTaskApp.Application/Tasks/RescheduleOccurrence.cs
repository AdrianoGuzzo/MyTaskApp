using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tasks;

public sealed record RescheduleOccurrence(
    Guid OccurrenceId,
    DateOnly? ScheduledDate,
    TimeOnly? ScheduledTime);

public sealed class RescheduleOccurrenceHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<RescheduleOccurrenceHandler> logger)
{
    public async Task HandleAsync(
        RescheduleOccurrence command,
        CancellationToken cancellationToken = default)
    {
        var schedule = new TaskSchedule(command.ScheduledDate, command.ScheduledTime);
        var task = await tasks.GetByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);

        var occurrence = task.RescheduleOccurrence(command.OccurrenceId, schedule);

        // Reagendar desarmou o instante velho; rearmar e o que mantem o
        // lembrete alinhado com a data nova.
        ReminderArming.Arm(task, occurrence, clock, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "TaskRescheduled {TaskId} {OccurrenceId} {ScheduledDate}",
            task.Id,
            command.OccurrenceId,
            command.ScheduledDate);
    }
}
