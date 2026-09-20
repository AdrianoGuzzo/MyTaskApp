using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Application.Reminders;

/// <summary>
/// O usuário finalmente deu atenção. É este caso de uso — e só ele — que encerra
/// um lembrete: ter mostrado a notificação não encerra nada.
/// </summary>
public sealed record AcknowledgeReminder(Guid OccurrenceId, ReminderAcknowledgement By);

public sealed class AcknowledgeReminderHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IAlertPresenter presenter,
    TimeProvider timeProvider,
    ILogger<AcknowledgeReminderHandler> logger)
{
    public async Task HandleAsync(
        AcknowledgeReminder command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);

        task.GetOccurrence(command.OccurrenceId)
            .AcknowledgeReminder(timeProvider.GetUtcNow(), command.By);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Só tira o aviso da tela depois de gravado: um alerta que some sem o
        // lembrete ter sido encerrado é exatamente o que não pode acontecer.
        await presenter.DismissAsync(command.OccurrenceId, cancellationToken);

        logger.LogInformation(
            "ReminderAcknowledged {TaskId} {OccurrenceId} {By}",
            task.Id,
            command.OccurrenceId,
            command.By);
    }
}
