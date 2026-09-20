namespace MyTaskApp.Application.Planning;

/// <summary>
/// Traduz instantes para o calendário e o relógio do usuário (ADR-002). Tudo que
/// o domínio recebe como data/hora de parede passa por aqui.
/// </summary>
public interface IUserClock
{
    TimeZoneInfo TimeZone { get; }

    DateOnly Today { get; }

    TimeOnly CurrentTime { get; }

    DateOnly ToLocalDate(DateTimeOffset instant);

    /// <summary>
    /// A outra ponta do ADR-002: transforma a hora de parede do usuário no
    /// instante absoluto usado para disparar lembretes. Não há sobrecarga para
    /// hora opcional de propósito — ocorrência sem horário não tem instante, e
    /// quem chama precisa decidir o que fazer com isso.
    /// </summary>
    DateTimeOffset ToInstant(DateOnly date, TimeOnly time);
}
