namespace MyTaskApp.Domain.Deadlines;

/// <summary>
/// Até quando a tarefa precisa estar pronta, em hora de parede (ADR-002): o
/// que o usuário lê no relógio dele. É independente do agendamento — "quando
/// pretendo fazer" e "quando precisa estar feito" são perguntas diferentes, e
/// uma tarefa pode ter uma, a outra, as duas ou nenhuma (ADR-050).
/// </summary>
/// <remarks>
/// A hora é obrigatória, ao contrário do agendamento: um prazo sem hora não
/// sabe quando passa a estar atrasado. Quem escolhe só o dia recebe o horário
/// padrão da configuração.
/// </remarks>
public sealed record TaskDeadline(DateOnly Date, TimeOnly Time)
{
    /// <summary>
    /// O prazo já passou no relógio de parede. No minuto exato do prazo ele já
    /// conta como vencido: "até 18:00" às 18:00 não tem mais tempo nenhum.
    /// </summary>
    public bool HasPassed(DateOnly today, TimeOnly now) =>
        Date < today || (Date == today && Time <= now);
}
