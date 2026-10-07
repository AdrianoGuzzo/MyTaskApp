using MyTaskApp.Domain.Planning;

namespace MyTaskApp.Application.History;

/// <summary>O que o histórico lê do banco, cru: a montagem é do domínio.</summary>
public sealed record ActivityHistoryRows(
    IReadOnlyList<ActivityCompletion> Completions,
    IReadOnlyList<ActivityPeriod> Periods);

public interface IActivityHistoryQuery
{
    /// <summary>
    /// As conclusões em <c>[<paramref name="since"/>, <paramref name="until"/>)</c>
    /// e os períodos de trabalho que encostam nesse intervalo — inclusive o que
    /// começou antes e o que ainda corre. Só o intervalo, nunca o banco inteiro.
    /// </summary>
    Task<ActivityHistoryRows> GetAsync(
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken = default);
}
