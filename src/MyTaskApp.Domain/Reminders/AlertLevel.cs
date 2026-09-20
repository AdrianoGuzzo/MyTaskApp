namespace MyTaskApp.Domain.Reminders;

/// <summary>
/// O quanto insistir nesta tentativa. Resultado de <see cref="ReminderEscalation"/>;
/// a borda só obedece.
/// </summary>
public sealed record AlertLevel(
    int Step,
    bool Notify,
    bool PlaySound,
    bool Prominent,
    bool BringToFront)
{
    /// <summary>Nada a fazer — todos os canais do degrau estão desligados.</summary>
    public bool IsSilent => !Notify && !PlaySound && !BringToFront;
}
