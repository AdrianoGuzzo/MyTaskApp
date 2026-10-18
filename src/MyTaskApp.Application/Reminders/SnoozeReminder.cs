using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Reminders;

/// <summary>
/// "Adiar 30 min". Não encerra o lembrete — ele volta. Mas adiar é dar atenção,
/// então a insistência recomeça do primeiro degrau.
/// </summary>
public sealed record SnoozeReminder(Guid OccurrenceId, TimeSpan For);

public sealed class SnoozeReminderHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IAlertPresenter presenter,
    TimeProvider timeProvider,
    ILogger<SnoozeReminderHandler> logger)
{
    public static readonly TimeSpan MinSnooze = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxSnooze = TimeSpan.FromHours(24);

    public async Task HandleAsync(
        SnoozeReminder command,
        CancellationToken cancellationToken = default)
    {
        if (command.For < MinSnooze)
        {
            throw new DomainException("O adiamento precisa ser de pelo menos 1 minuto.");
        }

        if (command.For > MaxSnooze)
        {
            throw new DomainException("Não é possível adiar um lembrete por mais de 24 horas.");
        }

        var task = await tasks.FindByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);
        var occurrence = task?.GetOccurrence(command.OccurrenceId);

        // Sumiu, foi atendido ou concluído enquanto o aviso estava na tela: não
        // há o que adiar, e recusar deixaria o aviso preso (ver AcknowledgeReminder).
        if (occurrence is null
            || occurrence.Status is not TaskItemStatus.Pending
            || occurrence.Reminder.IsAcknowledged)
        {
            await presenter.DismissAsync(command.OccurrenceId, cancellationToken);
            logger.LogInformation("ReminderAlreadySettled {OccurrenceId}", command.OccurrenceId);
            return;
        }

        var until = timeProvider.GetUtcNow() + command.For;

        occurrence.SnoozeReminder(until);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await presenter.DismissAsync(command.OccurrenceId, cancellationToken);

        logger.LogInformation(
            "ReminderSnoozed {TaskId} {OccurrenceId} {Until}",
            occurrence.TaskItemId,
            command.OccurrenceId,
            until);
    }
}
