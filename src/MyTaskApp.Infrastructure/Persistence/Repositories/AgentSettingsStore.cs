using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class AgentSettingsStore(MyTaskAppDbContext context) : IAgentSettingsStore
{
    public async Task<string?> GetArgumentsAsync(string providerId, CancellationToken cancellationToken = default) =>
        (await FindAsync(providerId, cancellationToken))?.Arguments;

    public async Task SaveArgumentsAsync(
        string providerId,
        string arguments,
        CancellationToken cancellationToken = default) =>
        (await TrackedAsync(providerId, cancellationToken)).Arguments = arguments;

    public async Task<bool?> GetMonitoringAsync(string providerId, CancellationToken cancellationToken = default) =>
        (await FindAsync(providerId, cancellationToken))?.MonitorActivity;

    public async Task SaveMonitoringAsync(
        string providerId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        (await TrackedAsync(providerId, cancellationToken)).MonitorActivity = enabled;

    private Task<AgentSettingsRow?> FindAsync(string providerId, CancellationToken cancellationToken) =>
        context.AgentSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(settings => settings.ProviderId == providerId, cancellationToken);

    /// <summary>
    /// A linha rastreada, criada se faltar. Também procura entre as adicionadas
    /// e ainda não gravadas: salvar parâmetros e acompanhamento no mesmo
    /// <c>SaveChanges</c> não pode inserir duas linhas com a mesma chave.
    /// </summary>
    private async Task<AgentSettingsRow> TrackedAsync(string providerId, CancellationToken cancellationToken)
    {
        var row = context.AgentSettings.Local.FirstOrDefault(stored => stored.ProviderId == providerId)
            ?? await context.AgentSettings.FirstOrDefaultAsync(
                stored => stored.ProviderId == providerId,
                cancellationToken);

        if (row is null)
        {
            row = new AgentSettingsRow { ProviderId = providerId };
            await context.AgentSettings.AddAsync(row, cancellationToken);
        }

        return row;
    }
}
