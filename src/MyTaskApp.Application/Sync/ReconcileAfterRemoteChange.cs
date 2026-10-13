using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Sync;

/// <summary>As tarefas que mudaram por chegada de outro computador (ADR-061).</summary>
/// <param name="TaskIds">Todas as que receberam alguma mudança (tarefa ou ocorrência).</param>
/// <param name="RearmTaskIds">
/// As que mudaram a política de lembrete ou o agendamento: os lembretes
/// pendentes delas são rearmados a partir de agora.
/// </param>
public sealed record ReconcileAfterRemoteChange(IReadOnlyCollection<Guid> TaskIds, IReadOnlyCollection<Guid> RearmTaskIds);

/// <summary>
/// Recalcula o que é deste computador depois de um estado que veio de fora: a
/// conclusão derivada e os lembretes. Só mexe em campo local, então não gera
/// operação de volta.
/// </summary>
/// <remarks>
/// Não rearma tudo a cada sincronização: um lembrete "1 hora depois de criar"
/// rearmado a partir de agora andaria para frente a cada pull. Rearma a
/// ocorrência que nunca teve lembrete aqui (chegou agora) e as das tarefas cuja
/// política ou agendamento mudou.
/// </remarks>
public sealed class ReconcileAfterRemoteChangeHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IUserClock clock,
    TimeProvider timeProvider)
{
    public async Task HandleAsync(ReconcileAfterRemoteChange command, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var rearm = command.RearmTaskIds.ToHashSet();

        foreach (var taskId in command.TaskIds.Distinct())
        {
            // Excluída pelo próprio pull: não há o que recalcular.
            if (await tasks.FindByIdAsync(taskId, cancellationToken) is not { } task)
            {
                continue;
            }

            task.ReconcileDerivedState();

            if (task.IsOutOfTheMainList)
            {
                continue;
            }

            foreach (var occurrence in task.Occurrences.Where(occurrence => occurrence.Status is TaskItemStatus.Pending))
            {
                var neverArmedHere = occurrence.Reminder is
                {
                    IsArmed: false,
                    IsAcknowledged: false,
                    LastFiredAtUtc: null,
                    WaitingSinceUtc: null,
                };

                if (neverArmedHere || rearm.Contains(taskId))
                {
                    ReminderArming.Arm(task, occurrence, clock, now);
                }
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
