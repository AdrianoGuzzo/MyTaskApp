using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Planning;

/// <summary>
/// O que a classificação precisa saber de uma ocorrência. Recebe
/// <paramref name="CompletedOn"/> já como data local: converter instante em data
/// é responsabilidade da borda, não do domínio (ADR-002).
/// </summary>
public readonly record struct TodayCandidate(
    DateOnly? ScheduledDate,
    TimeOnly? ScheduledTime,
    TaskItemStatus Status,
    DateOnly? CompletedOn);
