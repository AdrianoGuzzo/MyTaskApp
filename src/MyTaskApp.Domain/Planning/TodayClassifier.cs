using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Domain.Planning;

/// <summary>
/// Decide em que seção da tela "Hoje" cada ocorrência cai. Função pura: não lê
/// relógio nem banco, então cada regra do §9 vira um teste direto.
/// </summary>
public static class TodayClassifier
{
    /// <summary>Retorna <c>null</c> quando a ocorrência não pertence ao dia.</summary>
    public static TodayPlacement? Classify(
        TodayCandidate candidate,
        DateOnly today,
        TimeOnly now,
        NowWindow window)
    {
        if (candidate.Status is TaskItemStatus.Cancelled)
        {
            return null;
        }

        if (candidate.Status is TaskItemStatus.Completed)
        {
            // Vale o dia em que foi concluída, não o dia para o qual foi marcada:
            // terminar hoje algo de ontem conta como feito hoje.
            return candidate.CompletedOn == today
                ? new TodayPlacement(TodaySection.Completed, IsLate: false)
                : null;
        }

        if (candidate.Deadline is { } deadline)
        {
            // Com prazo, quem diz que está atrasada é o prazo, e não a data em
            // que foi capturada: o que entra segunda para entregar sexta não
            // vira ATRASADA na terça (ADR-050).
            if (deadline.HasPassed(today, now))
            {
                return new TodayPlacement(TodaySection.Overdue, IsLate: false);
            }

            // Marcada para uma hora de hoje: é o plano do dia, e o prazo vai
            // junto como rótulo. O resto corre em PRAZOS, uma seção só.
            if (candidate.ScheduledDate != today || candidate.ScheduledTime is null)
            {
                return new TodayPlacement(TodaySection.Deadlines, IsLate: false);
            }
        }

        if (candidate.ScheduledDate is not { } scheduledDate || scheduledDate > today)
        {
            return null;
        }

        if (scheduledDate < today)
        {
            return new TodayPlacement(TodaySection.Overdue, IsLate: false);
        }

        if (candidate.ScheduledTime is not { } scheduledTime)
        {
            return new TodayPlacement(TodaySection.Unscheduled, IsLate: false);
        }

        var (start, end) = window.BoundsAt(now);

        return scheduledTime >= start && scheduledTime <= end
            ? new TodayPlacement(TodaySection.Now, IsLate: false)
            : new TodayPlacement(TodaySection.Today, IsLate: scheduledTime < now);
    }
}
