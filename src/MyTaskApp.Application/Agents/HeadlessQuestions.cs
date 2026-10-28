using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyTaskApp.Application.Agents;

/// <summary>Uma opção da pergunta, como o Claude mandou.</summary>
public sealed record HeadlessQuestionOption(string Label, string? Description);

/// <summary>Uma pergunta do Claude: o texto, o rótulo curto, as opções e se aceita mais de uma.</summary>
public sealed record HeadlessQuestion(string Text, string? Header, IReadOnlyList<HeadlessQuestionOption> Options, bool MultiSelect);

/// <summary>
/// A pergunta do Claude (a ferramenta <c>AskUserQuestion</c>) no modo sem
/// terminal (ADR-093). Chega ao MCP de aprovação como qualquer ferramenta; a
/// resposta volta permitindo com <c>updatedInput</c>: as perguntas como
/// vieram e <c>answers</c>, do texto de cada pergunta para a opção escolhida
/// (mais de uma, separadas por ", "), ou o texto livre da pessoa.
/// </summary>
/// <remarks>
/// A pergunta vai ao controlador mascarada (<c>SensitiveText</c>) e cortada,
/// então a resposta assinada não usa o texto: é uma lista, na ordem das
/// perguntas, com o rótulo mostrado de cada opção escolhida. O agente traduz
/// de volta para o texto e os rótulos originais antes de responder ao Claude.
/// </remarks>
public static class HeadlessQuestions
{
    public const string Tool = "AskUserQuestion";

    /// <summary>O <c>kind</c> nos detalhes da aprovação: o controlador mostra as opções.</summary>
    public const string Kind = "question";

    /// <summary>O Claude manda de 1 a 4 perguntas, com 2 a 4 opções; mais que isso não é a forma esperada.</summary>
    public const int MaxQuestions = 4;

    public const int MaxOptions = 4;

    /// <summary>O teto de uma resposta: a pergunta pede uma escolha, não um texto longo.</summary>
    public const int MaxAnswerLength = 500;

    private const int MaxQuestionLength = 500;

    private const int MaxLabelLength = 120;

    private const int MaxDescriptionLength = 300;

    private const string Separator = ", ";

    /// <summary>
    /// As perguntas da entrada, que vem do modelo e não é confiável (ou dos
    /// detalhes da aprovação, que têm a mesma forma). Nulo: não tem a forma
    /// esperada, e a pergunta não vai para ninguém.
    /// </summary>
    public static IReadOnlyList<HeadlessQuestion>? Read(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array
            || questions.GetArrayLength() is 0 or > MaxQuestions)
        {
            return null;
        }

        var read = new List<HeadlessQuestion>();

        foreach (var question in questions.EnumerateArray())
        {
            if (question.ValueKind != JsonValueKind.Object
                || Text(question, "question") is not { } text
                || !question.TryGetProperty("options", out var options)
                || options.ValueKind != JsonValueKind.Array
                || options.GetArrayLength() is 0 or > MaxOptions)
            {
                return null;
            }

            var choices = new List<HeadlessQuestionOption>();

            foreach (var option in options.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object || Text(option, "label") is not { } label)
                {
                    return null;
                }

                choices.Add(new HeadlessQuestionOption(label, Text(option, "description")));
            }

            var multiSelect = question.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True;
            read.Add(new HeadlessQuestion(text, Text(question, "header"), choices, multiSelect));
        }

        // Duas perguntas com o mesmo texto: o Claude lê a resposta pelo texto, e não saberia de qual é.
        return read.Select(question => question.Text).Distinct(StringComparer.Ordinal).Count() == read.Count ? read : null;
    }

    /// <summary>As perguntas como vão ao controlador: cada texto passado por <paramref name="mask"/> e cortado.</summary>
    public static IReadOnlyList<HeadlessQuestion> Shown(IReadOnlyList<HeadlessQuestion> questions, Func<string, string> mask) =>
    [
        .. questions.Select(question => new HeadlessQuestion(
            Clip(mask(question.Text), MaxQuestionLength),
            question.Header is { } header ? Clip(mask(header), MaxLabelLength) : null,
            [
                .. question.Options.Select(option => new HeadlessQuestionOption(
                    Clip(mask(option.Label), MaxLabelLength),
                    option.Description is { } description ? Clip(mask(description), MaxDescriptionLength) : null)),
            ],
            question.MultiSelect)),
    ];

    /// <summary>Os detalhes da aprovação de uma pergunta: as perguntas na forma da entrada do Claude.</summary>
    public static Dictionary<string, object?> Details(Guid sessionId, string? toolUseId, IReadOnlyList<HeadlessQuestion> shown) => new()
    {
        ["kind"] = Kind,
        ["agentSessionId"] = sessionId,
        ["tool"] = Tool,
        ["toolUseId"] = toolUseId,
        ["canRemember"] = false,
        ["questions"] = shown.Select(question => new Dictionary<string, object?>
        {
            ["question"] = question.Text,
            ["header"] = question.Header,
            ["multiSelect"] = question.MultiSelect,
            ["options"] = question.Options.Select(option => new Dictionary<string, object?>
            {
                ["label"] = option.Label,
                ["description"] = option.Description,
            }).ToList(),
        }).ToList(),
    };

    /// <summary>As perguntas dos detalhes de uma aprovação; nulo se ela não é uma pergunta.</summary>
    public static IReadOnlyList<HeadlessQuestion>? FromDetails(JsonElement details) =>
        details.ValueKind == JsonValueKind.Object
        && details.TryGetProperty("kind", out var kind)
        && kind.ValueKind == JsonValueKind.String
        && kind.GetString() == Kind
            ? Read(details)
            : null;

    /// <summary>A resposta de uma pergunta com várias opções escolhidas.</summary>
    public static string Join(IEnumerable<string> labels) => string.Join(Separator, labels);

    /// <summary>As respostas como vão na resposta assinada: uma por pergunta, na ordem.</summary>
    public static string Sign(IReadOnlyList<string> answers) => JsonSerializer.Serialize(answers);

    /// <summary>
    /// As respostas que voltaram assinadas, conferidas: uma por pergunta, nem
    /// vazia nem longa. Nulo: não respondem às perguntas feitas.
    /// </summary>
    public static IReadOnlyList<string>? ReadAnswers(string? signed, int questions)
    {
        if (string.IsNullOrWhiteSpace(signed))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(signed) is { } answers
                && answers.Count == questions
                && answers.All(answer => !string.IsNullOrWhiteSpace(answer) && answer.Length <= MaxAnswerLength)
                ? answers
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// As respostas para o Claude: pelo texto original de cada pergunta, e o
    /// rótulo mostrado de cada opção escolhida trocado pelo original. O que
    /// não é rótulo de opção vai como a pessoa escreveu.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ForClaude(
        IReadOnlyList<HeadlessQuestion> asked,
        IReadOnlyList<HeadlessQuestion> shown,
        IReadOnlyList<string> answers)
    {
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < asked.Count; index++)
        {
            var options = shown[index].Options;
            var parts = answers[index].Split(Separator).Select(part =>
            {
                var option = options.ToList().FindIndex(item => string.Equals(item.Label, part, StringComparison.Ordinal));
                return option >= 0 ? asked[index].Options[option].Label : part;
            });

            mapped[asked[index].Text] = Join(parts);
        }

        return mapped;
    }

    /// <summary>A entrada com as respostas, para permitir com <c>updatedInput</c>: as perguntas ficam como vieram.</summary>
    public static JsonElement Answer(JsonElement input, IReadOnlyDictionary<string, string> answers)
    {
        var updated = JsonNode.Parse(input.GetRawText())?.AsObject() ?? [];
        var map = new JsonObject();

        foreach (var (question, answer) in answers)
        {
            map[question] = answer;
        }

        updated["answers"] = map;
        return JsonSerializer.SerializeToElement(updated);
    }

    /// <summary>A entrada respondida por quem escolhe (o teste com o Claude de verdade).</summary>
    public static JsonElement Answer(JsonElement input, Func<HeadlessQuestion, string> choose) =>
        Answer(input, (Read(input) ?? []).ToDictionary(question => question.Text, choose, StringComparer.Ordinal));

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string Clip(string text, int length) => text.Length <= length ? text : $"{text[..(length - 1)]}…";
}
