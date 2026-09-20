using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// A configuração padrão do usuário, em linha única. POCO de propósito: o
/// <see cref="ReminderPolicy"/> valida no construtor, e uma linha corrompida
/// precisa poder ser lida antes de ser recusada.
/// </summary>
internal sealed class ReminderSettingsRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public bool IsEnabled { get; set; }

    public ReminderAnchor Anchor { get; set; }

    public TimeSpan Offset { get; set; }

    public bool RepeatUntilAcknowledged { get; set; }

    public TimeSpan RepeatEvery { get; set; }

    public AlertChannels Channels { get; set; }

    public DateTimeOffset? PausedUntilUtc { get; set; }
}
