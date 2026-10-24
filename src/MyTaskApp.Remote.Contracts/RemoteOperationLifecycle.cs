namespace MyTaskApp.Remote.Contracts;

/// <summary>
/// As transições válidas de uma operação, e quem decide cada uma (ADR-086).
/// O servidor recusa o que não está aqui, inclusive regressão: um evento
/// atrasado nunca tira uma operação de um estado final.
/// </summary>
public static class RemoteOperationLifecycle
{
    private static readonly Dictionary<RemoteOperationStatus, RemoteOperationStatus[]> Next = new()
    {
        [RemoteOperationStatus.Queued] =
        [
            RemoteOperationStatus.Dispatched, RemoteOperationStatus.Expired, RemoteOperationStatus.Canceled,
        ],

        // Sem aceite a tempo, volta para a fila; o agente que a buscou pode aceitar ou recusar.
        [RemoteOperationStatus.Dispatched] =
        [
            RemoteOperationStatus.Queued, RemoteOperationStatus.Accepted, RemoteOperationStatus.Rejected,
            RemoteOperationStatus.Expired, RemoteOperationStatus.Canceled,
        ],

        // Aceita e ainda não começou: a tarefa pode nunca chegar pelo sync, ou o agente cair.
        [RemoteOperationStatus.Accepted] =
        [
            RemoteOperationStatus.Running, RemoteOperationStatus.Failed, RemoteOperationStatus.Canceled,
            RemoteOperationStatus.Interrupted,
        ],
        [RemoteOperationStatus.Running] =
        [
            RemoteOperationStatus.WaitingApproval, RemoteOperationStatus.Succeeded, RemoteOperationStatus.Failed,
            RemoteOperationStatus.Canceled, RemoteOperationStatus.Interrupted,
        ],
        [RemoteOperationStatus.WaitingApproval] =
        [
            RemoteOperationStatus.Running, RemoteOperationStatus.Failed, RemoteOperationStatus.Canceled,
            RemoteOperationStatus.Interrupted,
        ],
    };

    public static bool IsFinal(RemoteOperationStatus status) => !Next.ContainsKey(status);

    /// <summary>Quem decide chegar em <paramref name="to"/> saindo de <paramref name="from"/>.</summary>
    public static StatusAuthority AuthorityOf(RemoteOperationStatus from, RemoteOperationStatus to) =>
        to is RemoteOperationStatus.Queued or RemoteOperationStatus.Dispatched or RemoteOperationStatus.Expired
        || (to == RemoteOperationStatus.Canceled && from is RemoteOperationStatus.Queued or RemoteOperationStatus.Dispatched)
            ? StatusAuthority.Server
            : StatusAuthority.Agent;

    /// <summary>A transição existe, e quem pede é quem decide.</summary>
    public static bool CanMove(RemoteOperationStatus from, RemoteOperationStatus to, StatusAuthority by) =>
        Next.TryGetValue(from, out var allowed)
        && allowed.Contains(to)
        && AuthorityOf(from, to) == by;
}
