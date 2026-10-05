namespace MyTaskApp.Domain.Deadlines;

/// <summary>
/// Decide se o prazo merece um aviso agora. Função pura no estilo do
/// <c>TodayClassifier</c>: não lê relógio nem banco, e cada regra do §4 vira um
/// teste direto.
/// </summary>
/// <remarks>
/// Um aviso por degrau, nunca um por tique. Quem ficou com o app fechado
/// durante dois degraus recebe <b>um</b> aviso, o do mais grave — a mesma
/// coalescência do ADR-004, aqui por construção: só o degrau atual é comparado
/// com o último avisado.
/// </remarks>
public static class DeadlineAlerting
{
    /// <summary>O degrau ligado mais grave que já acendeu. <c>None</c> = ainda nenhum.</summary>
    public static DeadlineAlertStage CurrentStage(
        DeadlineAlertStage enabled,
        DateTimeOffset deadlineAtUtc,
        DateTimeOffset nowUtc)
    {
        for (var index = DeadlineAlertStages.Ordered.Count - 1; index >= 0; index--)
        {
            var stage = DeadlineAlertStages.Ordered[index];

            if (enabled.HasFlag(stage) && nowUtc >= deadlineAtUtc - DeadlineAlertStages.LeadTimeOf(stage))
            {
                return stage;
            }
        }

        return DeadlineAlertStage.None;
    }

    /// <summary>
    /// O degrau a avisar agora, ou <c>null</c>. Em ordem:
    /// <list type="number">
    /// <item>adiado e o adiamento não venceu: nada;</item>
    /// <item>nenhum degrau ligado acendeu: nada;</item>
    /// <item>o adiamento venceu: avisa o degrau atual de novo;</item>
    /// <item>o degrau atual é mais grave que o último avisado: avisa;</item>
    /// <item>atrasada, com repetição, e o intervalo passou desde o último: avisa de novo.</item>
    /// </list>
    /// </summary>
    public static DeadlineAlertStage? Decide(
        DeadlineAlertStage enabled,
        TimeSpan? overdueRepeatEvery,
        DateTimeOffset deadlineAtUtc,
        DeadlineAlertState state,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.SnoozedUntilUtc is { } until && nowUtc < until)
        {
            return null;
        }

        var current = CurrentStage(enabled, deadlineAtUtc, nowUtc);

        if (current is DeadlineAlertStage.None)
        {
            return null;
        }

        if (state.SnoozedUntilUtc is not null || current > state.LastStage)
        {
            return current;
        }

        // Um intervalo só, mesmo depois de dias fechado: o próximo conta deste.
        var repeatIsDue = current is DeadlineAlertStage.Overdue
            && overdueRepeatEvery is { } every
            && state.LastAlertAtUtc is { } last
            && nowUtc >= last + every;

        return repeatIsDue ? current : null;
    }
}
