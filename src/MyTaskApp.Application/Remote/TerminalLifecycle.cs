using MyTaskApp.Remote.Contracts;
namespace MyTaskApp.Application.Remote;

public interface ITerminalLifecycleApi
{
    Task<TerminalLifecycleCurrentList> ListCurrentAsync(CancellationToken cancellationToken);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
    Task PublishAsync(TerminalLifecyclePage batch, CancellationToken cancellationToken);
    Task<TerminalLifecyclePage> EventsAsync(Guid sessionId, long after, CancellationToken cancellationToken);
}

public interface ITerminalLifecycleJournal
{
    Task RecoverAsync(IReadOnlyList<Guid> liveSessionIds, CancellationToken cancellationToken);
    Task CaptureAsync(IReadOnlyList<RemoteTerminalSessionReport> states, CancellationToken cancellationToken);
    Task<TerminalLifecyclePage> PendingAsync(CancellationToken cancellationToken);
    Task AcknowledgeAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken);
}
