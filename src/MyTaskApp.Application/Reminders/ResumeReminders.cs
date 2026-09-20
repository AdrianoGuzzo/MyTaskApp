using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Reminders;

public sealed record ResumeReminders;

public sealed class ResumeRemindersHandler(
    IReminderSettingsStore settings,
    IUnitOfWork unitOfWork,
    ILogger<ResumeRemindersHandler> logger)
{
    public async Task HandleAsync(
        ResumeReminders command,
        CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);

        // O que venceu durante a pausa não se perdeu: continua vencido e
        // coalesce no primeiro tique. É por isso que pausar é seguro.
        await settings.SaveAsync(current with { PausedUntilUtc = null }, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("RemindersResumed");
    }
}
