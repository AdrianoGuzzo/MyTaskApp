using MyTaskApp.Application.External;

namespace MyTaskApp.Application.Tests.Fakes;

internal sealed class FakeBranchConventionStore : IBranchConventionStore
{
    public BranchConventions Conventions { get; set; } = BranchConventions.Default;

    public int SaveCount { get; private set; }

    public Task<BranchConventions> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Conventions);

    public Task SaveAsync(BranchConventions conventions, CancellationToken cancellationToken = default)
    {
        Conventions = conventions;
        SaveCount++;
        return Task.CompletedTask;
    }
}
