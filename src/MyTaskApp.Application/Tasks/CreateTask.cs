using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
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
    ReminderPolicy? Reminder = null);

public sealed record CreateTaskResult(Guid TaskId, Guid OccurrenceId);

public sealed class CreateTaskHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IReminderSettingsStore settings,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<CreateTaskHandler> logger)
{
    public async Task<CreateTaskResult> HandleAsync(
        CreateTask command,
        CancellationToken cancellationToken = default)
    {
        var createdAt = timeProvider.GetUtcNow();

        // Sem politica explicita vale o padrao do usuario: e o que faz
        // "crio e nao preciso mais olhar" funcionar sem configurar nada.
        var reminder = command.Reminder
            ?? (await settings.GetAsync(cancellationToken)).DefaultPolicy;

        var task = TaskItem.Create(
            command.Title,
            createdAt,
            command.Description,
            command.Priority,
            new TaskSchedule(command.ScheduledDate, command.ScheduledTime),
            reminder);

        var occurrence = task.Occurrences.Single();

        // Armar antes de salvar: o lembrete entra na mesma transacao da tarefa.
        ReminderArming.Arm(task, occurrence, clock, createdAt);

        await tasks.AddAsync(task, cancellationToken);
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
