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
