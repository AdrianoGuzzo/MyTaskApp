using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Domain.Tests.Commands;

/// <summary>Os parâmetros tipados de um comando rápido: obrigatório, opcional, padrão (ADR-051).</summary>
public class CommandParameterSpecTests
{
    [Fact]
    public void ARequiredBlank_IsMissing()
    {
        var evaluation = new CommandParameterSpec("project", "Projeto").Evaluate("   ");

        evaluation.IsValid.Should().BeFalse();
        evaluation.IsMissing.Should().BeTrue();
        evaluation.Error.Should().Be("Informe Projeto.");
    }

    [Fact]
    public void AnOptionalBlank_IsEmpty()
    {
        var evaluation = new CommandParameterSpec("extra", IsRequired: false).Evaluate(null);

        evaluation.IsValid.Should().BeTrue();
        evaluation.Value.Should().BeEmpty();
    }

    [Fact]
    public void ABlank_TakesTheDefault()
    {
        var evaluation = new CommandParameterSpec("profile", DefaultValue: "Development").Evaluate("");

        evaluation.Value.Should().Be("Development");
        evaluation.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ATypedValue_WinsOverTheDefault_AndIsTrimmed()
    {
        new CommandParameterSpec("profile", DefaultValue: "Development").Evaluate("  Staging ").Value
            .Should().Be("Staging");
    }

    [Theory]
    [InlineData("8080", true)]
    [InlineData("1.5", true)]
    [InlineData("1,5", false)]
    [InlineData("oito", false)]
    public void Numbers_UseTheInvariantCulture(string typed, bool valid)
    {
        new CommandParameterSpec("port", Type: CommandParameterType.Number).Evaluate(typed).IsValid
            .Should().Be(valid);
    }

    [Fact]
    public void AChoiceOutsideTheOptions_IsInvalid()
    {
        var spec = new CommandParameterSpec("profile", "Perfil", CommandParameterType.Choice, Options: ["Development", "Staging"]);

        spec.Evaluate("Production").Error.Should().Contain("Development, Staging");
        spec.Evaluate("Staging").IsValid.Should().BeTrue();
    }

    [Fact]
    public void AChoiceWithoutOptions_IsRejected()
    {
        var normalize = () => new CommandParameterSpec("profile", Type: CommandParameterType.Choice).Normalized();

        normalize.Should().Throw<DomainException>();
    }

    [Fact]
    public void ADefaultOutsideTheOptions_IsRejected()
    {
        var normalize = () => new CommandParameterSpec(
            "profile", Type: CommandParameterType.Choice, DefaultValue: "Prod", Options: ["Dev"]).Normalized();

        normalize.Should().Throw<DomainException>().WithMessage("*padrão*");
    }

    [Fact]
    public void ANumberDefaultThatIsNotANumber_IsRejected()
    {
        var normalize = () => new CommandParameterSpec("port", Type: CommandParameterType.Number, DefaultValue: "abc").Normalized();

        normalize.Should().Throw<DomainException>();
    }

    [Fact]
    public void Normalized_TrimsAndDropsEmptyAndRepeatedOptions_AndForgetsOptionsOutsideAChoice()
    {
        new CommandParameterSpec("p", " ", CommandParameterType.Choice, Options: [" a ", "", "a", "b"]).Normalized()
            .Should().BeEquivalentTo(new CommandParameterSpec("p", null, CommandParameterType.Choice, null, true, ["a", "b"]));

        new CommandParameterSpec("p", Options: ["a"]).Normalized().Options.Should().BeNull();
    }

    [Theory]
    [InlineData("1abc")]
    [InlineData("com espaço")]
    [InlineData("")]
    public void AnInvalidName_IsRejected(string name)
    {
        var normalize = () => new CommandParameterSpec(name).Normalized();

        normalize.Should().Throw<DomainException>();
    }
}
