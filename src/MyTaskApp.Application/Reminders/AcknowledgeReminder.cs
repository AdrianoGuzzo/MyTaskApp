using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Application.Reminders;

/// <summary>
/// O usuário finalmente deu atenção. É este caso de uso — e só ele — que encerra
/// um lembrete: ter mostrado a notificação não encerra nada.
/// </summary>
/// <remarks>
/// Idempotente: se o lembrete já não pede atenção — a ocorrência sumiu ou já foi
/// atendida por outro caminho —, só tira o aviso da tela. Recusar aqui deixava o
/// aviso preso, porque a janela não tem outro jeito de sair.
/// </remarks>
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
        var task = await tasks.FindByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);
        var occurrence = task?.GetOccurrence(command.OccurrenceId);

        if (occurrence is null || occurrence.Reminder.IsAcknowledged)
        {
            await presenter.DismissAsync(command.OccurrenceId, cancellationToken);
            logger.LogInformation("ReminderAlreadySettled {OccurrenceId}", command.OccurrenceId);
            return;
        }

        occurrence.AcknowledgeReminder(timeProvider.GetUtcNow(), command.By);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Só tira o aviso da tela depois de gravado: um alerta que some sem o
        // lembrete ter sido encerrado é exatamente o que não pode acontecer.
        await presenter.DismissAsync(command.OccurrenceId, cancellationToken);

        logger.LogInformation(
            "ReminderAcknowledged {TaskId} {OccurrenceId} {By}",
            occurrence.TaskItemId,
            command.OccurrenceId,
            command.By);
    }
}
