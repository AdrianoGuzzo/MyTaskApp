namespace MyTaskApp.Domain.Reminders;

/// <summary>
/// A escada de insistência: quanto mais tentativas sem atenção, mais evidente o
/// aviso. Função pura (ADR-010) — não lê relógio nem banco, então cada degrau
/// vira um teste direto.
/// </summary>
public static class ReminderEscalation
{
    /// <summary>Acima daqui a insistência não cresce mais.</summary>
    public const int TopStep = 5;

    /// <summary>Degrau em que o som entra.</summary>
    public const int SoundStep = 3;

    /// <summary>Degrau em que o aviso passa a ser evidente.</summary>
    public const int ProminentStep = 4;

    /// <summary>
    /// <paramref name="attempt"/> é 1-based: 1 é o primeiro aviso. Os canais
    /// desligados apenas pulam o degrau — a ordem nunca muda.
    /// </summary>
    public static AlertLevel LevelFor(int attempt, AlertChannels channels)
    {
        var step = Math.Clamp(attempt, 1, TopStep);
        var notifies = channels.HasFlag(AlertChannels.Notification);

        return new AlertLevel(
            step,
            Notify: notifies,

            // Som é degrau 3 em diante, e só se o canal estiver ligado: por mais
            // atrasado que fique, um lembrete mudo continua mudo.
            PlaySound: step >= SoundStep && channels.HasFlag(AlertChannels.Sound),

            // "Notificação mais evidente" é um sabor de notificação, então
            // depende do mesmo canal.
            Prominent: step >= ProminentStep && notifies,

            BringToFront: step >= TopStep && channels.HasFlag(AlertChannels.BringToFront));
    }
}
