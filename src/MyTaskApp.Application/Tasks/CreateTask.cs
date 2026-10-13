using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tasks;

/// <summary>
/// Data e hora chegam separadas porque é assim que a UI as coleta; a validação
/// de "hora sem data" pertence ao domínio (<see cref="TaskSchedule"/>).
/// </summary>
public sealed record CreateTask(
    string Title,
    string? Description = null,
    TaskPriority Priority = TaskPriority.Normal,
    DateOnly? ScheduledDate = null,
    TimeOnly? ScheduledTime = null,
    ReminderPolicy? Reminder = null,
    DevicePlacement? Placement = null);

public sealed record CreateTaskResult(Guid TaskId, Guid OccurrenceId);

public sealed class CreateTaskHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IReminderSettingsStore settings,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<CreateTaskHandler> logger,
    DevicePlacementState? placements = null)
{
    public async Task<CreateTaskResult> HandleAsync(
        CreateTask command,
        CancellationToken cancellationToken = default)
    {
        var createdAt = timeProvider.GetUtcNow();
        var placement = placements.ForNew(command.Placement);

        var task = await TaskCreation.AddAsync(
            tasks,
            settings,
            audit,
            currentUser,
            clock,
            createdAt,
            command.Reminder,
            reminder => TaskItem.Create(
                command.Title,
                createdAt,
                command.Description,
                command.Priority,
                new TaskSchedule(command.ScheduledDate, command.ScheduledTime),
                reminder),
            placement,
            cancellationToken);

        var occurrence = task.Occurrences.Single();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "TaskCreated {TaskId} {OccurrenceId} {Scheduled} {RemindAt}",
            task.Id,
            occurrence.Id,
            occurrence.ScheduledDate,
            occurrence.Reminder.NextFireAtUtc);

        return new CreateTaskResult(task.Id, occurrence.Id);
    }
}
