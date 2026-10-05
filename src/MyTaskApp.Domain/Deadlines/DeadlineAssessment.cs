namespace MyTaskApp.Domain.Deadlines;

/// <summary>Em que pé está o prazo. Calculado sempre, nunca gravado (§18).</summary>
public enum DeadlineStatus
{
    /// <summary>A tarefa não tem prazo.</summary>
    None = 0,

    /// <summary>Mais de 48 horas pela frente.</summary>
    OnTrack = 1,

    /// <summary>Até 48 horas, mas não hoje.</summary>
    DueSoon = 2,

    /// <summary>Vence hoje, no calendário.</summary>
    DueToday = 3,

    /// <summary>O prazo passou e a tarefa continua aberta.</summary>
    Overdue = 4,

    /// <summary>Concluída até o prazo.</summary>
    Met = 5,

    /// <summary>Concluída depois do prazo.</summary>
    Missed = 6,
}

/// <summary>
/// Quanto o prazo pede de atenção. Vem junto com texto na tela — a cor
/// reforça, não substitui (§5).
/// </summary>
public enum DeadlineSeverity
{
    Normal = 0,
    Attention = 1,
    Urgent = 2,
    Overdue = 3,
}

/// <param name="Remaining">
/// Do momento de referência até o prazo: agora, ou a conclusão para quem já
/// terminou. Negativo quando o prazo ficou para trás.
/// </param>
public sealed record DeadlineSnapshot(DeadlineStatus Status, DeadlineSeverity Severity, TimeSpan Remaining);

/// <summary>
/// A regra única do estado do prazo. Função pura: a borda converte o prazo em
/// instante (<c>IUserClock.ToInstant</c>, ADR-002) e passa os dois.
/// </summary>
public static class DeadlineAssessment
{
    /// <summary>Daqui para baixo, o prazo pede atenção.</summary>
    public static readonly TimeSpan AttentionWithin = TimeSpan.FromHours(48);

    /// <summary>Daqui para baixo — ou no próprio dia —, o prazo é urgente.</summary>
    public static readonly TimeSpan UrgentWithin = TimeSpan.FromHours(8);

    /// <param name="completedAtUtc">
    /// Quando a ocorrência foi concluída; <c>null</c> enquanto estiver aberta.
    /// </param>
    public static DeadlineSnapshot Assess(
        TaskDeadline deadline,
        DateTimeOffset deadlineAtUtc,
        DateTimeOffset nowUtc,
        DateOnly today,
        DateTimeOffset? completedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(deadline);

        if (completedAtUtc is { } completedAt)
        {
            var margin = deadlineAtUtc - completedAt;

            return new DeadlineSnapshot(
                margin >= TimeSpan.Zero ? DeadlineStatus.Met : DeadlineStatus.Missed,
                DeadlineSeverity.Normal,
                margin);
        }

        var remaining = deadlineAtUtc - nowUtc;

        if (remaining <= TimeSpan.Zero)
        {
            return new DeadlineSnapshot(DeadlineStatus.Overdue, DeadlineSeverity.Overdue, remaining);
        }

        if (deadline.Date <= today)
        {
            return new DeadlineSnapshot(DeadlineStatus.DueToday, DeadlineSeverity.Urgent, remaining);
        }

        if (remaining <= UrgentWithin)
        {
            // Amanhã às 02:00, visto às 20:00: são seis horas, e é isso que manda.
            return new DeadlineSnapshot(DeadlineStatus.DueSoon, DeadlineSeverity.Urgent, remaining);
        }

        return remaining <= AttentionWithin
            ? new DeadlineSnapshot(DeadlineStatus.DueSoon, DeadlineSeverity.Attention, remaining)
            : new DeadlineSnapshot(DeadlineStatus.OnTrack, DeadlineSeverity.Normal, remaining);
    }
}
