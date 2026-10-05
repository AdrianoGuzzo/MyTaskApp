using MyTaskApp.Application.Deadlines;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// Lê as candidatas a aviso de prazo do próprio repositório em memória, com o
/// mesmo filtro do SQL de verdade.
/// </summary>
internal sealed class FakeDeadlineAlertQuery(FakeTaskItemRepository repository) : IDeadlineAlertQuery
{
    public Task<IReadOnlyList<DeadlineCandidateRow>> GetCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var rows = repository.Tasks
            .Where(task => !task.IsOutOfTheMainList)
            .SelectMany(task => task.Occurrences, (task, occurrence) => (task, occurrence))
            .Where(pair => pair.occurrence.Status is TaskItemStatus.Pending && pair.occurrence.Deadline is not null)
            .OrderBy(pair => pair.occurrence.DeadlineDate)
            .ThenBy(pair => pair.occurrence.DeadlineTime)
            .Select(pair => new DeadlineCandidateRow(
                pair.occurrence.Id,
                pair.task.Id,
                pair.task.Title,
                pair.occurrence.Deadline!,
                pair.task.DeadlineAlerts,
                pair.occurrence.DeadlineAlert))
            .ToList();

        return Task.FromResult<IReadOnlyList<DeadlineCandidateRow>>(rows);
    }
}

internal sealed class FakeDeadlineSettingsStore(DeadlineSettings? initial = null) : IDeadlineSettingsStore
{
    public DeadlineSettings Settings { get; set; } = initial ?? DeadlineSettings.Factory;

    public Task<DeadlineSettings> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Settings);

    public Task SaveAsync(DeadlineSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings;
        return Task.CompletedTask;
    }

    public void Alerts(bool isEnabled, DeadlineAlertStage stages, TimeSpan? overdueRepeatEvery = null) =>
        Settings = Settings with { Alerts = new DeadlineAlertPolicy(isEnabled, stages, overdueRepeatEvery) };
}

/// <summary>Anota os avisos de prazo pedidos, como o <see cref="RecordingAlertPresenter"/>.</summary>
internal sealed class RecordingDeadlineAlertPresenter : IDeadlineAlertPresenter
{
    public List<DeadlineAlert> Presented { get; } = [];

    public List<DeadlineDigest> Digests { get; } = [];

    public List<Guid> Dismissed { get; } = [];

    /// <summary>Roda dentro do <c>PresentAsync</c>, para inspecionar o estado no momento do aviso.</summary>
    public Action<DeadlineAlert>? OnPresent { get; set; }

    public bool Fail { get; set; }

    public Task PresentAsync(DeadlineAlert alert, CancellationToken cancellationToken = default)
    {
        OnPresent?.Invoke(alert);

        if (Fail)
        {
            return Task.FromException(new InvalidOperationException("A janela não abriu."));
        }

        Presented.Add(alert);
        return Task.CompletedTask;
    }

    public Task PresentDigestAsync(DeadlineDigest digest, CancellationToken cancellationToken = default)
    {
        Digests.Add(digest);
        return Task.CompletedTask;
    }

    public Task DismissAsync(Guid occurrenceId, CancellationToken cancellationToken = default)
    {
        Dismissed.Add(occurrenceId);
        return Task.CompletedTask;
    }
}
