using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Tests.Agents;

/// <summary>O texto do campo "Parâmetros" virando a lista que vai ao processo.</summary>
public class AgentArgumentsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_IsNoArgument(string? text)
    {
        AgentArguments.Parse(text).Should().BeEmpty();
    }

    [Fact]
    public void Spaces_SeparateTheArguments_HoweverManyThereAre()
    {
        AgentArguments.Parse("  --dangerously-skip-permissions   --model  opus ")
            .Should().Equal("--dangerously-skip-permissions", "--model", "opus");
    }

    [Fact]
    public void Quotes_KeepAnArgumentWithSpacesTogether()
    {
        AgentArguments.Parse("--append-system-prompt \"seja breve\" --verbose")
            .Should().Equal("--append-system-prompt", "seja breve", "--verbose");
    }

    [Fact]
    public void EmptyQuotes_AreAnEmptyArgument()
    {
        AgentArguments.Parse("--flag \"\"").Should().Equal("--flag", "");
    }

    /// <summary>Nada passa por shell: o que seria operador chega como texto.</summary>
    [Fact]
    public void ShellOperators_AreJustText()
    {
        AgentArguments.Parse("--x && del").Should().Equal("--x", "&&", "del");
    }

    [Fact]
    public void AnUnclosedQuote_IsRefused()
    {
        FluentActions.Invoking(() => AgentArguments.Parse("--append-system-prompt \"seja breve"))
            .Should().Throw<DomainException>()
            .WithMessage("Aspas sem fechar nos parâmetros do agente.");
    }

    [Fact]
    public void TooLong_IsRefused()
    {
        FluentActions.Invoking(() => AgentArguments.Parse(new string('a', AgentArguments.MaxLength + 1)))
            .Should().Throw<DomainException>();
    }
}
