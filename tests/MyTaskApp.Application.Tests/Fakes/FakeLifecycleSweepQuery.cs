using MyTaskApp.Application.Lifecycle;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// Devolve candidatos fixos e registra os cortes pedidos — é comparando o corte
/// com o relógio falso que se prova que o prazo conta da conclusão, e não da
/// criação (§2).
/// </summary>
internal sealed class FakeLifecycleSweepQuery : ILifecycleSweepQuery
{
    public List<Guid> ReadyToArchive { get; } = [];

    public List<Guid> ReadyToPurge { get; } = [];

    public DateTimeOffset? ArchiveCutoffAsked { get; private set; }

    public DateTimeOffset? PurgeCutoffAsked { get; private set; }

    public int ArchiveCallCount { get; private set; }

    public Task<IReadOnlyList<Guid>> GetReadyToArchiveAsync(
        DateTimeOffset concludedBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArchiveCutoffAsked = concludedBefore;
        ArchiveCallCount++;

        return Task.FromResult<IReadOnlyList<Guid>>([.. ReadyToArchive.Take(limit)]);
    }

    public Task<IReadOnlyList<Guid>> GetReadyToPurgeAsync(
        DateTimeOffset deletedBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        PurgeCutoffAsked = deletedBefore;

        return Task.FromResult<IReadOnlyList<Guid>>([.. ReadyToPurge.Take(limit)]);
    }
}
