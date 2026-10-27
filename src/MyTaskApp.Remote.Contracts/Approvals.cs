using System.Security.Cryptography;
using System.Text.Json;

namespace MyTaskApp.Remote.Contracts;

public enum ApprovalKind
{
    /// <summary>Uma ferramenta do Claude fora do perfil (<c>--permission-prompt-tool</c>).</summary>
    ToolUse,

    /// <summary>Um comando com "pedir confirmação" (ADR-051).</summary>
    CommandConfirmation,

    WorktreeRemoval,

    /// <summary>A cópia de uma origem de produção (ADR-090).</summary>
    ProductionDatabase,
}

/// <summary>O agente pede um humano. Sem resposta até <paramref name="ExpiresAt"/>, vale negar.</summary>
/// <param name="Summary">Legível e mascarado: "Rodar Bash: dotnet test".</param>
/// <param name="Details">O pedido inteiro, que a tela mostra (RS-35).</param>
/// <param name="TypedConfirmationHint">"Digite o nome do banco de origem", quando a política exige.</param>
/// <param name="RequestHash">SHA-256 do pedido: amarra a resposta ao que foi mostrado.</param>
public sealed record ApprovalRequest(
    Guid ApprovalId,
    Guid OperationId,
    ApprovalKind Kind,
    string Summary,
    JsonElement Details,
    string? TypedConfirmationHint,
    DateTimeOffset ExpiresAt,
    string RequestHash);

public enum ApprovalDecision
{
    Allow,
    Deny,
}

/// <summary>Assinada pelo controlador, como o pedido da operação, e de uso único.</summary>
/// <param name="RememberForThisSession">Só em <see cref="ApprovalKind.ToolUse"/>, e só para a mesma ferramenta e padrão.</param>
public sealed record ApprovalResponse(
    Guid ApprovalId,
    Guid OperationId,
    string RequestHash,
    ApprovalDecision Decision,
    string? TypedConfirmation,
    bool RememberForThisSession,
    DateTimeOffset RespondedAt);

public enum ApprovalState
{
    Pending,
    Allowed,
    Denied,

    /// <summary>Passou de <see cref="ApprovalRequest.ExpiresAt"/> sem resposta: vale negar.</summary>
    Expired,
}

/// <summary>
/// Uma aprovação como o servidor a guarda (<c>GET /api/v1/remote/approvals/{id}</c>).
/// A resposta vai assinada, como veio do controlador: o agente confere a
/// assinatura com as chaves em que confia, e não a palavra do servidor.
/// </summary>
/// <param name="RespondedByDeviceId">Quem respondeu, conferido pelo servidor com a chave ativa dele.</param>
public sealed record ApprovalView(
    ApprovalRequest Request,
    Guid TargetDeviceId,
    ApprovalState State,
    SignedEnvelope? Response,
    Guid? RespondedByDeviceId,
    DateTimeOffset? RespondedAt);

/// <summary>
/// O <see cref="ApprovalRequest.RequestHash"/>: SHA-256, em hexadecimal, do
/// pedido inteiro com o hash vazio. O agente calcula ao pedir e compara com o
/// que volta na resposta assinada; se o servidor mudou o que a pessoa viu, os
/// hashes não batem.
/// </summary>
public static class ApprovalHash
{
    public static string Of(ApprovalRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(request with { RequestHash = string.Empty }, RemoteJson.Options)));

    /// <summary>O pedido com o hash calculado.</summary>
    public static ApprovalRequest Seal(ApprovalRequest request) => request with { RequestHash = Of(request) };

    public static bool IsSealed(ApprovalRequest request) => request.RequestHash == Of(request);
}
