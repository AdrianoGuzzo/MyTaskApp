using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class DeadlineSettingsStore(
    MyTaskAppDbContext context,
    ILogger<DeadlineSettingsStore> logger) : IDeadlineSettingsStore
{
    public async Task<DeadlineSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var row = await context.DeadlineSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                settings => settings.Id == DeadlineSettingsRow.SingletonId,
                cancellationToken);

        if (row is null)
        {
            // Instalação nova, ou quem atualizou e nunca abriu a configuração:
            // o padrão de fábrica, sem semear linha (ADR-014).
            return DeadlineSettings.Factory;
        }

        try
        {
            var alerts = new DeadlineAlertPolicy(row.IsEnabled, row.Stages, row.OverdueRepeatEvery);

            return new DeadlineSettings(alerts, row.DefaultTime);
        }
        catch (DomainException exception)
        {
            logger.LogWarning(exception, "InvalidDeadlineSettingsStored");
            return DeadlineSettings.Factory;
        }
    }

    public async Task SaveAsync(
        DeadlineSettings settings,
        CancellationToken cancellationToken = default)
    {
        var row = await context.DeadlineSettings.FirstOrDefaultAsync(
            stored => stored.Id == DeadlineSettingsRow.SingletonId,
            cancellationToken);

        if (row is null)
        {
            row = new DeadlineSettingsRow();
            await context.DeadlineSettings.AddAsync(row, cancellationToken);
        }

        row.IsEnabled = settings.Alerts.IsEnabled;
        row.Stages = settings.Alerts.Stages;
        row.OverdueRepeatEvery = settings.Alerts.OverdueRepeatEvery;
        row.DefaultTime = settings.DefaultTime;
    }
}
