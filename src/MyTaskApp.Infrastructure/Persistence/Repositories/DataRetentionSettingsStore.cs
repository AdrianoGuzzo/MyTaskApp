using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class DataRetentionSettingsStore(
    MyTaskAppDbContext context,
    ILogger<DataRetentionSettingsStore> logger) : IDataRetentionSettingsStore
{
    public async Task<DataRetentionPolicy> GetAsync(CancellationToken cancellationToken = default)
    {
        var row = await context.DataRetentionSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                settings => settings.Id == DataRetentionSettingsRow.SingletonId,
                cancellationToken);

        if (row is null)
        {
            // Instalação nova: padrão de fábrica sem semear linha nenhuma
            // (ADR-014). Aqui isso tem uma consequência deliberada — o
            // arquivamento automático nasce desligado, então atualizar o app
            // nunca varre o histórico de ninguém sem que tenham pedido.
            return DataRetentionPolicy.Factory;
        }

        try
        {
            return new DataRetentionPolicy(
                row.AutoArchiveEnabled,
                row.AutoArchiveAfterDays,
                row.TrashRetentionDays);
        }
        catch (DomainException exception)
        {
            // Degrada, não derruba — e aqui degradar é a opção segura: o padrão
            // de fábrica tem o arquivamento automático desligado, então uma
            // linha corrompida nunca vira uma varredura inesperada.
            logger.LogWarning(exception, "InvalidDataRetentionSettingsStored");
            return DataRetentionPolicy.Factory;
        }
    }

    public async Task SaveAsync(
        DataRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var row = await context.DataRetentionSettings.FirstOrDefaultAsync(
            stored => stored.Id == DataRetentionSettingsRow.SingletonId,
            cancellationToken);

        if (row is null)
        {
            row = new DataRetentionSettingsRow();
            await context.DataRetentionSettings.AddAsync(row, cancellationToken);
        }

        row.AutoArchiveEnabled = policy.AutoArchiveEnabled;
        row.AutoArchiveAfterDays = policy.AutoArchiveAfterDays;
        row.TrashRetentionDays = policy.TrashRetentionDays;
    }
}
