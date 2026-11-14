using System.Text.Json;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Agents.Codex;

internal static class CodexHookEvents
{
    public static AgentEvent? Translate(JsonElement payload, Guid sessionId, Guid? taskId, DateTimeOffset receivedAt)
    {
        string? Text(string name) => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (payload.ValueKind != JsonValueKind.Object || Text("hook_event_name") is not { } name)
            return null;
        // Os hooks dos subagentes não representam a atividade da conversa principal.
        if (Text("agent_id") is not null)
            return null;
        var type = name switch
        {
            "SessionStart" => AgentEventType.SessionStarted,
            "SessionEnd" => AgentEventType.SessionStopped,
            "Stop" => AgentEventType.ResponseCompleted,
            "PermissionRequest" => AgentEventType.NeedsUserInput,
            "PreToolUse" when Text("tool_name") is "AskUserQuestion" or "request_user_input" => AgentEventType.NeedsUserInput,
            "UserPromptSubmit" or "PreToolUse" or "PostToolUse" => AgentEventType.Working,
            _ => AgentEventType.Notification,
        };
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "tool_name", "permission_mode", "reason" })
            if (Text(key) is { } value)
                metadata[key] = value;
        var message = type == AgentEventType.NeedsUserInput ? "O Codex precisa de você."
            : name == "Stop" ? Text("last_assistant_message") : null;
        return new AgentEvent(type, sessionId, taskId, Text("session_id"), Text("cwd"), message, receivedAt, name, metadata);
    }
}
