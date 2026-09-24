using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class AgentSettingsStore(MyTaskAppDbContext context) : IAgentSettingsStore
{
    public async Task<string?> GetArgumentsAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var row = await context.AgentSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(settings => settings.ProviderId == providerId, cancellationToken);

        return row?.Arguments;
    }

    public async Task SaveArgumentsAsync(
        string providerId,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        var row = await context.AgentSettings.FirstOrDefaultAsync(
            stored => stored.ProviderId == providerId,
            cancellationToken);

        if (row is null)
        {
            row = new AgentSettingsRow { ProviderId = providerId };
            await context.AgentSettings.AddAsync(row, cancellationToken);
        }

        row.Arguments = arguments;
    }
}
