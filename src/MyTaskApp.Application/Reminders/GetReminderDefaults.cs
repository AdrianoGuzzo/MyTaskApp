namespace MyTaskApp.Application.Reminders;

/// <summary>A configuração padrão, para a tela de ajustes e para a criação.</summary>
public sealed record GetReminderDefaults;

public sealed class GetReminderDefaultsHandler(IReminderSettingsStore settings)
{
    public Task<ReminderSettings> HandleAsync(
        GetReminderDefaults command,
        CancellationToken cancellationToken = default) =>
        settings.GetAsync(cancellationToken);
}
