using System.Text.Json;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>
/// A fila de operações no servidor (ADR-086): o agente busca o que é desta
/// máquina, muda o estado e conta o andamento; o controlador cria, acompanha e
/// cancela. O servidor só coordena — quem decide se roda é o agente.
/// </summary>
public interface IRemoteOperationsApi
{
    /// <summary>A fila desta máquina: as novas passam a entregues; as já entregues e não terminadas voltam junto.</summary>
    Task<IReadOnlyList<AgentOperation>> ListAgentOperationsAsync(CancellationToken cancellationToken);

    /// <summary>Repetir o estado é nada; regressão e passo que não é do agente voltam como erro.</summary>
    Task<RemoteOperationView> UpdateStatusAsync(Guid operationId, OperationStatusUpdate update, CancellationToken cancellationToken);

    /// <summary>O repetido é ignorado; o ack é o último <c>Seq</c> sem buraco.</summary>
    Task<EventBatchAck> AppendEventsAsync(EventBatch batch, CancellationToken cancellationToken);

    /// <summary>O controlador pede: o mesmo envelope devolve a mesma operação.</summary>
    Task<RemoteOperationView> CreateAsync(SignedEnvelope request, CancellationToken cancellationToken);

    Task<RemoteOperationView> GetAsync(Guid operationId, CancellationToken cancellationToken);

    Task<RemoteEventPage> ListEventsAsync(Guid operationId, long after, int? limit, CancellationToken cancellationToken);

    Task<RemoteOperationView> CancelAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>O catálogo desta máquina (ADR-088). Só de quem é executor.</summary>
    Task<AgentCatalog> PublishCatalogAsync(AgentCatalog catalog, CancellationToken cancellationToken);

    /// <summary>O catálogo que outra máquina da conta publicou.</summary>
    Task<AgentCatalog> GetCatalogAsync(Guid deviceId, CancellationToken cancellationToken);

    /// <summary>O agente pede uma aprovação, selada (<see cref="ApprovalHash"/>). O mesmo pedido de novo devolve a mesma.</summary>
    Task<ApprovalView> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);

    Task<ApprovalView> GetApprovalAsync(Guid approvalId, CancellationToken cancellationToken);

    /// <summary>As que esperam resposta nesta conta.</summary>
    Task<IReadOnlyList<ApprovalView>> ListPendingApprovalsAsync(CancellationToken cancellationToken);

    /// <summary>Um controlador responde, assinando (<see cref="RemoteJws.ApprovalType"/>).</summary>
    Task<ApprovalView> RespondApprovalAsync(Guid approvalId, SignedEnvelope response, CancellationToken cancellationToken);
}

/// <summary>
/// Uma operação no diário do agente (ADR-086): o pedido assinado como chegou,
/// o estado daqui e o que o servidor já sabe. O diário é o que sobrevive a uma
/// queda: o agente reinicia e sabe o que estava no meio e o que falta contar.
/// </summary>
/// <param name="Envelope">O JWS do controlador, os bytes que foram conferidos.</param>
/// <param name="ReportedStatus">O último estado que o servidor confirmou; nulo é "entregue".</param>
/// <param name="NextSeq">O próximo número de evento.</param>
/// <param name="AckedSeq">Até onde o servidor confirmou os eventos.</param>
public sealed record RemoteJournalEntry(
    Guid OperationId,
    Guid ControllerDeviceId,
    string ControllerNickname,
    string Kind,
    Guid? TaskId,
    string Envelope,
    RemoteOperationStatus Status,
    RemoteOperationStatus? ReportedStatus,
    string? ResultCode,
    string? ResultSummary,
    string? Step,
    long NextSeq,
    long AckedSeq,
    DateTimeOffset ReceivedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsFinal => RemoteOperationLifecycle.IsFinal(Status);

    /// <summary>Há estado ou evento que o servidor ainda não sabe.</summary>
    public bool HasUnreported => ReportedStatus != Status || AckedSeq < NextSeq - 1;
}

/// <summary>O diário local das operações remotas. Só o agente escreve nele.</summary>
public interface IRemoteOperationJournal
{
    Task<RemoteJournalEntry?> FindAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary><c>false</c>: o id já estava no diário — "nunca visto" é por aqui (4.4, item 4).</summary>
    Task<bool> TryAddAsync(RemoteJournalEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// O estado daqui. Cada escrita mexe só nos próprios campos: o estado, o
    /// que o servidor sabe e a numeração dos eventos andam em paralelo.
    /// </summary>
    Task SetStatusAsync(Guid operationId, RemoteOperationStatus status, string? code = null, string? summary = null, CancellationToken cancellationToken = default);

    /// <summary>O servidor confirmou este estado.</summary>
    Task MarkReportedAsync(Guid operationId, RemoteOperationStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Numera e guarda até o servidor confirmar; devolve o evento com o
    /// <c>Seq</c>. Um <see cref="RemoteEventKind.StepStarted"/> vira a etapa
    /// do diário, a que a recuperação conta depois de uma queda.
    /// </summary>
    Task<RemoteOperationEvent> AppendEventAsync(RemoteOperationEvent draft, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteOperationEvent>> PendingEventsAsync(Guid operationId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Apaga o que o servidor já tem e guarda até onde. Nunca volta para trás.</summary>
    Task AcknowledgeAsync(Guid operationId, long lastContiguousSeq, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteJournalEntry>> ListUnfinishedAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteJournalEntry>> ListUnreportedAsync(CancellationToken cancellationToken = default);

    /// <summary>Quantas operações deste controlador chegaram desde <paramref name="since"/> (4.4, item 8).</summary>
    Task<int> CountFromControllerSinceAsync(Guid controllerDeviceId, DateTimeOffset since, CancellationToken cancellationToken = default);

    /// <summary>
    /// Esquece as encerradas e já contadas antes de <paramref name="before"/>.
    /// Um pedido vale no máximo <see cref="RemoteLimits.MaxAcceptanceWindow"/>:
    /// depois disso, o "nunca visto" é o prazo vencido que recusa.
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default);
}

/// <summary>As entidades do pedido que ainda não chegaram pelo sync (a tarefa criada no celular agora há pouco).</summary>
public interface IRemoteEntityProbe
{
    Task<IReadOnlyList<Guid>> MissingAsync(IReadOnlyCollection<Guid> entityIds, CancellationToken cancellationToken = default);
}

/// <summary>Os tetos do item 8 de 4.4. Ficam no agente: é ele quem decide.</summary>
public sealed record RemoteDispatchLimits(int MaxConcurrent, int MaxPerControllerPerHour)
{
    public static RemoteDispatchLimits Default { get; } = new(MaxConcurrent: 3, MaxPerControllerPerHour: 120);
}

/// <summary>
/// Um tipo de operação que este agente sabe rodar (ADR-088): traduz o pedido
/// num caso de uso, e nada mais. Regra de negócio não mora aqui.
/// </summary>
public interface IRemoteOperationExecutor
{
    string Kind { get; }

    /// <summary>
    /// O item 6 de 4.4: cada id do pedido existe aqui e está liberado. Devolve
    /// a recusa, com o motivo que a tela de quem pediu mostra, ou nulo.
    /// </summary>
    Task<RemoteRefusal?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Roda num escopo só dela, que dura a operação inteira. O cancelamento é
    /// o pedido de quem controla ou o agente fechando; uma
    /// <see cref="MyTaskApp.Domain.DomainException"/> é a regra do caso de uso
    /// (4.4, item 7) e termina como falha, com a mensagem da tela.
    /// </summary>
    Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken);
}

/// <summary>Como terminou. <c>Summary</c> é a linha da lista: "3 comandos, 1 falhou".</summary>
public sealed record RemoteOperationResult(bool Succeeded, string? Summary = null, string? Code = null, JsonElement? Data = null)
{
    public static RemoteOperationResult Success(string? summary = null, JsonElement? data = null) => new(true, summary, null, data);

    public static RemoteOperationResult Failure(string code, string? summary = null) => new(false, summary, code);
}

/// <summary>Por que esta máquina não roda o pedido: o código (<see cref="RemoteErrorCodes"/>) e, se houver, o motivo em palavras.</summary>
public sealed record RemoteRefusal(string Code, string? Summary = null);

/// <summary>O que o executor recebe: o pedido conferido, quem pediu, por onde contar o andamento e pedir aprovação.</summary>
public sealed class RemoteExecution(
    RemoteOperationRequest request,
    string controllerNickname,
    Func<RemoteOperationEvent, CancellationToken, Task> emit,
    TimeProvider timeProvider,
    Func<RemoteApprovalAsk, CancellationToken, Task<RemoteApprovalResult>>? approve = null)
{
    public RemoteOperationRequest Request { get; } = request;

    public string ControllerNickname { get; } = controllerNickname;

    public T Payload<T>() => RemoteOperationRules.PayloadOf<T>(Request);

    public Task EmitAsync(
        RemoteEventKind kind,
        string? text = null,
        string? step = null,
        JsonElement? data = null,
        CancellationToken cancellationToken = default) =>
        emit(new RemoteOperationEvent(Request.OperationId, 0, timeProvider.GetUtcNow(), kind, step, null, text, data), cancellationToken);

    /// <summary>
    /// Para e espera um humano no controlador. Sem resposta a tempo, ou com
    /// uma que não confere, é negar: só <see cref="RemoteApprovalResult.IsAllowed"/> segue.
    /// </summary>
    public Task<RemoteApprovalResult> RequestApprovalAsync(RemoteApprovalAsk ask, CancellationToken cancellationToken = default) =>
        approve is null
            ? Task.FromResult(new RemoteApprovalResult(RemoteApprovalOutcome.Denied))
            : approve(ask, cancellationToken);
}
