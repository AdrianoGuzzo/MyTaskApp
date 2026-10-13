using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Domain.Devices;
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
/// <param name="Moves">As que outro computador mudou de lugar (ADR-062).</param>
/// <param name="SharedTagIds">Etiquetas juntadas pelo nome na primeira conexão: passam a ser de todos.</param>
public sealed record ReconcileAfterRemoteChange(
    IReadOnlyCollection<Guid> TaskIds,
    IReadOnlyCollection<Guid> RearmTaskIds,
    IReadOnlyCollection<PlacementMove>? Moves = null,
    IReadOnlyCollection<Guid>? SharedTagIds = null);

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
    TimeProvider timeProvider,
    DevicePlacementState? placements = null,
    ITagRepository? tags = null)
{
    public async Task HandleAsync(ReconcileAfterRemoteChange command, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var rearm = command.RearmTaskIds.ToHashSet();
        var arrivals = (command.Moves ?? [])
            .Where(IsArrival)
            .GroupBy(move => move.TaskId)
            .ToDictionary(group => group.Key, group => group.Last());

        foreach (var taskId in command.TaskIds.Distinct())
        {
            // Excluída pelo próprio pull: não há o que recalcular.
            if (await tasks.FindByIdAsync(taskId, cancellationToken) is not { } task)
            {
                continue;
            }

            task.ReconcileDerivedState();

            if (arrivals.TryGetValue(taskId, out var arrival))
            {
                task.MarkArrived(arrival.ByDeviceId, now);
            }

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

        // A etiqueta que existia aqui e lá, juntada pelo nome, marca tarefas dos
        // dois computadores: aparece em todos. Pelo domínio, para subir (ADR-062).
        if (tags is not null)
        {
            foreach (var tagId in command.SharedTagIds ?? [])
            {
                if (await tags.FindByIdAsync(tagId, cancellationToken) is { } tag && tag.Placement?.IsEverywhere != true)
                {
                    tag.Place(DevicePlacement.Everywhere);
                }
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Chegou (ADR-062) quando outro computador a mandou para este, ou a
    /// compartilhou estando escondida aqui. Uma tarefa nova "em todos" não é
    /// chegada: ninguém a mandou para cá em particular.
    /// </summary>
    private bool IsArrival(PlacementMove move)
    {
        if (placements is not { IsActive: true } || placements.IsHere(move.ByDeviceId) || move.After is not { } after)
        {
            return false;
        }

        if (after.DeviceId is { } target)
        {
            return placements.IsHere(target);
        }

        return !move.IsNew && !placements.IsVisibleHere(move.Before);
    }
}
