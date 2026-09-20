using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Application.Reminders;

/// <summary>
/// O ajuste individual: esta tarefa foge do padrão. A política fica na série, e
/// não na ocorrência, então quando houver recorrência "por tarefa" já significa
/// "por série" sem migração de sentido.
/// </summary>
public sealed record SetTaskReminder(Guid TaskId, ReminderPolicy Policy);

public sealed class SetTaskReminderHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<SetTaskReminderHandler> logger)
{
    public async Task HandleAsync(
        SetTaskReminder command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        task.ChangeReminder(command.Policy);

        // Rearma o que ainda está pendente: mudar a política e continuar
        // avisando no horário antigo seria a pior das duas coisas. O que já foi
        // concluído ou cancelado fica como está.
        ReminderArming.ArmPending(task, clock, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "TaskReminderChanged {TaskId} {IsEnabled} {Offset}",
            task.Id,
            command.Policy.IsEnabled,
            command.Policy.Offset);
    }
}
