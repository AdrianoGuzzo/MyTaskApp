namespace MyTaskApp.Application.TimeTracking;

/// <summary>
/// O cronômetro que está correndo (ADR-052), com o que a linha, a faixa do HUD e
/// a confirmação de troca precisam dizer. O tempo corrido não está aqui de
/// propósito: é <c>agora − StartedAt</c>, calculado por quem desenha.
/// </summary>
public sealed record ActiveTimerView(
    Guid EntryId,
    Guid OccurrenceId,
    Guid TaskId,
    string TaskTitle,
    DateTimeOffset StartedAt);

/// <summary>
/// Acha o cronômetro ativo já com o título da tarefa, numa consulta só. É o que o
/// quadro lê a cada recarga e o que a abertura do app usa para restaurar o
/// estado: o banco é a verdade, não a memória do processo.
/// </summary>
public interface IActiveTimerQuery
{
    Task<ActiveTimerView?> FindAsync(CancellationToken cancellationToken = default);
}
