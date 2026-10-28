using System.Text.Json;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Application.Tests.Agents;

/// <summary>
/// A pergunta do Claude (ADR-093): a entrada que vem do modelo, a forma que vai
/// ao controlador e as respostas que voltam assinadas, traduzidas para o Claude.
/// </summary>
public sealed class HeadlessQuestionsTests
{
    private const string Color = """
        {"questions":[
          {"question":"Qual cor?","header":"Cor","multiSelect":false,"options":[{"label":"Azul","description":"Frio"},{"label":"Verde"}]},
          {"question":"Quais testes?","multiSelect":true,"options":[{"label":"Unidade"},{"label":"Integração"},{"label":"Ponta a ponta"}]}
        ]}
        """;

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TheQuestions_AreReadWithTheirOptions()
    {
        var questions = HeadlessQuestions.Read(Input(Color))!;

        questions.Should().HaveCount(2);
        questions[0].Should().BeEquivalentTo(new HeadlessQuestion("Qual cor?", "Cor", [new("Azul", "Frio"), new("Verde", null)], false));
        questions[1].MultiSelect.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"questions":[]}""")]
    [InlineData("""{"questions":"Qual?"}""")]
    [InlineData("""{"questions":[{"question":"Qual?","options":[]}]}""")]
    [InlineData("""{"questions":[{"question":"","options":[{"label":"A"}]}]}""")]
    [InlineData("""{"questions":[{"question":"Qual?","options":[{"label":"A"},{"label":"B"},{"label":"C"},{"label":"D"},{"label":"E"}]}]}""")]
    [InlineData("""{"questions":[{"question":"Qual?","options":[{"label":"A"}]},{"question":"Qual?","options":[{"label":"B"}]}]}""")]
    [InlineData("""[]""")]
    public void AnInputOutOfShape_IsNotAQuestion(string json) =>
        HeadlessQuestions.Read(Input(json)).Should().BeNull();

    [Fact]
    public void WhatGoesToTheController_IsMasked_AndReadsBackAsTheSameQuestions()
    {
        var asked = HeadlessQuestions.Read(Input(Color))!;

        var shown = HeadlessQuestions.Shown(asked, text => text.Replace("Azul", "***", StringComparison.Ordinal));
        var details = JsonSerializer.SerializeToElement(HeadlessQuestions.Details(Guid.CreateVersion7(), "tool-1", shown));

        HeadlessQuestions.FromDetails(details).Should().BeEquivalentTo(shown);
        shown[0].Options[0].Label.Should().Be("***");
        details.GetProperty("canRemember").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void TheDetailsOfAnotherApproval_AreNotAQuestion() =>
        HeadlessQuestions.FromDetails(Input("""{"tool":"Bash","questions":[{"question":"Qual?","options":[{"label":"A"}]}]}""")).Should().BeNull();

    [Fact]
    public void TheSignedAnswers_GoToClaudeByTheOriginalText_WithTheOriginalLabels()
    {
        var asked = HeadlessQuestions.Read(Input(Color))!;
        var shown = HeadlessQuestions.Shown(asked, text => text.Replace("Azul", "***", StringComparison.Ordinal));
        var signed = HeadlessQuestions.Sign(["***", HeadlessQuestions.Join(["Unidade", "Ponta a ponta"])]);

        var answers = HeadlessQuestions.ReadAnswers(signed, asked.Count)!;
        var forClaude = HeadlessQuestions.ForClaude(asked, shown, answers);
        var updated = HeadlessQuestions.Answer(Input(Color), forClaude);

        forClaude.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Qual cor?"] = "Azul",
            ["Quais testes?"] = "Unidade, Ponta a ponta",
        });
        updated.GetProperty("answers").GetProperty("Qual cor?").GetString().Should().Be("Azul");
        updated.GetProperty("questions").GetArrayLength().Should().Be(2, "as perguntas voltam como vieram");
    }

    [Fact]
    public void AWrittenAnswer_GoesAsWritten()
    {
        var asked = HeadlessQuestions.Read(Input(Color))!;

        var forClaude = HeadlessQuestions.ForClaude(asked, asked, ["Roxo, na verdade", "Unidade"]);

        forClaude["Qual cor?"].Should().Be("Roxo, na verdade");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("""["Azul"]""")]
    [InlineData("""["Azul",""]""")]
    [InlineData("""{"Qual cor?":"Azul"}""")]
    public void AnswersThatDoNotAnswerEveryQuestion_AreNoAnswer(string? signed) =>
        HeadlessQuestions.ReadAnswers(signed, 2).Should().BeNull();

    [Fact]
    public void AnAnswerTooLong_IsNoAnswer() =>
        HeadlessQuestions.ReadAnswers(HeadlessQuestions.Sign([new string('a', HeadlessQuestions.MaxAnswerLength + 1)]), 1).Should().BeNull();
}
