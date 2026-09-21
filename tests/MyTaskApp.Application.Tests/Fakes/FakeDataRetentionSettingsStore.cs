using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Application.Tests.Fakes;

internal sealed class FakeDataRetentionSettingsStore(DataRetentionPolicy? policy = null)
    : IDataRetentionSettingsStore
{
    public DataRetentionPolicy Policy { get; set; } = policy ?? DataRetentionPolicy.Factory;

    public int SaveCount { get; private set; }

    public Task<DataRetentionPolicy> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Policy);

    public Task SaveAsync(
        DataRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        Policy = policy;
        SaveCount++;
        return Task.CompletedTask;
    }
}
