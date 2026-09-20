namespace MyTaskApp.Domain.Reminders;

/// <summary>
/// Quando disparar. Função pura (ADR-010): recebe instantes já convertidos e
/// devolve instantes — a conversão de hora de parede para instante é da borda
/// (ADR-002), então nada aqui sabe o que é fuso ou horário de verão.
/// </summary>
public static class ReminderScheduling
{
    /// <summary>
    /// O primeiro disparo. <c>null</c> quando a política está desligada ou
    /// quando ela pede "antes do horário" e a ocorrência não tem horário —
    /// não há "antes de quê".
    /// </summary>
    public static DateTimeOffset? FirstFireAt(
        ReminderPolicy policy,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? scheduledInstantUtc) =>
        policy switch
        {
            { IsEnabled: false } => null,
            { Anchor: ReminderAnchor.AfterCreation } => createdAtUtc + policy.Offset,
            { Anchor: ReminderAnchor.BeforeScheduledTime } =>
                scheduledInstantUtc is { } scheduled ? scheduled - policy.Offset : null,
            _ => null,
        };

    /// <summary>
    /// O próximo disparo depois de um que já aconteceu — o primeiro da grade
    /// original que cai <b>estritamente depois</b> de <paramref name="nowUtc"/>.
    /// </summary>
    /// <remarks>
    /// Aqui mora a coalescência do ADR-004, e ela é aritmética, não um laço:
    /// avança intervalos inteiros de uma vez. Três dias fechado com "a cada 15
    /// min" devolve <b>um</b> instante a ≤ 15 min daqui, não 288 disparos
    /// acumulados. Manter a grade (e não somar o intervalo a "agora") faz um
    /// lembrete das 15:00 continuar caindo em 15:15 e 15:30 mesmo que o tique
    /// que o despachou tenha chegado atrasado.
    /// </remarks>
    public static DateTimeOffset? NextFireAfter(
        ReminderPolicy policy,
        DateTimeOffset lastScheduledFireUtc,
        DateTimeOffset nowUtc)
    {
        if (!policy.IsEnabled || !policy.RepeatUntilAcknowledged)
        {
            return null;
        }

        var interval = policy.RepeatEvery;
        var elapsed = nowUtc - lastScheduledFireUtc;

        // Piso + 1 garante "estritamente depois de agora" inclusive quando o
        // atraso é múltiplo exato do intervalo.
        var slots = elapsed <= TimeSpan.Zero
            ? 1L
            : (long)Math.Floor(elapsed / interval) + 1L;

        // Um instante vindo de um banco corrompido não pode estourar a data.
        var maxSlots = (DateTimeOffset.MaxValue - lastScheduledFireUtc).Ticks / interval.Ticks;

        return slots > maxSlots
            ? null
            : lastScheduledFireUtc + TimeSpan.FromTicks(interval.Ticks * slots);
    }
}
