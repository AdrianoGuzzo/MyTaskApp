using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Mcp;
using MyTaskApp.Domain;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class McpServerSettingsStore(
    MyTaskAppDbContext context,
    ILogger<McpServerSettingsStore> logger) : IMcpServerSettingsStore
{
    public async Task<McpServerSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var row = await context.McpServerSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                settings => settings.Id == McpServerSettingsRow.SingletonId,
                cancellationToken);

        if (row is null)
        {
            // Instalação nova, ou anterior ao MCP: desligado, sem semear linha (ADR-014).
            return McpServerSettings.Factory;
        }

        try
        {
            return new McpServerSettings(row.Enabled, row.Port, row.StartWithApp, row.ReadOnly);
        }
        catch (DomainException exception)
        {
            // Degrada para o padrão, que é desligado: uma linha corrompida nunca
            // vira uma porta aberta que ninguém pediu.
            logger.LogWarning(exception, "InvalidMcpServerSettingsStored");
            return McpServerSettings.Factory;
        }
    }

    public async Task SaveAsync(
        McpServerSettings settings,
        CancellationToken cancellationToken = default)
    {
        var row = await context.McpServerSettings.FirstOrDefaultAsync(
            stored => stored.Id == McpServerSettingsRow.SingletonId,
            cancellationToken);

        if (row is null)
        {
            row = new McpServerSettingsRow();
            await context.McpServerSettings.AddAsync(row, cancellationToken);
        }

        row.Enabled = settings.Enabled;
        row.Port = settings.Port;
        row.StartWithApp = settings.StartWithApp;
        row.ReadOnly = settings.ReadOnly;
    }
}
