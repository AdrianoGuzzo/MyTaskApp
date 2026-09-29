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

    public Dictionary<string, bool> Monitoring { get; } = [];

    public Task<bool?> GetMonitoringAsync(string providerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Monitoring.TryGetValue(providerId, out var enabled) ? enabled : (bool?)null);

    public Task SaveMonitoringAsync(string providerId, bool enabled, CancellationToken cancellationToken = default)
    {
        Monitoring[providerId] = enabled;
        return Task.CompletedTask;
    }

    public Dictionary<string, string> Models { get; } = [];

    public Task<string?> GetModelAsync(string providerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Models.TryGetValue(providerId, out var model) ? model : null);

    public Task SaveModelAsync(string providerId, string model, CancellationToken cancellationToken = default)
    {
        Models[providerId] = model;
        return Task.CompletedTask;
    }

    public Dictionary<string, string> Efforts { get; } = [];

    public Task<string?> GetEffortAsync(string providerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Efforts.TryGetValue(providerId, out var effort) ? effort : null);

    public Task SaveEffortAsync(string providerId, string effort, CancellationToken cancellationToken = default)
    {
        Efforts[providerId] = effort;
        return Task.CompletedTask;
    }
}
