using MyTaskApp.Application.TimeTracking;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>Devolve os períodos semeados que tocam o intervalo; com <see cref="Gate"/>, a próxima leitura espera.</summary>
internal sealed class FakeTimeEntryReportQuery : ITimeEntryReportQuery
{
    public List<TimeEntryReportRow> Rows { get; } = [];

    public TimeEntryReportCriteria? LastCriteria { get; private set; }

    public TaskCompletionSource? Gate { get; set; }

    public async Task<IReadOnlyList<TimeEntryReportRow>> ListAsync(TimeEntryReportCriteria criteria, CancellationToken cancellationToken = default)
    {
        LastCriteria = criteria;

        if (Gate is { } gate)
        {
            Gate = null;
            await gate.Task;
        }

        return Rows
            .Where(row => criteria.Until is not { } until || row.StartedAt < until)
            .Where(row => criteria.Since is not { } since || row.EndedAt is null || row.EndedAt > since)
            .Where(row => criteria.TagId is null)
            .OrderByDescending(row => row.StartedAt)
            .ToList();
    }
}
