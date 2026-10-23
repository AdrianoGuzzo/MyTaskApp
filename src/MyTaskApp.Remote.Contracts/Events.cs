using System.Text.Json;

namespace MyTaskApp.Remote.Contracts;

public enum RemoteEventKind
{
    StatusChanged,
    StepStarted,
    StepCompleted,
    Checkpoint,

    /// <summary>Uma linha de stdout ou stderr, já mascarada.</summary>
    Output,

    /// <summary>Texto do Claude, do <c>stream-json</c>.</summary>
    AgentMessage,

    /// <summary>A ferramenta que o Claude usou e um resumo da entrada.</summary>
    ToolUse,

    ApprovalRequested,
    ApprovalResolved,
    Warning,
    Result,
}

/// <param name="Seq">1, 2, 3… sem buraco, gerado pelo agente. Repetido é ignorado; o servidor guarda por <c>(OperationId, Seq)</c>.</param>
/// <param name="Text">Já mascarado, até <see cref="RemoteLimits.MaxEventTextLength"/>.</param>
public sealed record RemoteOperationEvent(
    Guid OperationId,
    long Seq,
    DateTimeOffset At,
    RemoteEventKind Kind,
    string? Step = null,
    RemoteOperationStatus? Status = null,
    string? Text = null,
    JsonElement? Data = null);

/// <summary>Um lote de eventos do agente. Reenviar é inofensivo.</summary>
public sealed record EventBatch(Guid OperationId, IReadOnlyList<RemoteOperationEvent> Events);

/// <summary>O maior <c>Seq</c> sem buraco que o servidor tem: o agente apaga do diário só até ele.</summary>
public sealed record EventBatchAck(long LastContiguousSeq);

public static class RemoteEventSequence
{
    /// <summary>
    /// Até onde a sequência anda sem buraco, partindo do que já estava
    /// confirmado. Eventos fora de ordem esperam o que falta chegar.
    /// </summary>
    public static long LastContiguous(long confirmed, IEnumerable<long> received)
    {
        var seen = received.Where(seq => seq > confirmed).ToHashSet();
        var last = confirmed;

        while (seen.Contains(last + 1))
        {
            last++;
        }

        return last;
    }
}
