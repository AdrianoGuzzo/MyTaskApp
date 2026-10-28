using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Remote;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.Remote;

/// <summary>Uma aprovação como a lista mostra: o que é, o que pede e até quando.</summary>
public sealed record ApprovalLine(ApprovalView View, string Title, string Detail)
{
    public string Summary => View.Request.Summary;

    public bool NeedsTyping => View.Request.TypedConfirmationHint is not null;

    /// <summary>"Permitir nesta conversa": só quando o agente diz o que ficaria permitido.</summary>
    public bool CanRemember => RemoteApprovalTexts.CanRemember(View.Request);

    public string? RememberLabel => RemoteApprovalTexts.RememberLabel(View.Request);

    /// <summary>Uma pergunta do Claude (ADR-093): permitir é responder.</summary>
    public bool IsQuestion => RemoteApprovalTexts.Questions(View.Request) is not null;

    public string AllowLabel => RemoteApprovalTexts.AllowLabel(View.Request);

    public string DenyLabel => RemoteApprovalTexts.DenyLabel(View.Request);

    /// <summary>As perguntas, uma por linha, com as opções.</summary>
    public string? QuestionText => RemoteApprovalTexts.Questions(View.Request) is { } questions
        ? string.Join(Environment.NewLine, questions.Select(RemoteApprovalTexts.Describe))
        : null;
}

/// <summary>
/// Responder a uma aprovação do celular, na aba Remoto ou na tela da operação.
/// Permitir uma que pede o nome digitado (a produção crítica, ADR-090) pergunta
/// o nome antes, e um nome que não confere não sai daqui. A assinatura pede o
/// destravamento (<see cref="UnlockedDeviceSigner"/>).
/// </summary>
public sealed class ApprovalAnswers(IUseCaseRunner runner, IDialogService dialogs, ApprovalWatcher watcher, TimeProvider timeProvider)
{
    /// <summary>A escolha que abre a resposta escrita, no fim das opções.</summary>
    public const string OtherAnswer = "Outra resposta…";

    /// <summary>A escolha que fecha uma pergunta de mais de uma opção.</summary>
    public const string Done = "Pronto";

    public ApprovalLine Line(ApprovalView view, string machine)
    {
        var expires = view.Request.ExpiresAt.ToOffset(timeProvider.LocalTimeZone.GetUtcOffset(view.Request.ExpiresAt));
        var title = RemoteApprovalTexts.Questions(view.Request) is null ? Title(view.Request.Kind) : RemoteApprovalTexts.QuestionTitle;

        return new ApprovalLine(view, title, $"{machine} · sem resposta até as {expires:HH:mm}, é negado");
    }

    /// <summary><c>false</c>: a pessoa desistiu ou digitou outro nome; nada foi respondido.</summary>
    /// <param name="remember">Permitir também o mesmo pedido até o fim da conversa, se o agente disse que pode.</param>
    public async Task<bool> AnswerAsync(ApprovalView view, bool allow, bool remember = false)
    {
        string? typed = null;
        List<string>? answers = null;

        // A pergunta do Claude: uma escolha por pergunta, ou a resposta escrita.
        if (allow && RemoteApprovalTexts.Questions(view.Request) is { } questions)
        {
            answers = [];

            foreach (var question in questions)
            {
                if (await ChooseAsync(question) is not { } answer)
                {
                    return false;
                }

                answers.Add(answer);
            }
        }

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
                new AnswerApproval(
                    view.Request.ApprovalId,
                    allow ? ApprovalDecision.Allow : ApprovalDecision.Deny,
                    typed,
                    allow && remember && RemoteApprovalTexts.CanRemember(view.Request),
                    answers),
                token));
        await watcher.RefreshAsync();
        return true;
    }

    /// <summary>
    /// A resposta a uma pergunta: a opção escolhida, ou a escrita. Com mais de
    /// uma valendo, cada escolha marca ou desmarca, até "Pronto". Nulo: a
    /// pessoa desistiu.
    /// </summary>
    private async Task<string?> ChooseAsync(HeadlessQuestion question)
    {
        var labels = question.Options.Select(option => option.Label).ToList();
        var chosen = new List<string>();

        while (true)
        {
            var items = question.MultiSelect
                ? [.. labels.Select(label => chosen.Contains(label) ? $"✓ {label}" : label), OtherAnswer, Done]
                : new List<string>([.. question.Options.Select(option => option.Description is { } description ? $"{option.Label} — {description}" : option.Label), OtherAnswer]);

            if (await dialogs.ChooseAsync(question.Text, items) is not { } index)
            {
                return null;
            }

            if (index < labels.Count)
            {
                if (!question.MultiSelect)
                {
                    return labels[index];
                }

                if (!chosen.Remove(labels[index]))
                {
                    chosen.Add(labels[index]);
                }

                continue;
            }

            if (items[index] == Done)
            {
                if (chosen.Count > 0)
                {
                    return HeadlessQuestions.Join(labels.Where(chosen.Contains));
                }

                continue;
            }

            var written = (await dialogs.PromptAsync(question.Text, "Escreva a resposta", string.Empty, HeadlessQuestions.MaxAnswerLength))?.Trim();

            if (!string.IsNullOrEmpty(written))
            {
                return written;
            }
        }
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
