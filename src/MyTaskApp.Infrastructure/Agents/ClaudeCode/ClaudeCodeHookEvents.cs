using System.Text.Json;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Agents.ClaudeCode;

/// <summary>
/// O JSON de um hook do Claude Code → <see cref="AgentEvent"/> (ADR-036). É o
/// único lugar do app que conhece nome de evento do Claude.
/// </summary>
/// <remarks>
/// Estado sai só dos campos estruturados — <c>hook_event_name</c>,
/// <c>notification_type</c>, <c>tool_name</c> —, nunca do texto: "terminou" ou
/// um "?" no fim da resposta não dizem nada. O texto (a pergunta, a última
/// resposta) vai junto só para o aviso mostrar.
/// </remarks>
internal static class ClaudeCodeHookEvents
{
    /// <summary>Os avisos que param o Claude esperando alguém.</summary>
    private static readonly HashSet<string> NeedsInputNotifications = new(StringComparer.Ordinal)
    {
        "permission_prompt",
        "elicitation_dialog",
        "elicitation_url_dialog",
        "agent_needs_input",
    };

    /// <summary><c>null</c> quando o JSON não é um evento de hook.</summary>
    public static AgentEvent? Translate(
        JsonElement payload,
        Guid agentSessionId,
        Guid? taskId,
        DateTimeOffset receivedAt)
    {
        if (payload.ValueKind != JsonValueKind.Object || Text(payload, "hook_event_name") is not { } name)
        {
            return null;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in (string[])["source", "reason", "notification_type", "tool_name", "error_type", "permission_mode"])
        {
            if (Text(payload, key) is { } value)
            {
                metadata[key] = value;
            }
        }

        var (type, message) = Classify(name, payload);

        return new AgentEvent(
            type,
            agentSessionId,
            taskId,
            Text(payload, "session_id"),
            Text(payload, "cwd"),
            message,
            receivedAt,
            name,
            metadata);
    }

    private static (AgentEventType Type, string? Message) Classify(string name, JsonElement payload) => name switch
    {
        "SessionStart" => (AgentEventType.SessionStarted, null),
        "UserPromptSubmit" or "PostToolUse" or "SubagentStart" => (AgentEventType.Working, null),
        "PreToolUse" => Text(payload, "tool_name") switch
        {
            "AskUserQuestion" => (AgentEventType.NeedsUserInput, FirstQuestion(payload) ?? "O Claude tem uma pergunta para você."),
            "ExitPlanMode" => (AgentEventType.NeedsUserInput, "O plano está pronto para a sua aprovação."),
            _ => (AgentEventType.Working, null),
        },
        "Notification" => NeedsInputNotifications.Contains(Text(payload, "notification_type") ?? string.Empty)
            ? (AgentEventType.NeedsUserInput, Text(payload, "message") ?? "O Claude precisa de você.")
            : (AgentEventType.Notification, Text(payload, "message")),
        "Stop" => (AgentEventType.ResponseCompleted, Text(payload, "last_assistant_message")),
        "StopFailure" => (AgentEventType.SessionFailed, DescribeFailure(payload)),
        "TaskCompleted" => (AgentEventType.TaskCompleted, Text(payload, "task_subject") ?? Text(payload, "subject")),
        "SessionEnd" => (AgentEventType.SessionStopped, null),
        _ => (AgentEventType.Notification, null),
    };

    /// <summary>A primeira pergunta do <c>AskUserQuestion</c> — o que o aviso mostra.</summary>
    private static string? FirstQuestion(JsonElement payload) =>
        payload.TryGetProperty("tool_input", out var input)
        && input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("questions", out var questions)
        && questions.ValueKind == JsonValueKind.Array
        && questions.GetArrayLength() > 0
            ? Text(questions[0], "question")
            : null;

    /// <summary>O <c>error_type</c> do <c>StopFailure</c>, em português.</summary>
    private static string DescribeFailure(JsonElement payload) => Text(payload, "error_type") switch
    {
        "rate_limit" => "Limite de uso do Claude atingido.",
        "overloaded" => "A API do Claude está sobrecarregada.",
        "authentication_failed" or "oauth_org_not_allowed" => "O Claude não conseguiu se autenticar.",
        "billing_error" or "account_on_hold" => "Problema na conta ou na cobrança do Claude.",
        "server_error" => "Erro no servidor da API do Claude.",
        "max_output_tokens" => "A resposta passou do tamanho máximo.",
        { } other => $"A resposta terminou em erro ({other}).",
        null => "A resposta terminou em erro.",
    };

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
