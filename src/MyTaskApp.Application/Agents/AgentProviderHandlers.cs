using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Agents;

public sealed record AgentProviderOption(string Id, string Name);

public sealed record AgentProviderChoices(IReadOnlyList<AgentProviderOption> Providers, string DefaultProviderId);

public sealed class AgentProviderSettingsHandler(
    IAgentCliProviders providers, IAgentSettingsStore settings, IUnitOfWork unitOfWork)
{
    public async Task<AgentProviderChoices> GetAsync(CancellationToken cancellationToken = default) =>
        new(providers.All.Select(provider => new AgentProviderOption(provider.Id, provider.Name)).ToArray(),
            (await providers.ResolveAsync(settings, null, cancellationToken)).Id);

    public async Task SaveAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = providers.Get(providerId);
        await settings.SaveDefaultProviderAsync(provider.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

public static class AgentProviderResolution
{
    public static async Task<IAgentCliProvider> ResolveAsync(
        this IAgentCliProviders providers, IAgentSettingsStore settings, string? providerId,
        CancellationToken cancellationToken = default)
    {
        if (providerId is not null)
            return providers.Get(providerId);
        var saved = await settings.GetDefaultProviderAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(saved) ? providers.Get(null) : providers.Find(saved) ?? providers.Get(null);
    }
}
