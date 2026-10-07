using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.TimeTracking;

/// <summary>
/// ▶ na tarefa (ADR-052). <paramref name="ReplaceRunning"/> é a resposta do
/// "Parar e iniciar": sem ele, outro cronômetro correndo recusa o pedido.
/// </summary>
public sealed record StartTimer(Guid OccurrenceId, bool ReplaceRunning = false);

public sealed class StartTimerHandler(
    ITaskItemRepository tasks,
    ITimeEntryRepository timeEntries,
    IUnitOfWork unitOfWork,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<StartTimerHandler> logger)
{
    public async Task<ActiveTimerView> HandleAsync(StartTimer command, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);

        task.EnsureTimerCanStart(command.OccurrenceId);

        var now = timeProvider.GetUtcNow();

        // Relido do banco, e não do que a tela acha que está correndo (§27): a
        // tela pode estar um minuto atrasada, e o duplo clique chega aqui duas vezes.
        var running = await timeEntries.FindActiveAsync(cancellationToken);

        if (running is not null)
        {
            if (running.TaskOccurrenceId == command.OccurrenceId)
            {
                return new ActiveTimerView(running.Id, command.OccurrenceId, task.Id, task.Title, running.StartedAt);
            }

            if (!command.ReplaceRunning)
            {
                var other = await tasks.FindByOccurrenceIdAsync(running.TaskOccurrenceId, cancellationToken);

                throw new DomainException(
                    $"Você está trabalhando em \"{other?.Title ?? "outra tarefa"}\". Pare o cronômetro dela antes de iniciar outro.");
            }

            RunningTimer.Close(timeEntries, running, now);

            logger.LogInformation(
                "TimerStopped {EntryId} {OccurrenceId} {Reason}",
                running.Id,
                running.TaskOccurrenceId,
                "Replaced");
        }

        var siblings = await timeEntries.ListForOccurrenceAsync(command.OccurrenceId, cancellationToken);

        // Só pega um período lançado "no futuro" de um relógio que voltou — mas
        // é a mesma regra da edição, e não custa nada valer aqui também.
        TimeLog.EnsureNoOverlap(now, null, siblings, now, entry => TimeEntryText.DatedRange(entry, clock));

        var entry = TimeEntry.StartTimer(command.OccurrenceId, now);

        await timeEntries.AddAsync(entry, cancellationToken);

        // Um SaveChanges só: o fim do anterior e o início deste entram juntos.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TimerStarted {EntryId} {TaskId} {OccurrenceId}", entry.Id, task.Id, command.OccurrenceId);

        return new ActiveTimerView(entry.Id, command.OccurrenceId, task.Id, task.Title, entry.StartedAt);
    }
}

/// <summary>⏹ na tarefa. Sem cronômetro correndo nela, não há o que parar — e não é erro.</summary>
public sealed record StopTimer(Guid OccurrenceId);

public sealed class StopTimerHandler(
    ITimeEntryRepository timeEntries,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<StopTimerHandler> logger)
{
    public async Task HandleAsync(StopTimer command, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var stopped = await RunningTimer.StopIfOnAsync(timeEntries, [command.OccurrenceId], now, cancellationToken);

        if (stopped is null)
        {
            return;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "TimerStopped {EntryId} {OccurrenceId} {Reason}",
            stopped.Id,
            command.OccurrenceId,
            stopped.EndedAt is null ? "DiscardedClockWentBack" : "User");
    }
}

/// <summary>O cronômetro que está correndo, se houver: a confirmação de troca e a abertura do app.</summary>
public sealed class GetActiveTimerHandler(IActiveTimerQuery query)
{
    public Task<ActiveTimerView?> HandleAsync(CancellationToken cancellationToken = default) =>
        query.FindAsync(cancellationToken);
}
