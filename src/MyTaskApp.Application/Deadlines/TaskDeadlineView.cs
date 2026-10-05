using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Application.Deadlines;

/// <summary>
/// O prazo já avaliado e escrito, pronto para a linha, o HUD e o editor. A tela
/// só mostra: nenhuma conta de prazo acontece no Desktop (§3, §24 dos critérios).
/// </summary>
/// <param name="Remaining">Até o prazo; negativo quando passou (ver <see cref="DeadlineSnapshot"/>).</param>
/// <param name="Label">A linha da tarefa: "ATENÇÃO · vence amanhã às 18:00".</param>
/// <param name="DateLabel">O editor: "sex 09/10 18:00".</param>
/// <param name="Countdown">O editor, ao lado da data: "2 dias e 6 horas restantes".</param>
public sealed record TaskDeadlineView(
    TaskDeadline Deadline,
    DeadlineStatus Status,
    DeadlineSeverity Severity,
    TimeSpan Remaining,
    string Label,
    string DateLabel,
    string Countdown)
{
    /// <summary>
    /// Avalia o prazo agora. É aqui, e só aqui, que o prazo de parede vira
    /// instante (ADR-002) para a avaliação do domínio.
    /// </summary>
    /// <param name="completedAtUtc">A conclusão, para quem já terminou.</param>
    public static TaskDeadlineView Describe(
        TaskDeadline deadline,
        IUserClock clock,
        DateTimeOffset nowUtc,
        DateTimeOffset? completedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        ArgumentNullException.ThrowIfNull(clock);

        var today = clock.ToLocalDate(nowUtc);
        var snapshot = DeadlineAssessment.Assess(
            deadline,
            clock.ToInstant(deadline.Date, deadline.Time),
            nowUtc,
            today,
            completedAtUtc);

        return new TaskDeadlineView(
            deadline,
            snapshot.Status,
            snapshot.Severity,
            snapshot.Remaining,
            DeadlineFormatter.RowLabel(snapshot, deadline, today),
            DeadlineFormatter.Date(deadline),
            DeadlineFormatter.Countdown(snapshot));
    }
}
