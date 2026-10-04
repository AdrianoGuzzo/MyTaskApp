using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// A configuração de prazos, em linha única (ADR-050). POCO pela mesma razão do
/// <see cref="ReminderSettingsRow"/>: a política valida no construtor, e uma
/// linha corrompida precisa poder ser lida antes de ser recusada.
/// </summary>
internal sealed class DeadlineSettingsRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public bool IsEnabled { get; set; }

    public DeadlineAlertStage Stages { get; set; }

    public TimeSpan? OverdueRepeatEvery { get; set; }

    public TimeOnly DefaultTime { get; set; }
}
