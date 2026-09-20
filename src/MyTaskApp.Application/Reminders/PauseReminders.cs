using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Reminders;

/// <summary>Silencia tudo por um tempo — a saída de emergência da bandeja.</summary>
public sealed record PauseReminders(TimeSpan For);

public sealed class PauseRemindersHandler(
    IReminderSettingsStore settings,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<PauseRemindersHandler> logger)
{
    public static readonly TimeSpan MaxPause = TimeSpan.FromDays(7);

    public async Task HandleAsync(
        PauseReminders command,
        CancellationToken cancellationToken = default)
    {
        if (command.For <= TimeSpan.Zero)
        {
            throw new DomainException("Diga por quanto tempo pausar os lembretes.");
        }

        if (command.For > MaxPause)
        {
            throw new DomainException("Não é possível pausar os lembretes por mais de 7 dias.");
        }

        var until = timeProvider.GetUtcNow() + command.For;
        var current = await settings.GetAsync(cancellationToken);

        await settings.SaveAsync(current with { PausedUntilUtc = until }, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("RemindersPaused {Until}", until);
    }
}
