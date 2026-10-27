using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.Remote;

/// <summary>Uma aprovação como a lista mostra: o que é, o que pede e até quando.</summary>
public sealed record ApprovalLine(ApprovalView View, string Title, string Detail)
{
    public string Summary => View.Request.Summary;

    public bool NeedsTyping => View.Request.TypedConfirmationHint is not null;
}

/// <summary>
/// Responder a uma aprovação do celular, na aba Remoto ou na tela da operação.
/// Permitir uma que pede o nome digitado (a produção crítica, ADR-090) pergunta
/// o nome antes, e um nome que não confere não sai daqui. A assinatura pede o
/// destravamento (<see cref="UnlockedDeviceSigner"/>).
/// </summary>
public sealed class ApprovalAnswers(IUseCaseRunner runner, IDialogService dialogs, ApprovalWatcher watcher, TimeProvider timeProvider)
{
    public ApprovalLine Line(ApprovalView view, string machine)
    {
        var expires = view.Request.ExpiresAt.ToOffset(timeProvider.LocalTimeZone.GetUtcOffset(view.Request.ExpiresAt));

        return new ApprovalLine(view, Title(view.Request.Kind), $"{machine} · sem resposta até as {expires:HH:mm}, é negado");
    }

    /// <summary><c>false</c>: a pessoa desistiu ou digitou outro nome; nada foi respondido.</summary>
    public async Task<bool> AnswerAsync(ApprovalView view, bool allow)
    {
        string? typed = null;

        if (allow && view.Request.TypedConfirmationHint is { } hint)
        {
            typed = (await dialogs.PromptAsync(
                "Confirme digitando",
                $"É produção crítica. Para permitir, digite o nome do banco: {hint}",
                string.Empty,
                200))?.Trim();

            if (typed is null)
            {
                return false;
            }

            if (!string.Equals(typed, hint, StringComparison.Ordinal))
            {
                await dialogs.AlertAsync("O nome não confere", $"Digite exatamente {hint}. Nada foi respondido.");
                return false;
            }
        }

        await runner.RunAsync<AnswerApprovalHandler, ApprovalView>(
            (handler, token) => handler.HandleAsync(
                new AnswerApproval(view.Request.ApprovalId, allow ? ApprovalDecision.Allow : ApprovalDecision.Deny, typed),
                token));
        await watcher.RefreshAsync();
        return true;
    }

    private static string Title(ApprovalKind kind) => kind switch
    {
        ApprovalKind.ToolUse => "O Claude quer usar uma ferramenta",
        ApprovalKind.CommandConfirmation => "Rodar um comando que pede confirmação",
        ApprovalKind.WorktreeRemoval => "Apagar um worktree",
        ApprovalKind.ProductionDatabase => "Copiar um banco de produção",
        _ => kind.ToString(),
    };
}
