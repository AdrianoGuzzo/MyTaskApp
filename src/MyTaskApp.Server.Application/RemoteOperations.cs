using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Application;

/// <summary>
/// A fila de operações (fase 2, T-12; ADR-086): o controlador cria assinando,
/// o agente da máquina alvo busca, aceita, conta o andamento em eventos e
/// termina. O servidor guarda o pedido como veio e nunca o reescreve: o agente
/// confere a assinatura nos bytes do controlador, e não numa cópia.
/// </summary>
public sealed class RemoteOperationUseCases(
    IDeviceRegistry devices,
    IWorkspaceAccess workspaces,
    IRemoteDeviceStore remote,
    IRemoteOperationStore operations,
    IRemoteApprovalStore approvals,
    IRemoteNotifier notifier,
    TimeProvider timeProvider,
    ILogger<RemoteOperationUseCases> logger)
{
    internal const int DefaultListLimit = 50;

    internal const int MaxListLimit = 200;

    internal const int MaxResultCodeLength = 64;

    internal const int MaxStepLength = 100;

    /// <summary>Quantas vezes uma mudança de estado tenta de novo quando outra chegou junto.</summary>
    private const int MaxAttempts = 3;

    /// <summary>
    /// O controlador do token cria uma operação. Reenviar o mesmo envelope
    /// devolve a mesma operação; outro pedido com o mesmo id é recusado.
    /// </summary>
    public async Task<RemoteOperationView> CreateAsync(SyncCaller caller, SignedEnvelope envelope, CancellationToken cancellationToken)
    {
        var key = await remote.FindActiveKeyAsync(caller.UserId, caller.DeviceId, cancellationToken)
            ?? throw new SyncServerException(RemoteErrorCodes.NotRegistered, 409, "Registre este dispositivo na orquestração antes de pedir operações.");

        if (!RemoteJws.TryVerify<RemoteOperationRequest>(envelope, RemoteJws.OperationType, key, out var request))
        {
            throw new SyncServerException(RemoteErrorCodes.UntrustedSignature, 403, "O pedido não foi assinado pela chave deste dispositivo.");
        }

        var hash = HashOf(envelope);

        // Antes das regras: o reenvio de um pedido que já venceu devolve o que foi gravado.
        if (await operations.FindAsync(caller.UserId, request.OperationId, cancellationToken) is { } existing)
        {
            return SameOrConflict(existing, hash);
        }

        if (request.RequestedByDeviceId != caller.DeviceId)
        {
            throw SyncServerException.Forbidden("O pedido diz ser de outro dispositivo.");
        }

        var now = timeProvider.GetUtcNow();

        if (RemoteOperationRules.Problems(request, now) is [var problem, ..])
        {
            throw new SyncServerException(
                problem,
                400,
                problem == RemoteErrorCodes.ClockSkew
                    ? "O relógio deste aparelho está adiantado em relação ao servidor. Acerte a hora."
                    : "O pedido não segue as regras da orquestração.");
        }

        if (await workspaces.RoleOfAsync(caller.UserId, request.WorkspaceId, cancellationToken) is null)
        {
            throw new SyncServerException(RemoteErrorCodes.WrongWorkspace, 403, "Você não participa dessa conta.");
        }

        await EnsureExecutorAsync(caller.UserId, request.TargetDeviceId, cancellationToken);

        var operation = new RemoteOperationRecord
        {
            OperationId = request.OperationId,
            UserId = caller.UserId,
            WorkspaceId = request.WorkspaceId,
            TargetDeviceId = request.TargetDeviceId,
            RequestedByDeviceId = request.RequestedByDeviceId,
            Kind = request.Kind,
            TaskId = request.TaskId,
            Status = RemoteOperationStatus.Queued,
            CreatedAt = request.CreatedAt,
            NotAfter = request.NotAfter,
            ReceivedAt = now,
            UpdatedAt = now,
            Request = envelope.Jws,
            RequestHash = hash,
            AgentSessionId = SessionOf(request),
        };

        // O teto conta e grava de uma vez (fase 3, E9): dois pedidos juntos não passam dele.
        switch (await operations.TryAddAsync(operation, RemoteLimits.MaxOpenOperationsPerExecutor, cancellationToken))
        {
            case RemoteAddOutcome.Full:
                throw new SyncServerException(
                    RemoteErrorCodes.TooManyOperations,
                    409,
                    $"Essa máquina já tem {RemoteLimits.MaxOpenOperationsPerExecutor} pedidos sem terminar. Espere ou cancele algum.");

            case RemoteAddOutcome.Duplicate:
                // Dois envios juntos do mesmo pedido: vale o que gravou primeiro. Sem
                // ele nesta conta, o id é de outra, e não serve aqui.
                return await operations.FindAsync(caller.UserId, request.OperationId, cancellationToken) is { } raced
                    ? SameOrConflict(raced, hash)
                    : throw IdempotencyKeyReuse();
        }

        logger.LogInformation(
            "RemoteOperationQueued {UserId} {OperationId} {Kind} {TargetDeviceId}",
            caller.UserId, operation.OperationId, operation.Kind, operation.TargetDeviceId);

        await notifier.ToDeviceAsync(caller.UserId, operation.TargetDeviceId, RemoteHubMessages.OperationAvailable, operation.OperationId);
        await ChangedAsync(caller.UserId, operation.OperationId, RemoteOperationStatus.Queued);

        // Relida: o banco guarda microssegundos, e o reenvio tem de devolver a mesma.
        return ToView(await FindAsync(caller, operation.OperationId, cancellationToken));
    }

    /// <summary>As operações mais recentes da conta, as recebidas por último primeiro.</summary>
    public async Task<IReadOnlyList<RemoteOperationView>> ListAsync(SyncCaller caller, int? limit, CancellationToken cancellationToken) =>
        [
            .. (await operations.ListRecentAsync(caller.UserId, Math.Clamp(limit ?? DefaultListLimit, 1, MaxListLimit), cancellationToken))
                .Select(ToView),
        ];

    public async Task<RemoteOperationView> GetAsync(SyncCaller caller, Guid operationId, CancellationToken cancellationToken) =>
        ToView(await FindAsync(caller, operationId, cancellationToken));

    /// <summary>
    /// O que o agente da máquina do token tem a fazer: a fila passa a
    /// <see cref="RemoteOperationStatus.Dispatched"/>, e as já entregues e não
    /// terminadas voltam junto. Vencida sem aceite não volta.
    /// </summary>
    public async Task<IReadOnlyList<AgentOperation>> AgentOperationsAsync(SyncCaller caller, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var dispatched = await operations.DispatchQueuedAsync(caller.UserId, caller.DeviceId, now, cancellationToken);

        if (dispatched.Count > 0)
        {
            logger.LogInformation("RemoteOperationsDispatched {UserId} {DeviceId} {Count}", caller.UserId, caller.DeviceId, dispatched.Count);
        }

        foreach (var operationId in dispatched)
        {
            await ChangedAsync(caller.UserId, operationId, RemoteOperationStatus.Dispatched);
        }

        return
        [
            .. (await operations.ListActiveForTargetAsync(caller.UserId, caller.DeviceId, now, cancellationToken))
                .Select(operation => new AgentOperation(new SignedEnvelope(operation.Request), operation.Status, operation.CancelRequestedAt)),
        ];
    }

    /// <summary>
    /// O agente alvo muda o estado. Repetir o atual não muda nada; o que o
    /// ciclo de vida não prevê, ou não é do agente decidir, é recusado.
    /// </summary>
    public async Task<RemoteOperationView> UpdateStatusAsync(
        SyncCaller caller,
        Guid operationId,
        OperationStatusUpdate update,
        CancellationToken cancellationToken)
    {
        var code = Cap(update.Code, MaxResultCodeLength);
        var summary = Cap(update.Summary, RemoteLimits.MaxResultSummaryLength);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var operation = await FindTargetedAsync(caller, operationId, cancellationToken);
            var now = timeProvider.GetUtcNow();

            if (operation.Status == update.Status)
            {
                return ToView(operation);
            }

            if (!RemoteOperationLifecycle.CanMove(SourceOf(operation.Status, update.Status), update.Status, StatusAuthority.Agent))
            {
                throw InvalidTransition();
            }

            // Aceitar depois do prazo, antes de a varredura passar: vence agora.
            if (update.Status == RemoteOperationStatus.Accepted && operation.NotAfter <= now)
            {
                if (await operations.TryMoveAsync(operationId, operation.Status, RemoteOperationStatus.Expired, now, null, null, cancellationToken))
                {
                    await ChangedAsync(caller.UserId, operationId, RemoteOperationStatus.Expired);
                }

                throw new SyncServerException(RemoteErrorCodes.AlreadyExpired, 409, "O prazo para aceitar esta operação já passou.");
            }

            if (await operations.TryMoveAsync(operationId, operation.Status, update.Status, now, code, summary, cancellationToken))
            {
                logger.LogInformation(
                    "RemoteOperationStatusChanged {UserId} {OperationId} {From} {To}",
                    caller.UserId, operationId, operation.Status, update.Status);

                await ChangedAsync(caller.UserId, operationId, update.Status);

                if (RemoteOperationLifecycle.IsFinal(update.Status))
                {
                    await CloseApprovalsAsync(caller.UserId, operationId, cancellationToken);
                }

                return ToView(await FindAsync(caller, operationId, cancellationToken));
            }
        }

        throw InvalidTransition();
    }

    /// <summary>
    /// Um lote de eventos do agente alvo. O que já estava guardado é ignorado,
    /// e a resposta diz até onde a sequência chegou sem buraco.
    /// </summary>
    public async Task<EventBatchAck> AppendEventsAsync(SyncCaller caller, Guid operationId, EventBatch batch, CancellationToken cancellationToken)
    {
        if (batch.OperationId != operationId || batch.Events.Any(item => item.OperationId != operationId))
        {
            throw SyncServerException.BadRequest("Os eventos são de outra operação.");
        }

        if (batch.Events.Count > RemoteLimits.MaxEventsPerBatch || batch.Events.Any(item => item.Seq < 1))
        {
            throw SyncServerException.BadRequest($"Até {RemoteLimits.MaxEventsPerBatch} eventos por lote, com Seq a partir de 1.");
        }

        var operation = await FindTargetedAsync(caller, operationId, cancellationToken);
        var events = batch.Events.DistinctBy(item => item.Seq).Select(Prepare).ToList();

        var last = await operations.AppendEventsAsync(
            operation.OperationId, events, RemoteLimits.MaxEventsPerOperation, timeProvider.GetUtcNow(), cancellationToken);

        await notifier.ToAccountAsync(caller.UserId, RemoteHubMessages.EventsAppended, operationId, last);

        return new EventBatchAck(last);
    }

    /// <summary>
    /// Os eventos depois de <paramref name="after"/>, em ordem, para quem é da
    /// conta, e só até o primeiro buraco (4.6): o que vem depois dele espera o
    /// que falta chegar. Quem lê anda o <c>after</c> pelo último que recebeu, e
    /// um evento entregue depois do buraco faria o que falta nunca aparecer.
    /// </summary>
    public async Task<RemoteEventPage> ListEventsAsync(SyncCaller caller, Guid operationId, long? after, int? limit, CancellationToken cancellationToken)
    {
        var operation = await FindAsync(caller, operationId, cancellationToken);
        var size = Math.Clamp(limit ?? RemoteLimits.MaxEventPageSize, 1, RemoteLimits.MaxEventPageSize);

        // Um a mais que a página: sobrando, há mais depois dela.
        var events = (await operations.ListEventsAsync(operationId, Math.Max(after ?? 0, 0), size + 1, cancellationToken))
            .Where(item => item.Seq <= operation.LastContiguousSeq)
            .ToList();

        return new RemoteEventPage([.. events.Take(size)], operation.LastContiguousSeq, events.Count > size);
    }

    /// <summary>
    /// Qualquer dispositivo da conta pede para cancelar. Antes do aceite, o
    /// servidor cancela; depois, marca o pedido e o agente decide quando parar.
    /// </summary>
    public async Task<RemoteOperationView> CancelAsync(SyncCaller caller, Guid operationId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var operation = await FindAsync(caller, operationId, cancellationToken);
            var now = timeProvider.GetUtcNow();

            if (RemoteOperationLifecycle.IsFinal(operation.Status))
            {
                return ToView(operation);
            }

            if (operation.Status is RemoteOperationStatus.Queued or RemoteOperationStatus.Dispatched)
            {
                if (!await operations.TryMoveAsync(operationId, operation.Status, RemoteOperationStatus.Canceled, now, null, null, cancellationToken))
                {
                    continue;
                }
            }
            else
            {
                await operations.RequestCancelAsync(operationId, now, cancellationToken);
            }

            logger.LogInformation("RemoteOperationCancelRequested {UserId} {OperationId} {Status}", caller.UserId, operationId, operation.Status);

            var view = ToView(await FindAsync(caller, operationId, cancellationToken));

            // A fila não chegou ao agente; a entregue, ele pode ter na mão.
            if (operation.Status != RemoteOperationStatus.Queued)
            {
                await notifier.ToDeviceAsync(caller.UserId, operation.TargetDeviceId, RemoteHubMessages.CancelRequested, operationId);
            }

            await ChangedAsync(caller.UserId, operationId, view.Status);

            return view;
        }

        return ToView(await FindAsync(caller, operationId, cancellationToken));
    }

    /// <summary>
    /// A varredura: entregue sem aceite a tempo volta para a fila, vencida sem
    /// aceite expira, e os eventos mais velhos que <paramref name="retention"/> saem.
    /// A operação terminada há mais que isso sai também, com as aprovações e os
    /// eventos dela (fase 3, E9). O pedido dela já venceu: reenviado, é recusado
    /// pelo <c>NotAfter</c>, e não volta como novo.
    /// </summary>
    public async Task<RemoteSweepResult> SweepAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var expired = await operations.ExpireAsync(now, cancellationToken);
        var requeued = await operations.RequeueAsync(now - RemoteLimits.DispatchTimeout, now, cancellationToken);
        var pruned = await operations.PruneEventsAsync(now - retention, cancellationToken);
        var purged = await operations.PurgeAsync(now - retention, cancellationToken);

        if (expired.Count > 0 || requeued.Count > 0 || pruned > 0 || purged > 0)
        {
            logger.LogInformation(
                "RemoteOperationsSwept {Expired} {Requeued} {PrunedEvents} {PurgedOperations}",
                expired.Count, requeued.Count, pruned, purged);
        }

        foreach (var operation in expired)
        {
            await ChangedAsync(operation.UserId, operation.OperationId, RemoteOperationStatus.Expired);
        }

        foreach (var operation in requeued)
        {
            await notifier.ToDeviceAsync(operation.UserId, operation.TargetDeviceId, RemoteHubMessages.OperationAvailable, operation.OperationId);
            await ChangedAsync(operation.UserId, operation.OperationId, RemoteOperationStatus.Queued);
        }

        return new RemoteSweepResult(expired, requeued, pruned) { PurgedOperations = purged };
    }

    /// <summary>
    /// O estado de onde o agente sai. A varredura pode ter devolvido para a fila
    /// a operação que ele já tinha buscado: aceitar ou recusar vale como se ela
    /// ainda estivesse entregue.
    /// </summary>
    internal static RemoteOperationStatus SourceOf(RemoteOperationStatus current, RemoteOperationStatus requested) =>
        current == RemoteOperationStatus.Queued && requested is RemoteOperationStatus.Accepted or RemoteOperationStatus.Rejected
            ? RemoteOperationStatus.Dispatched
            : current;

    /// <summary>
    /// O evento como fica guardado: texto e etapa dentro do teto, sem o NUL que
    /// o PostgreSQL não grava, e o <c>Data</c> grande demais, ou com o NUL
    /// escapado que o <c>jsonb</c> recusa, deixado de fora.
    /// </summary>
    internal static RemoteOperationEvent Prepare(RemoteOperationEvent item)
    {
        var data = item.Data is { } element
            && element.ValueKind != JsonValueKind.Undefined
            && element.GetRawText() is { } raw
            && raw.Length <= RemoteLimits.MaxEventTextLength
            && !raw.Contains("\\u0000", StringComparison.OrdinalIgnoreCase)
                ? item.Data
                : null;

        return item with
        {
            Text = Truncate(item.Text?.Replace("\0", string.Empty, StringComparison.Ordinal), RemoteLimits.MaxEventTextLength),
            Step = Cap(item.Step, MaxStepLength),
            Data = data,
        };
    }

    /// <summary>Uma linha curta: sem controle nem formatação, e cortada no teto. Vazia vira nula.</summary>
    internal static string? Cap(string? value, int max) =>
        Truncate(ReceivedText.Clean(value)?.Trim(), max) is { Length: > 0 } capped ? capped : null;

    internal static RemoteOperationView ToView(RemoteOperationRecord operation) => new(
        operation.OperationId,
        operation.WorkspaceId,
        operation.TargetDeviceId,
        operation.RequestedByDeviceId,
        operation.Kind,
        operation.TaskId,
        operation.Status,
        operation.CreatedAt,
        operation.NotAfter,
        operation.UpdatedAt,
        operation.CancelRequestedAt,
        operation.ResultCode,
        operation.ResultSummary,
        operation.LastContiguousSeq);

    /// <summary>
    /// A conversa do Claude que o pedido continua ou para (ADR-091). O
    /// <c>agent.start</c> ainda não tem: o relato do agente liga depois.
    /// </summary>
    internal static Guid? SessionOf(RemoteOperationRequest request) => request.Kind switch
    {
        RemoteOperationKinds.AgentPrompt => RemoteOperationRules.PayloadOf<PromptRequest>(request).AgentSessionId,
        RemoteOperationKinds.AgentStop => RemoteOperationRules.PayloadOf<AgentStopRequest>(request).AgentSessionId,
        _ => null,
    };

    /// <summary>SHA-256 dos bytes do envelope, em hexadecimal: o "mesmo pedido" da idempotência.</summary>
    internal static string HashOf(SignedEnvelope envelope) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Jws)));

    private Task ChangedAsync(Guid userId, Guid operationId, RemoteOperationStatus status) =>
        notifier.ToAccountAsync(userId, RemoteHubMessages.OperationChanged, operationId, status);

    /// <summary>
    /// A operação terminou com perguntas abertas (cancelada esperando uma
    /// aprovação, por exemplo): elas fecham como vencidas, e quem as mostra é avisado.
    /// </summary>
    private async Task CloseApprovalsAsync(Guid userId, Guid operationId, CancellationToken cancellationToken)
    {
        var closed = await approvals.CloseForOperationAsync(userId, operationId, cancellationToken);

        foreach (var approval in closed)
        {
            await notifier.ToAccountAsync(userId, RemoteHubMessages.ApprovalResolved, approval.Request.ApprovalId, operationId);
        }

        if (closed.Count > 0)
        {
            logger.LogInformation("RemoteApprovalsClosedWithOperation {UserId} {OperationId} {Count}", userId, operationId, closed.Count);
        }
    }

    private async Task<RemoteOperationRecord> FindAsync(SyncCaller caller, Guid operationId, CancellationToken cancellationToken) =>
        await operations.FindAsync(caller.UserId, operationId, cancellationToken) ?? throw SyncServerException.NotFound();

    /// <summary>Só a máquina alvo fala pela operação; para as outras da conta, ela é só leitura.</summary>
    private async Task<RemoteOperationRecord> FindTargetedAsync(SyncCaller caller, Guid operationId, CancellationToken cancellationToken)
    {
        var operation = await FindAsync(caller, operationId, cancellationToken);

        return operation.TargetDeviceId == caller.DeviceId
            ? operation
            : throw new SyncServerException(RemoteErrorCodes.WrongTarget, 403, "Esta operação é de outra máquina.");
    }

    private async Task EnsureExecutorAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        if (await devices.FindAsync(userId, deviceId, cancellationToken) is not { IsRevoked: false })
        {
            throw SyncServerException.NotFound();
        }

        var target = (await remote.ListAsync(userId, cancellationToken)).SingleOrDefault(state => state.DeviceId == deviceId);

        if (target is null || !target.Roles.HasFlag(DeviceRoles.Executor))
        {
            throw new SyncServerException(RemoteErrorCodes.NotAnExecutor, 409, "Essa máquina não aceita controle remoto. Ligue a opção nela primeiro.");
        }
    }

    private static RemoteOperationView SameOrConflict(RemoteOperationRecord existing, string hash) =>
        existing.RequestHash == hash ? ToView(existing) : throw IdempotencyKeyReuse();

    private static SyncServerException IdempotencyKeyReuse() =>
        new(RemoteErrorCodes.IdempotencyKeyReuse, 409, "Este id de operação já foi usado com outro pedido.");

    private static SyncServerException InvalidTransition() =>
        new(RemoteErrorCodes.InvalidTransition, 409, "A operação não pode ir para esse estado agora.");

    private static string? Truncate(string? value, int max) => value is { } text && text.Length > max ? text[..max] : value;
}

/// <summary>O que uma varredura mudou: quem expirou, quem voltou para a fila e quantos eventos saíram.</summary>
public sealed record RemoteSweepResult(
    IReadOnlyList<RemoteOperationRecord> Expired,
    IReadOnlyList<RemoteOperationRecord> Requeued,
    int PrunedEvents)
{
    /// <summary>As terminadas há mais que a retenção, que saíram com as aprovações e os eventos.</summary>
    public int PurgedOperations { get; init; }
}

/// <summary>Como terminou a gravação de um pedido novo.</summary>
public enum RemoteAddOutcome
{
    Added,

    /// <summary>O id já existe, nesta conta ou em outra.</summary>
    Duplicate,

    /// <summary>A máquina já tem o teto de pedidos sem terminar.</summary>
    Full,
}

/// <summary>Uma operação guardada, com o envelope como o controlador mandou.</summary>
public sealed class RemoteOperationRecord
{
    public required Guid OperationId { get; init; }

    public required Guid UserId { get; init; }

    public required Guid WorkspaceId { get; init; }

    public required Guid TargetDeviceId { get; init; }

    public required Guid RequestedByDeviceId { get; init; }

    public required string Kind { get; init; }

    public Guid? TaskId { get; init; }

    public required RemoteOperationStatus Status { get; init; }

    /// <summary>A hora do controlador, do pedido assinado.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset NotAfter { get; init; }

    /// <summary>A hora do servidor ao gravar: a ordem da lista.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? DispatchedAt { get; init; }

    public DateTimeOffset? CancelRequestedAt { get; init; }

    public string? ResultCode { get; init; }

    public string? ResultSummary { get; init; }

    public long LastContiguousSeq { get; init; }

    /// <summary>O JWS compacto, byte a byte como chegou.</summary>
    public required string Request { get; init; }

    /// <summary>SHA-256 do JWS, em hexadecimal.</summary>
    public required string RequestHash { get; init; }

    /// <summary>A conversa do Claude da operação (ADR-091), quando há.</summary>
    public Guid? AgentSessionId { get; init; }
}

public interface IRemoteOperationStore
{
    /// <summary>
    /// Grava se o id é novo e a máquina tem menos que <paramref name="maxOpen"/>
    /// pedidos sem terminar, contando e gravando de uma vez: dois pedidos
    /// juntos não passam do teto.
    /// </summary>
    Task<RemoteAddOutcome> TryAddAsync(RemoteOperationRecord operation, int maxOpen, CancellationToken cancellationToken);

    Task<RemoteOperationRecord?> FindAsync(Guid userId, Guid operationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteOperationRecord>> ListRecentAsync(Guid userId, int limit, CancellationToken cancellationToken);

    /// <summary>A fila não vencida da máquina passa a entregue; devolve os ids que passaram.</summary>
    Task<IReadOnlyList<Guid>> DispatchQueuedAsync(Guid userId, Guid targetDeviceId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>As entregues e não vencidas, as aceitas, as rodando e as esperando aprovação da máquina, das mais velhas às mais novas.</summary>
    Task<IReadOnlyList<RemoteOperationRecord>> ListActiveForTargetAsync(Guid userId, Guid targetDeviceId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Muda o estado se ele ainda é <paramref name="from"/>; o código e o resumo
    /// nulos mantêm os de antes. Falso se outro chegou antes.
    /// </summary>
    Task<bool> TryMoveAsync(
        Guid operationId,
        RemoteOperationStatus from,
        RemoteOperationStatus to,
        DateTimeOffset at,
        string? code,
        string? summary,
        CancellationToken cancellationToken);

    /// <summary>Marca o pedido de cancelamento numa operação aceita e não terminada; a primeira marca fica.</summary>
    Task RequestCancelAsync(Guid operationId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Grava os eventos que ainda não estavam, travando a operação, e devolve o
    /// maior Seq sem buraco. Com <paramref name="maxStored"/> guardados, a saída
    /// (<see cref="RemoteEventKind.Output"/>) é descartada, mas conta como recebida.
    /// </summary>
    Task<long> AppendEventsAsync(
        Guid operationId,
        IReadOnlyList<RemoteOperationEvent> events,
        int maxStored,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteOperationEvent>> ListEventsAsync(Guid operationId, long after, int limit, CancellationToken cancellationToken);

    /// <summary>As da fila ou entregues que passaram do prazo viram <see cref="RemoteOperationStatus.Expired"/>.</summary>
    Task<IReadOnlyList<RemoteOperationRecord>> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>As entregues antes de <paramref name="dispatchedBefore"/> e sem aceite voltam para a fila.</summary>
    Task<IReadOnlyList<RemoteOperationRecord>> RequeueAsync(DateTimeOffset dispatchedBefore, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// As terminadas antes de <paramref name="before"/> e com o pedido vencido
    /// antes disso; os eventos e as aprovações saem junto, em cascata.
    /// </summary>
    Task<int> PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken);

    /// <summary>Apaga os eventos recebidos antes de <paramref name="receivedBefore"/>.</summary>
    Task<int> PruneEventsAsync(DateTimeOffset receivedBefore, CancellationToken cancellationToken);

    /// <summary>Liga a operação da máquina <paramref name="targetDeviceId"/> à conversa, se ainda não tem uma.</summary>
    Task LinkSessionAsync(Guid userId, Guid targetDeviceId, Guid operationId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>As <paramref name="limit"/> operações mais recentes da conversa pedidas à máquina dela, da mais antiga à mais nova.</summary>
    Task<IReadOnlyList<RemoteOperationRecord>> ListForSessionAsync(Guid userId, Guid targetDeviceId, Guid sessionId, int limit, CancellationToken cancellationToken);
}
