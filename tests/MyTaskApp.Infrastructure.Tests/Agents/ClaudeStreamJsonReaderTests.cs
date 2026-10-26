using System.Text.Json;
using MyTaskApp.Application.Agents;
using MyTaskApp.Infrastructure.Agents.ClaudeCode;

namespace MyTaskApp.Infrastructure.Tests.Agents;

/// <summary>
/// O <c>stream-json</c> do Claude em eventos do app (ADR-089), lido das saídas
/// em <c>StreamJson/</c> (o README de lá diz de onde veio cada uma).
/// </summary>
public class ClaudeStreamJsonReaderTests
{
    private static IReadOnlyList<HeadlessAgentEvent> ReadAll(string file) =>
        [.. File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Agents", "StreamJson", file)).SelectMany(ClaudeStreamJsonReader.Read)];

    [Fact]
    public void AReadQuestion_OpensTheConversation_UsesTheTool_AndEnds()
    {
        var events = ReadAll("read-and-answer.documented.jsonl");

        HeadlessAgentEvent[] expected =
            [
                new HeadlessSessionInitialized("3f9c2a7e-1b4d-4c8e-9a51-6d2e8f0b7c13", "claude-haiku-5-5", ["Read", "Grep", "Glob"], "2.1.310"),
                new HeadlessText("Vou ler o LEIA.md."),
                new HeadlessToolUse("toolu_01", "Read", "/work/eco-feature-1/LEIA.md"),
                new HeadlessToolResult("toolu_01", false, "1\tPalavra: ABACAXI-42"),
                new HeadlessText("ABACAXI-42"),
                new HeadlessResult(false, "success", "ABACAXI-42", 2, 0.0031m, [], "completed"),
            ];

        // As listas dentro dos records não comparam por valor: compara-se o JSON de cada um.
        events.Select(Json).Should().Equal(expected.Select(Json));
    }

    private static string Json(HeadlessAgentEvent item) => $"{item.GetType().Name} {JsonSerializer.Serialize(item, item.GetType())}";

    [Fact]
    public void ADeniedTool_ComesBackAsAnError_AndIsListedAtTheEnd_AndWhatIsNotAnEventIsSkipped()
    {
        var events = ReadAll("tool-denied.documented.jsonl");

        events.OfType<HeadlessToolUse>().Should().ContainSingle().Which.Summary.Should().Be("rm -rf build && dotnet test", "o Bash mostra o comando, não o JSON");
        events.OfType<HeadlessToolResult>().Should().ContainSingle().Which.Should().Be(new HeadlessToolResult("toolu_02", true, "Negado no celular: não apague a pasta build."));
        events.OfType<HeadlessResult>().Should().ContainSingle().Which.DeniedTools.Should().Equal("Bash");
        events.Should().HaveCount(5, "o aviso e o stream_event não viram nada");
    }

    /// <summary>Gravado do 2.1.310: o <c>subtype</c> diz <c>success</c>, e o que vale é o <c>is_error</c>.</summary>
    [Fact]
    public void AnExpiredLogin_IsAnError_EvenWithSubtypeSuccess()
    {
        var events = ReadAll("auth-expired.2.1.310.jsonl");

        events.OfType<HeadlessSessionInitialized>().Should().ContainSingle().Which.Version.Should().Be("2.1.310");
        var result = events.OfType<HeadlessResult>().Should().ContainSingle().Subject;
        result.Subtype.Should().Be("success");
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Failed to authenticate");
        result.TerminalReason.Should().Be("api_error");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ não fecha")]
    [InlineData("[1, 2]")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"hook_started\"}")]
    [InlineData("{\"type\":\"assistant\"}")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"content\":\"texto solto\"}}")]
    public void WhatIsNotAKnownEvent_IsIgnored_WithoutThrowing(string line) =>
        ClaudeStreamJsonReader.Read(line).Should().BeEmpty();

    [Fact]
    public void ALongInput_IsClipped_AndAnUnknownTool_ShowsItsJson()
    {
        var huge = JsonDocument.Parse(JsonSerializer.Serialize(new { command = new string('x', 2000) })).RootElement;
        var other = JsonDocument.Parse("{\"repo\":\"eco\"}").RootElement;

        ClaudeStreamJsonReader.Summarize("Bash", huge).Should().HaveLength(ClaudeStreamJsonReader.MaxSummaryLength).And.EndWith("…");
        ClaudeStreamJsonReader.Summarize("mcp__github__list", other).Should().Be("{\"repo\":\"eco\"}");
        ClaudeStreamJsonReader.Summarize("Grep", JsonDocument.Parse("{\"pattern\":\"TODO\",\"path\":\"src\"}").RootElement).Should().Be("TODO em src");
    }
}
