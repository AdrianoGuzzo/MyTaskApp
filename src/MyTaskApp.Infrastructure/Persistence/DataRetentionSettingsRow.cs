namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// A configuração de gerenciamento de dados, em linha única. POCO pela mesma
/// razão do <see cref="ReminderSettingsRow"/>: o
/// <c>DataRetentionPolicy</c> valida no construtor, e uma linha corrompida
/// precisa poder ser lida antes de ser recusada.
/// </summary>
internal sealed class DataRetentionSettingsRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public bool AutoArchiveEnabled { get; set; }

    public int AutoArchiveAfterDays { get; set; }

    public int TrashRetentionDays { get; set; }
}
