using System.Text.Json;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>
/// O que o "Permitir nesta conversa" cobre (ADR-086, aprovação de ferramenta):
/// no shell, o mesmo comando, letra por letra; nas outras ferramentas, a
/// ferramenta. Permitir <c>dotnet test</c> não libera <c>dotnet test; rm -rf .</c>
/// nem outro comando qualquer.
/// </summary>
public static class HeadlessToolScope
{
    /// <summary>O comando composto ou com redirecionamento faz mais que o começo dele diz: é sempre perguntado.</summary>
    private static readonly string[] Compound = ["&&", "||", ";", "|", "`", "$(", "&", ">", "<", "\n", "\r"];

    private static readonly HashSet<string> Shells = new(StringComparer.Ordinal) { "Bash", "PowerShell" };

    /// <summary>
    /// A chave do que fica permitido, ou nulo se este pedido não pode ser
    /// lembrado (o shell composto, ou sem comando).
    /// </summary>
    public static string? RememberKey(string tool, JsonElement input)
    {
        if (string.IsNullOrWhiteSpace(tool))
        {
            return null;
        }

        if (!Shells.Contains(tool))
        {
            return tool;
        }

        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("command", out var command)
            || command.ValueKind != JsonValueKind.String
            || command.GetString() is not { } text
            || string.IsNullOrWhiteSpace(text)
            || Compound.Any(text.Contains))
        {
            return null;
        }

        return $"{tool}: {string.Join(' ', text.Split(' ', '\t').Where(part => part.Length > 0))}";
    }
}

/// <summary>
/// Os textos de uma aprovação que as duas telas de controle mostram (o desktop
/// e o celular), lidos dos detalhes que o agente mandou. Um agente antigo não
/// manda <c>canRemember</c>: o botão não aparece, e nada muda para ele.
/// </summary>
public static class RemoteApprovalTexts
{
    public const string RememberButton = "Permitir nesta conversa";

    /// <summary>O agente disse que este pedido pode ser lembrado na conversa.</summary>
    public static bool CanRemember(ApprovalRequest request) =>
        request.Kind == ApprovalKind.ToolUse
        && request.TypedConfirmationHint is null
        && request.Details.ValueKind == JsonValueKind.Object
        && request.Details.TryGetProperty("canRemember", out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>O que "Permitir nesta conversa" deixa passar sem perguntar de novo; nulo se não pode.</summary>
    public static string? RememberLabel(ApprovalRequest request)
    {
        if (!CanRemember(request))
        {
            return null;
        }

        return request.Details.TryGetProperty("rememberAs", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } scope
            ? $"Até o fim da conversa, sem perguntar de novo: {scope}"
            : "Até o fim da conversa, sem perguntar de novo.";
    }
}
