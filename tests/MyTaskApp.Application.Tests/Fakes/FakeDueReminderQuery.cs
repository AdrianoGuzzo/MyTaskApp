using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// Lê os lembretes vencidos do próprio repositório em memória, com o mesmo
/// filtro do SQL de verdade. Assim o despacho e o agregado nunca saem de sincronia
/// dentro de um teste.
/// </summary>
internal sealed class FakeDueReminderQuery(FakeTaskItemRepository repository) : IDueReminderQuery
{
    public Task<IReadOnlyList<DueReminderRow>> GetDueAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var rows = repository.Tasks
            .SelectMany(task => task.Occurrences, (task, occurrence) => (task, occurrence))
            .Where(pair =>
                pair.occurrence.Status is TaskItemStatus.Pending
                && pair.occurrence.Reminder.NextFireAtUtc is { } next
                && next <= nowUtc
                && !pair.occurrence.Reminder.IsAcknowledged)
            .OrderBy(pair => pair.occurrence.Reminder.NextFireAtUtc)
            .Take(limit)
            .Select(pair => new DueReminderRow(
                pair.occurrence.Id,
                pair.task.Id,
                pair.task.Title,
                pair.task.Priority,
                pair.occurrence.ScheduledDate,
                pair.occurrence.ScheduledTime,
                pair.occurrence.Reminder.NextFireAtUtc!.Value,
                pair.occurrence.Reminder.Attempt,
                pair.occurrence.Reminder.WaitingSinceUtc,
                pair.task.Reminder.Channels))
            .ToList();

        return Task.FromResult<IReadOnlyList<DueReminderRow>>(rows);
    }
}

/// <summary>
/// Conta os tiques sem precisar de contêiner. O agendador só conhece o runner,
/// então isto basta para exercitar o timer inteiro.
/// </summary>
internal sealed class CountingUseCaseRunner : IUseCaseRunner
{
    public int Invocations { get; private set; }

    public Exception? Failure { get; set; }

    /// <summary>Segura um tique aberto, para testar sobreposição.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<TResult> RunAsync<THandler, TResult>(
        Func<THandler, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        where THandler : notnull
    {
        Invocations++;

        if (Gate is not null)
        {
            await Gate.Task;
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return DispatchDueRemindersResult.Nothing is TResult result ? result : default!;
    }

    public async Task RunAsync<THandler>(
        Func<THandler, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
        where THandler : notnull =>
        await RunAsync<THandler, bool>((_, _) => Task.FromResult(true), cancellationToken);
}
