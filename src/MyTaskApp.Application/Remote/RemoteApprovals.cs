using System.Text;
using System.Text.Json;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>O que um executor pede a um humano (ADR-087): o resumo é o que a tela do controlador mostra.</summary>
/// <param name="TypedConfirmationHint">O que a pessoa tem de digitar para permitir (o nome do banco de produção).</param>
/// <param name="Lifetime">Sem resposta até aqui, é negar. Nulo: <see cref="RemoteLimits.DefaultApprovalLifetime"/>.</param>
public sealed record RemoteApprovalAsk(
    ApprovalKind Kind,
    string Summary,
    JsonElement Details,
    string? TypedConfirmationHint = null,
    TimeSpan? Lifetime = null);

public enum RemoteApprovalOutcome
{
    Allowed,
    Denied,

    /// <summary>Ninguém respondeu a tempo: vale negar.</summary>
    Expired,

    /// <summary>Veio uma resposta, e ela não confere (assinatura, hash, outra aprovação, fora do prazo): vale negar.</summary>
    Invalid,
}

/// <summary>Como terminou. Só <see cref="RemoteApprovalOutcome.Allowed"/> deixa seguir.</summary>
public sealed record RemoteApprovalResult(
    RemoteApprovalOutcome Outcome,
    string? ControllerNickname = null,
    bool RememberForThisSession = false)
{
    public bool IsAllowed => Outcome == RemoteApprovalOutcome.Allowed;

    /// <summary>Como a operação termina quando o executor não segue sem a aprovação.</summary>
    public RemoteOperationResult AsFailure(string what) => Outcome == RemoteApprovalOutcome.Expired
        ? RemoteOperationResult.Failure(RemoteErrorCodes.ApprovalExpired, $"{what}: ninguém respondeu a tempo.")
        : RemoteOperationResult.Failure(RemoteErrorCodes.ApprovalDenied, Outcome == RemoteApprovalOutcome.Denied
            ? $"{what}: negado por {ControllerNickname}."
            : $"{what}: a resposta não conferiu.");
}

/// <summary>
/// A resposta de uma aprovação, conferida no agente (seção 4.3): o servidor
/// guarda e repassa, mas quem decide se ela vale é esta máquina. Vale só se
/// é a resposta a <b>este</b> pedido — mesmo id, mesma operação, mesmo hash
/// —, assinada por um controlador pareado e não revogado, dentro do prazo.
/// </summary>
public static class RemoteApprovalVerifier
{
    /// <summary>Nulo: ainda esperando.</summary>
    public static RemoteApprovalResult? Verify(ApprovalRequest asked, ApprovalView view, IReadOnlyList<TrustedController> trusted)
    {
        if (view.State == ApprovalState.Pending)
        {
            return null;
        }

        if (view.State == ApprovalState.Expired || view.Response is not { } envelope)
        {
            return new RemoteApprovalResult(RemoteApprovalOutcome.Expired);
        }

        if (!RemoteJws.TryParse(envelope, out var parts)
            || trusted.FirstOrDefault(controller => controller.IsActive && controller.Fingerprint == parts.KeyFingerprint) is not { } controller
            || !RemoteJws.TryVerify<ApprovalResponse>(
                envelope,
                RemoteJws.ApprovalType,
                new DevicePublicKey(DevicePublicKey.Es256, controller.SubjectPublicKeyInfo, controller.Fingerprint),
                out var response))
        {
            return new RemoteApprovalResult(RemoteApprovalOutcome.Invalid);
        }

        // A resposta de outra aprovação (reuso), de outro pedido (hash) ou depois do prazo não vale aqui.
        var matches = response.ApprovalId == asked.ApprovalId
            && response.OperationId == asked.OperationId
            && response.RequestHash == asked.RequestHash
            && response.RespondedAt <= asked.ExpiresAt
            && (response.Decision == ApprovalDecision.Allow) == (view.State == ApprovalState.Allowed);

        if (!matches)
        {
            return new RemoteApprovalResult(RemoteApprovalOutcome.Invalid, controller.Nickname);
        }

        if (response.Decision == ApprovalDecision.Deny)
        {
            return new RemoteApprovalResult(RemoteApprovalOutcome.Denied, controller.Nickname);
        }

        // Produção pede o nome digitado: permitir sem ele não é permitir.
        if (asked.TypedConfirmationHint is { } hint && !string.Equals(response.TypedConfirmation, hint, StringComparison.Ordinal))
        {
            return new RemoteApprovalResult(RemoteApprovalOutcome.Invalid, controller.Nickname);
        }

        return new RemoteApprovalResult(RemoteApprovalOutcome.Allowed, controller.Nickname, response.RememberForThisSession);
    }
}

/// <summary>As aprovações esperando resposta nesta conta, para o controlador mostrar.</summary>
public sealed record GetPendingApprovals;

public sealed class GetPendingApprovalsHandler(IRemoteOperationsApi remote)
{
    /// <summary>Só as seladas: um pedido cujo hash não confere não chega à tela, porque a resposta a ele não valeria.</summary>
    public async Task<IReadOnlyList<ApprovalView>> HandleAsync(GetPendingApprovals query, CancellationToken cancellationToken = default) =>
        [.. (await remote.ListPendingApprovalsAsync(cancellationToken)).Where(view => ApprovalHash.IsSealed(view.Request))];
}

/// <param name="TypedConfirmation">O que a pessoa digitou, quando o pedido pede.</param>
public sealed record AnswerApproval(Guid ApprovalId, ApprovalDecision Decision, string? TypedConfirmation = null, bool RememberForThisSession = false);

/// <summary>
/// Esta máquina, como controladora, responde a uma aprovação assinando com a
/// própria chave (ADR-087). Assina o hash do pedido que leu: se o servidor
/// trocou o pedido, o agente vê outro hash e não aceita.
/// </summary>
public sealed class AnswerApprovalHandler(IRemoteOperationsApi remote, IDeviceSigner signer, TimeProvider timeProvider)
{
    public async Task<ApprovalView> HandleAsync(AnswerApproval command, CancellationToken cancellationToken = default)
    {
        var view = await remote.GetApprovalAsync(command.ApprovalId, cancellationToken);

        if (view.State != ApprovalState.Pending)
        {
            throw new DomainException(view.State == ApprovalState.Expired
                ? "O prazo para responder passou: o pedido foi negado."
                : "Este pedido já foi respondido.");
        }

        if (!ApprovalHash.IsSealed(view.Request))
        {
            throw new DomainException("O pedido não confere com o que a outra máquina mandou. Não responda.");
        }

        var response = new ApprovalResponse(
            view.Request.ApprovalId,
            view.Request.OperationId,
            view.Request.RequestHash,
            command.Decision,
            command.Decision == ApprovalDecision.Allow ? command.TypedConfirmation : null,
            command.Decision == ApprovalDecision.Allow && command.RememberForThisSession,
            timeProvider.GetUtcNow());

        var key = await signer.GetPublicKeyAsync(cancellationToken);
        var input = RemoteJws.SigningInput(response, key.Fingerprint, RemoteJws.ApprovalType);
        var signature = await signer.SignAsync(Encoding.ASCII.GetBytes(input), cancellationToken);

        return await remote.RespondApprovalAsync(command.ApprovalId, RemoteJws.Compose(input, signature), cancellationToken);
    }
}

/// <param name="TaskId">A tarefa do pedido, que o agente espera o sync trazer se ainda não chegou.</param>
public sealed record SendRemoteOperation(
    Guid TargetDeviceId,
    string Kind,
    JsonElement Payload,
    Guid? TaskId = null,
    IReadOnlyList<Guid>? RequiredEntities = null);

/// <summary>
/// Esta máquina, como controladora, pede uma operação a outra (ADR-086): monta
/// o pedido, assina com a própria chave e põe na fila. O servidor só guarda e
/// avisa; quem confere e decide é o agente de lá.
/// </summary>
public sealed class SendRemoteOperationHandler(
    IRemoteOperationsApi remote,
    IDeviceSigner signer,
    IDeviceIdentity identity,
    ISyncLocalStore sync,
    TimeProvider timeProvider)
{
    public async Task<RemoteOperationView> HandleAsync(SendRemoteOperation command, CancellationToken cancellationToken = default)
    {
        if ((await sync.ReadAsync(cancellationToken)).WorkspaceId is not { } workspace)
        {
            throw new DomainException("Conecte a sincronização para controlar outra máquina.");
        }

        var now = timeProvider.GetUtcNow();
        var request = new RemoteOperationRequest(
            Guid.CreateVersion7(now),
            workspace,
            command.TargetDeviceId,
            identity.DeviceId,
            command.Kind,
            command.Payload,
            command.TaskId,
            command.RequiredEntities ?? [],
            now,
            now + RemoteLimits.DefaultAcceptanceWindow);

        if (RemoteOperationRules.Problems(request, now) is [var problem, ..])
        {
            throw new DomainException($"O pedido não está completo ({problem}).");
        }

        var key = await signer.GetPublicKeyAsync(cancellationToken);
        var input = RemoteJws.SigningInput(request, key.Fingerprint, RemoteJws.OperationType);
        var signature = await signer.SignAsync(Encoding.ASCII.GetBytes(input), cancellationToken);

        return await remote.CreateAsync(RemoteJws.Compose(input, signature), cancellationToken);
    }
}
