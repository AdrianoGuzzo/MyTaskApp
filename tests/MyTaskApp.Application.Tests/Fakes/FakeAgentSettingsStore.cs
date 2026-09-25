using MyTaskApp.Application.Agents;

namespace MyTaskApp.Application.Tests.Fakes;

internal sealed class FakeAgentSettingsStore : IAgentSettingsStore
{
    public Dictionary<string, string> Arguments { get; } = [];

    public int SaveCount { get; private set; }

    public Task<string?> GetArgumentsAsync(string providerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Arguments.TryGetValue(providerId, out var arguments) ? arguments : null);

    public Task SaveArgumentsAsync(string providerId, string arguments, CancellationToken cancellationToken = default)
    {
        Arguments[providerId] = arguments;
        SaveCount++;
        return Task.CompletedTask;
    }
}
