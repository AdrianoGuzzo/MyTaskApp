using System.Text.Json;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Agents.ClaudeCode;

/// <summary>
/// Uma linha do <c>claude -p --output-format stream-json --verbose</c> (ADR-089)
/// em eventos do app. Cada linha é um objeto JSON com <c>type</c>:
/// <list type="bullet">
/// <item><c>system</c>/<c>init</c>: a conversa abriu, com o id, o modelo e as ferramentas;</item>
/// <item><c>assistant</c>: uma mensagem do modelo, com blocos <c>text</c>, <c>tool_use</c> e <c>thinking</c> (este não sai daqui);</item>
/// <item><c>user</c>: os <c>tool_result</c> que voltaram para o modelo;</item>
/// <item><c>result</c>: o fim, com turnos, custo e as permissões negadas.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Conferido no 2.1.310: uma falha de login chega como mensagem do
/// <c>assistant</c> (modelo <c>&lt;synthetic&gt;</c>) e um <c>result</c> com
/// <c>subtype: success</c> e <c>is_error: true</c>. Por isso o que vale é o
/// <c>is_error</c>, e não o <c>subtype</c>.
/// </para>
/// <para>
/// Tolerante de propósito: campo que falta vira vazio, tipo desconhecido e
/// linha que não é JSON são ignorados. Uma versão nova do Claude que acrescente
/// eventos não derruba a sessão; a que mude os conhecidos falha nos testes com
/// as saídas gravadas.
/// </para>
/// </remarks>
internal static class ClaudeStreamJsonReader
{
    /// <summary>O resumo da entrada de uma ferramenta: o bastante para a pessoa decidir, sem despejar um arquivo.</summary>
    internal const int MaxSummaryLength = 400;

    public static IReadOnlyList<HeadlessAgentEvent> Read(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{')
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            return Text(root, "type") switch
            {
                "system" when Text(root, "subtype") == "init" => [Initialized(root)],
                "assistant" => Assistant(root),
                "user" => ToolResults(root),
                "result" => [Result(root)],
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static HeadlessSessionInitialized Initialized(JsonElement root) =>
        new(
            Text(root, "session_id") ?? string.Empty,
            Text(root, "model"),
            [.. Strings(root, "tools")],
            Text(root, "claude_code_version"));

    private static IReadOnlyList<HeadlessAgentEvent> Assistant(JsonElement root)
    {
        var events = new List<HeadlessAgentEvent>();

        foreach (var block in Content(root))
        {
            switch (Text(block, "type"))
            {
                case "text" when Text(block, "text") is { Length: > 0 } text:
                    events.Add(new HeadlessText(text));
                    break;

                case "tool_use":
                    var name = Text(block, "name") ?? "?";
                    events.Add(new HeadlessToolUse(
                        Text(block, "id") ?? string.Empty,
                        name,
                        block.TryGetProperty("input", out var input) ? Summarize(name, input) : string.Empty));
                    break;
            }
        }

        return events;
    }

    private static IReadOnlyList<HeadlessAgentEvent> ToolResults(JsonElement root) =>
        [
            .. Content(root)
                .Where(block => Text(block, "type") == "tool_result")
                .Select(block => new HeadlessToolResult(
                    Text(block, "tool_use_id") ?? string.Empty,
                    block.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True,
                    Clip(ResultText(block))))
        ];

    private static HeadlessResult Result(JsonElement root) =>
        new(
            root.TryGetProperty("is_error", out var error) ? error.ValueKind == JsonValueKind.True : Text(root, "subtype") != "success",
            Text(root, "subtype"),
            Text(root, "result"),
            root.TryGetProperty("num_turns", out var turns) && turns.TryGetInt32(out var count) ? count : 0,
            root.TryGetProperty("total_cost_usd", out var cost) && cost.TryGetDecimal(out var usd) ? usd : null,
            [
                .. (root.TryGetProperty("permission_denials", out var denials) && denials.ValueKind == JsonValueKind.Array
                    ? denials.EnumerateArray().Select(denial => Text(denial, "tool_name")).OfType<string>()
                    : []),
            ],
            Text(root, "terminal_reason"));

    /// <summary>
    /// O que a pessoa precisa ver para aprovar: o comando do Bash, o arquivo
    /// do Read e do Edit, o padrão da busca. O resto, o JSON da entrada.
    /// </summary>
    internal static string Summarize(string tool, JsonElement input)
    {
        string? Field(string name) => input.ValueKind == JsonValueKind.Object ? Text(input, name) : null;

        var summary = tool switch
        {
            "Bash" or "PowerShell" => Field("command"),
            "Read" or "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => Field("file_path") ?? Field("notebook_path"),
            "Grep" => Field("pattern") is { } pattern ? Field("path") is { } path ? $"{pattern} em {path}" : pattern : null,
            "Glob" => Field("pattern"),
            "WebFetch" => Field("url"),
            "WebSearch" => Field("query"),
            _ => null,
        };

        return Clip(summary ?? (input.ValueKind == JsonValueKind.Undefined ? string.Empty : input.GetRawText()));
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join('\n', content.EnumerateArray().Select(item => Text(item, "text")).OfType<string>()),
            _ => string.Empty,
        };
    }

    private static IEnumerable<JsonElement> Content(JsonElement root) =>
        root.TryGetProperty("message", out var message)
        && message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("content", out var content)
        && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
            : [];

    private static IEnumerable<string> Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static string Clip(string text)
    {
        var line = text.Trim();
        return line.Length <= MaxSummaryLength ? line : $"{line[..(MaxSummaryLength - 1)]}…";
    }
}
