using MyTaskApp.Application.History;
using MyTaskApp.Domain.Planning;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// O histórico em memória, com o mesmo filtro de intervalo do SQL (ADR-053). Guarda
/// o intervalo pedido: é ele que diz se o handler respeitou o fuso.
/// </summary>
internal sealed class FakeActivityHistoryQuery : IActivityHistoryQuery
{
    public List<ActivityCompletion> Completions { get; } = [];

    public List<ActivityPeriod> Periods { get; } = [];

    public (DateTimeOffset Since, DateTimeOffset Until)? Requested { get; private set; }

    public Task<ActivityHistoryRows> GetAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        Requested = (since, until);

        return Task.FromResult(new ActivityHistoryRows(
            Completions.Where(completion => completion.CompletedAt >= since && completion.CompletedAt < until).ToList(),
            Periods.Where(period => period.StartedAt < until && (period.EndedAt is null || period.EndedAt > since)).ToList()));
    }
}
