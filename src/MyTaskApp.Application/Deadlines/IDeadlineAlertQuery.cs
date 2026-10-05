using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Application.Deadlines;

/// <summary>
/// Uma ocorrência aberta com prazo, e o que já foi avisado sobre ela. Leitura
/// não passa pelo agregado (ADR-005); só quem vai mesmo ser avisado é carregado.
/// </summary>
/// <param name="TaskOverride">Os avisos escolhidos na tarefa; <c>null</c> = padrão global.</param>
public sealed record DeadlineCandidateRow(
    Guid OccurrenceId,
    Guid TaskId,
    string Title,
    TaskDeadline Deadline,
    DeadlineAlertStage? TaskOverride,
    DeadlineAlertState Alert);

public interface IDeadlineAlertQuery
{
    /// <summary>
    /// Toda ocorrência pendente com prazo, de checklists na lista principal. Não
    /// filtra por "venceu": o próximo aviso não é coluna, é cálculo (ADR-050), e
    /// tarefas com prazo são poucas — o índice parcial do prazo cobre a busca.
    /// </summary>
    Task<IReadOnlyList<DeadlineCandidateRow>> GetCandidatesAsync(CancellationToken cancellationToken = default);
}
