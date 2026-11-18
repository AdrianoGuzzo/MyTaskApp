using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Server.Application;

public interface ITerminalHistoryStore
{
    Task<TerminalLifecycleCurrentList> ListCurrentAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task IngestAsync(Guid userId, Guid deviceId, TerminalLifecyclePage batch, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteTerminalSessionView>> ListAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<TerminalLifecyclePage> EventsAsync(Guid userId, Guid sessionId, long after, CancellationToken cancellationToken);
}
