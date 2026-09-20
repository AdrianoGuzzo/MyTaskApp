using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Application.Reminders;

/// <summary>
/// A configuração padrão do usuário. <paramref name="PausedUntilUtc"/> mora aqui
/// de propósito: "pausar lembretes por 1 hora" precisa sobreviver a um reinício,
/// que é justamente quando o usuário mais corre risco de ser incomodado.
/// </summary>
public sealed record ReminderSettings(
    ReminderPolicy DefaultPolicy,
    DateTimeOffset? PausedUntilUtc)
{
    /// <summary>O que uma instalação nova recebe, sem semear linha nenhuma.</summary>
    public static ReminderSettings Factory { get; } = new(ReminderPolicy.Default, null);

    public bool IsPausedAt(DateTimeOffset instant) => PausedUntilUtc > instant;
}

public interface IReminderSettingsStore
{
    Task<ReminderSettings> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ReminderSettings settings, CancellationToken cancellationToken = default);
}
