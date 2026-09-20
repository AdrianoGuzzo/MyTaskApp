using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class ReminderSettingsStore(
    MyTaskAppDbContext context,
    ILogger<ReminderSettingsStore> logger) : IReminderSettingsStore
{
    public async Task<ReminderSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var row = await context.ReminderSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                settings => settings.Id == ReminderSettingsRow.SingletonId,
                cancellationToken);

        if (row is null)
        {
            // Instalação nova: o padrão de fábrica sem semear linha nenhuma.
            // Semear um singleton mutável por migration vira armadilha no dia em
            // que o padrão mudar.
            return ReminderSettings.Factory;
        }

        try
        {
            var policy = new ReminderPolicy(
                row.IsEnabled,
                row.Anchor,
                row.Offset,
                row.RepeatUntilAcknowledged,
                row.RepeatEvery,
                row.Channels);

            return new ReminderSettings(policy, row.PausedUntilUtc);
        }
        catch (DomainException exception)
        {
            // Mesma degradação que o UserClock aplica a um fuso inválido: abrir
            // com o padrão de fábrica é melhor do que não abrir.
            logger.LogWarning(exception, "InvalidReminderDefaultsStored");
            return ReminderSettings.Factory;
        }
    }

    public async Task SaveAsync(
        ReminderSettings settings,
        CancellationToken cancellationToken = default)
    {
        var row = await context.ReminderSettings.FirstOrDefaultAsync(
            stored => stored.Id == ReminderSettingsRow.SingletonId,
            cancellationToken);

        if (row is null)
        {
            row = new ReminderSettingsRow();
            await context.ReminderSettings.AddAsync(row, cancellationToken);
        }

        var policy = settings.DefaultPolicy;

        row.IsEnabled = policy.IsEnabled;
        row.Anchor = policy.Anchor;
        row.Offset = policy.Offset;
        row.RepeatUntilAcknowledged = policy.RepeatUntilAcknowledged;
        row.RepeatEvery = policy.RepeatEvery;
        row.Channels = policy.Channels;
        row.PausedUntilUtc = settings.PausedUntilUtc;
    }
}
