namespace MyTaskApp.Remote.Contracts;

/// <summary>Uma transição do terminal, sem saída de tela ou texto de prompts.</summary>
public sealed record TerminalLifecycleEvent(Guid EventId, Guid DeviceId, Guid SessionId, long Sequence,
    DateTimeOffset OccurredAt, RemoteTerminalSessionReport State);

public sealed record TerminalLifecyclePage(IReadOnlyList<TerminalLifecycleEvent> Events);

public sealed record TerminalLifecycleCurrent(RemoteTerminalSessionView View, long Sequence, Guid EventId);
public sealed record TerminalLifecycleCurrentList(IReadOnlyList<TerminalLifecycleCurrent> Sessions);
