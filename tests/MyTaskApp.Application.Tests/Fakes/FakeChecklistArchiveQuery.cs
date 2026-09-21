using MyTaskApp.Application.Lifecycle;

namespace MyTaskApp.Application.Tests.Fakes;

internal sealed class FakeChecklistArchiveQuery : IChecklistArchiveQuery
{
    public List<ChecklistSummaryRow> Rows { get; } = [];

    public ChecklistScope? ScopeAsked { get; private set; }

    public string? SearchAsked { get; private set; }

    public DateTimeOffset? SinceAsked { get; private set; }

    public Task<IReadOnlyList<ChecklistSummaryRow>> SearchAsync(
        ChecklistScope scope,
        string? search,
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default)
    {
        ScopeAsked = scope;
        SearchAsked = search;
        SinceAsked = since;

        return Task.FromResult<IReadOnlyList<ChecklistSummaryRow>>([.. Rows]);
    }
}
