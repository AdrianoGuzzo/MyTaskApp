using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.Abstractions;

/// <summary>Os períodos de trabalho (ADR-052). Estreita por necessidade (ADR-005).</summary>
public interface ITimeEntryRepository
{
    Task AddAsync(TimeEntry entry, CancellationToken cancellationToken = default);

    Task<TimeEntry?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// O período aberto do app inteiro, se houver. Há no máximo um: o caso de uso
    /// recusa o segundo, e o índice <c>IX_TimeEntries_SingleActive</c> segura o
    /// que escapar.
    /// </summary>
    Task<TimeEntry?> FindActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Todos os períodos da ocorrência, do mais antigo ao mais novo.</summary>
    Task<IReadOnlyList<TimeEntry>> ListForOccurrenceAsync(
        Guid occurrenceId,
        CancellationToken cancellationToken = default);

    void Remove(TimeEntry entry);
}
