using MyTaskApp.Application.Mcp;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Application.TimeTracking;

namespace MyTaskApp.Application.Tests.Fakes;

internal sealed class FakeMcpServerSettingsStore : IMcpServerSettingsStore
{
    public McpServerSettings Stored { get; private set; } = McpServerSettings.Factory;

    public Task<McpServerSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Stored);

    public Task SaveAsync(McpServerSettings settings, CancellationToken cancellationToken = default)
    {
        Stored = settings;
        return Task.CompletedTask;
    }
}

internal sealed class FakeMcpAccessTokenStore : IMcpAccessTokenStore
{
    public string? Token { get; set; }

    public int Writes { get; private set; }

    public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Token);

    public Task WriteAsync(string token, CancellationToken cancellationToken = default)
    {
        Token = token;
        Writes++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeTaskSearchQuery : ITaskSearchQuery
{
    public List<TaskSearchRow> Rows { get; } = [];

    public List<TaskCountRow> Counts { get; } = [];

    public TaskSearchCriteria? LastCriteria { get; private set; }

    public Task<IReadOnlyList<TaskSearchRow>> SearchAsync(TaskSearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        LastCriteria = criteria;
        return Task.FromResult<IReadOnlyList<TaskSearchRow>>(Rows.ToList());
    }

    public Task<IReadOnlyList<TaskCountRow>> ListForCountingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TaskCountRow>>(Counts.ToList());
}

/// <summary>Devolve os períodos semeados que tocam o intervalo pedido, como a consulta real.</summary>
internal sealed class FakeTimeEntryReportQuery : ITimeEntryReportQuery
{
    public List<TimeEntryReportRow> Rows { get; } = [];

    public TimeEntryReportCriteria? LastCriteria { get; private set; }

    public Task<IReadOnlyList<TimeEntryReportRow>> ListAsync(TimeEntryReportCriteria criteria, CancellationToken cancellationToken = default)
    {
        LastCriteria = criteria;

        return Task.FromResult<IReadOnlyList<TimeEntryReportRow>>(Rows
            .Where(row => criteria.Until is not { } until || row.StartedAt < until)
            .Where(row => criteria.Since is not { } since || row.EndedAt is null || row.EndedAt > since)
            .Where(row => criteria.EntryId is not { } id || row.EntryId == id)
            .Where(row => criteria.Running is not { } running || (row.EndedAt is null) == running)
            .OrderByDescending(row => row.StartedAt)
            .ToList());
    }
}
