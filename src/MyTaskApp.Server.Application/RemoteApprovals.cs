using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Application;

/// <summary>
/// As aprovações (fase 2, T-19; ADR-086): o agente alvo pede um humano, o
/// controlador responde assinando, e a resposta vai ao agente como veio. O
/// servidor confere que quem respondeu é da conta, com a chave ativa, e que a
/// resposta é para aquele pedido; em quem confiar, quem decide é o agente.
/// </summary>
public sealed class RemoteApprovalUseCases(
    IRemoteDeviceStore remote,
    IRemoteOperationStore operations,
    IRemoteApprovalStore approvals,
    IRemoteNotifier notifier,
    TimeProvider timeProvider,
    ILogger<RemoteApprovalUseCases> logger)
{
    /// <summary>O pedido inteiro que a tela mostra: folga para um comando longo, e nada além.</summary>
    internal const int MaxDetailsLength = 16 * 1024;

    /// <summary>
    /// O agente alvo pede uma aprovação. O pedido vem selado
    /// (<see cref="ApprovalHash"/>) e é guardado sem mudar um byte: cortar o
    /// resumo mudaria o hash que amarra a resposta.
    /// </summary>
    public async Task<ApprovalView> RequestAsync(SyncCaller caller, Guid operationId, ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (request.OperationId != operationId || request.ApprovalId == Guid.Empty)
        {
            throw SyncServerException.BadRequest("A aprovação é de outra operação, ou não tem id.");
        }

        Validate(request);

        if (!ApprovalHash.IsSealed(request))
        {
            throw new SyncServerException(RemoteErrorCodes.HashMismatch, 400, "O hash do pedido não confere com o pedido.");
        }

        var operation = await operations.FindAsync(caller.UserId, operationId, cancellationToken) ?? throw SyncServerException.NotFound();

        if (operation.TargetDeviceId != caller.DeviceId)
        {
            throw new SyncServerException(RemoteErrorCodes.WrongTarget, 403, "Esta operação é de outra máquina.");
        }

        if (await approvals.FindAsync(caller.UserId, request.ApprovalId, cancellationToken) is { } existing)
        {
            return SameOrConflict(existing, request.RequestHash);
        }

        var now = timeProvider.GetUtcNow();

        if (request.ExpiresAt <= now)
        {
            throw new SyncServerException(RemoteErrorCodes.AlreadyExpired, 400, "O prazo da aprovação já passou.");
        }

        if (request.ExpiresAt > now + RemoteLimits.MaxApprovalLifetime)
        {
            throw new SyncServerException(RemoteErrorCodes.WindowTooLong, 400, "Uma aprovação espera no máximo uma hora.");
        }

        if (RemoteOperationLifecycle.IsFinal(operation.Status))
        {
            throw new SyncServerException(RemoteErrorCodes.InvalidTransition, 409, "A operação já terminou.");
        }

        var approval = new ApprovalRecord
        {
            UserId = caller.UserId,
            TargetDeviceId = caller.DeviceId,
            Request = request,
            CreatedAt = now,
            State = ApprovalState.Pending,
        };

        if (!await approvals.TryAddAsync(approval, cancellationToken))
        {
            return await approvals.FindAsync(caller.UserId, request.ApprovalId, cancellationToken) is { } raced
                ? SameOrConflict(raced, request.RequestHash)
                : throw IdempotencyKeyReuse();
        }

        logger.LogInformation(
            "RemoteApprovalRequested {UserId} {ApprovalId} {OperationId} {Kind}",
            caller.UserId, request.ApprovalId, operationId, request.Kind);

        await notifier.ToAccountAsync(caller.UserId, RemoteHubMessages.ApprovalRequested, request.ApprovalId, operationId);

        return ToView(approval, now);
    }

    /// <summary>As aprovações da conta ainda esperando resposta, as mais velhas primeiro.</summary>
    public async Task<IReadOnlyList<ApprovalView>> ListPendingAsync(SyncCaller caller, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        return [.. (await approvals.ListPendingAsync(caller.UserId, now, cancellationToken)).Select(approval => ToView(approval, now))];
    }

    public async Task<ApprovalView> GetAsync(SyncCaller caller, Guid approvalId, CancellationToken cancellationToken) =>
        ToView(await FindAsync(caller, approvalId, cancellationToken), timeProvider.GetUtcNow());

    /// <summary>
    /// Um dispositivo da conta responde, assinando com a chave ativa dele. A
    /// resposta é de uso único e tem de ser para este pedido; vencida, vale negar.
    /// </summary>
    public async Task<ApprovalView> RespondAsync(SyncCaller caller, Guid approvalId, SignedEnvelope envelope, CancellationToken cancellationToken)
    {
        var approval = await FindAsync(caller, approvalId, cancellationToken);

        var key = await remote.FindActiveKeyAsync(caller.UserId, caller.DeviceId, cancellationToken)
            ?? throw new SyncServerException(RemoteErrorCodes.NotRegistered, 409, "Registre este dispositivo na orquestração antes de responder.");

        if (!RemoteJws.TryVerify<ApprovalResponse>(envelope, RemoteJws.ApprovalType, key, out var response))
        {
            throw new SyncServerException(RemoteErrorCodes.UntrustedSignature, 403, "A resposta não foi assinada pela chave deste dispositivo.");
        }

        if (response.ApprovalId != approval.Request.ApprovalId
            || response.OperationId != approval.Request.OperationId
            || response.RequestHash != approval.Request.RequestHash)
        {
            throw new SyncServerException(RemoteErrorCodes.HashMismatch, 409, "A resposta não é para este pedido de aprovação.");
        }

        var now = timeProvider.GetUtcNow();

        if (approval.State != ApprovalState.Pending)
        {
            return Answered(approval, envelope, now);
        }

        // A operação acabou (cancelada, falhou) com a pergunta ainda aberta: não há mais quem espere a resposta.
        if (await operations.FindAsync(caller.UserId, approval.Request.OperationId, cancellationToken) is { } operation
            && RemoteOperationLifecycle.IsFinal(operation.Status))
        {
            foreach (var closed in await approvals.CloseForOperationAsync(caller.UserId, operation.OperationId, cancellationToken))
            {
                await ResolvedAsync(closed);
            }

            throw new SyncServerException(RemoteErrorCodes.ApprovalExpired, 410, "A operação já terminou: a aprovação não vale mais.");
        }

        if (approval.Request.ExpiresAt <= now)
        {
            if (await approvals.ExpireAsync(approvalId, now, cancellationToken))
            {
                await ResolvedAsync(approval);
            }

            throw new SyncServerException(RemoteErrorCodes.ApprovalExpired, 410, "A aprovação expirou: vale negar.");
        }

        var state = response.Decision == ApprovalDecision.Allow ? ApprovalState.Allowed : ApprovalState.Denied;

        if (!await approvals.TryAnswerAsync(approvalId, state, envelope.Jws, caller.DeviceId, now, cancellationToken))
        {
            return Answered(await FindAsync(caller, approvalId, cancellationToken), envelope, now);
        }

        logger.LogInformation(
            "RemoteApprovalAnswered {UserId} {ApprovalId} {OperationId} {State} {DeviceId}",
            caller.UserId, approvalId, approval.Request.OperationId, state, caller.DeviceId);

        await ResolvedAsync(approval);

        return ToView(await FindAsync(caller, approvalId, cancellationToken), now);
    }

    /// <summary>A varredura: a que passou do prazo sem resposta expira, e o agente é avisado.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var expired = await approvals.ExpireDueAsync(timeProvider.GetUtcNow(), cancellationToken);

        foreach (var approval in expired)
        {
            await ResolvedAsync(approval);
        }

        if (expired.Count > 0)
        {
            logger.LogInformation("RemoteApprovalsExpired {Count}", expired.Count);
        }

        return expired.Count;
    }

    /// <summary>Mais estrito que o resto: o texto é o que a pessoa lê antes de aprovar.</summary>
    internal static void Validate(ApprovalRequest request)
    {
        if (!Enum.IsDefined(request.Kind))
        {
            throw SyncServerException.BadRequest("Tipo de aprovação desconhecido.");
        }

        if (request.Summary.Length is 0 or > RemoteLimits.MaxApprovalSummaryLength
            || ReceivedText.HasControlOrFormat(request.Summary)
            || request.TypedConfirmationHint is { Length: 0 or > RemoteLimits.MaxApprovalSummaryLength }
            || ReceivedText.HasControlOrFormat(request.TypedConfirmationHint))
        {
            throw SyncServerException.BadRequest(
                $"O resumo e a dica de confirmação vão de 1 a {RemoteLimits.MaxApprovalSummaryLength} caracteres, sem caracteres de controle.");
        }

        var details = request.Details.ValueKind == JsonValueKind.Undefined ? string.Empty : request.Details.GetRawText();

        if (details.Length is 0 or > MaxDetailsLength)
        {
            throw SyncServerException.BadRequest($"Os detalhes da aprovação vão até {MaxDetailsLength} caracteres.");
        }
    }

    internal static ApprovalView ToView(ApprovalRecord approval, DateTimeOffset now) => new(
        approval.Request,
        approval.TargetDeviceId,
        approval.State == ApprovalState.Pending && approval.Request.ExpiresAt <= now ? ApprovalState.Expired : approval.State,
        approval.Response is { } response ? new SignedEnvelope(response) : null,
        approval.RespondedByDeviceId,
        approval.RespondedAt);

    private async Task<ApprovalRecord> FindAsync(SyncCaller caller, Guid approvalId, CancellationToken cancellationToken) =>
        await approvals.FindAsync(caller.UserId, approvalId, cancellationToken) ?? throw SyncServerException.NotFound();

    /// <summary>
    /// A resposta chega a todas as conexões da conta: o agente alvo, que espera
    /// por ela, e os outros controladores, que tiram o pedido da tela.
    /// </summary>
    private Task ResolvedAsync(ApprovalRecord approval) =>
        notifier.ToAccountAsync(approval.UserId, RemoteHubMessages.ApprovalResolved, approval.Request.ApprovalId, approval.Request.OperationId);

    /// <summary>O mesmo envelope de novo (a resposta que se perdeu) devolve a aprovação; outro é a segunda resposta.</summary>
    private static ApprovalView Answered(ApprovalRecord approval, SignedEnvelope envelope, DateTimeOffset now)
    {
        if (approval.Response == envelope.Jws)
        {
            return ToView(approval, now);
        }

        throw approval.State == ApprovalState.Expired
            ? new SyncServerException(RemoteErrorCodes.ApprovalExpired, 410, "A aprovação expirou: vale negar.")
            : new SyncServerException(RemoteErrorCodes.ApprovalAnswered, 409, "Esta aprovação já foi respondida.");
    }

    private ApprovalView SameOrConflict(ApprovalRecord existing, string hash) =>
        existing.Request.RequestHash == hash ? ToView(existing, timeProvider.GetUtcNow()) : throw IdempotencyKeyReuse();

    private static SyncServerException IdempotencyKeyReuse() =>
        new(RemoteErrorCodes.IdempotencyKeyReuse, 409, "Este id de aprovação já foi usado com outro pedido.");
}

/// <summary>Uma aprovação guardada: o pedido como o agente selou, e a resposta assinada, se houver.</summary>
public sealed class ApprovalRecord
{
    public required Guid UserId { get; init; }

    public required Guid TargetDeviceId { get; init; }

    public required ApprovalRequest Request { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required ApprovalState State { get; init; }

    /// <summary>O JWS da resposta, byte a byte.</summary>
    public string? Response { get; init; }

    public Guid? RespondedByDeviceId { get; init; }

    public DateTimeOffset? RespondedAt { get; init; }
}

public interface IRemoteApprovalStore
{
    /// <summary>Falso se o id já existe, nesta conta ou em outra.</summary>
    Task<bool> TryAddAsync(ApprovalRecord approval, CancellationToken cancellationToken);

    Task<ApprovalRecord?> FindAsync(Guid userId, Guid approvalId, CancellationToken cancellationToken);

    /// <summary>As pendentes e no prazo da conta.</summary>
    Task<IReadOnlyList<ApprovalRecord>> ListPendingAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Grava a resposta se ainda está pendente; falso se outra chegou antes.</summary>
    Task<bool> TryAnswerAsync(
        Guid approvalId,
        ApprovalState state,
        string response,
        Guid respondedByDeviceId,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>Marca expirada a pendente vencida; falso se já não estava pendente.</summary>
    Task<bool> ExpireAsync(Guid approvalId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Todas as pendentes vencidas viram <see cref="ApprovalState.Expired"/>; devolve quais.</summary>
    Task<IReadOnlyList<ApprovalRecord>> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// As pendentes de uma operação que terminou viram <see cref="ApprovalState.Expired"/>
    /// (para o agente, negar); devolve quais.
    /// </summary>
    Task<IReadOnlyList<ApprovalRecord>> CloseForOperationAsync(Guid userId, Guid operationId, CancellationToken cancellationToken);
}
