using MyTaskApp.Application.Development;

namespace MyTaskApp.Application.Tests.Development;

/// <summary>O nome da nova branch e a sugestão a partir do título (ADR-027).</summary>
public class GitBranchNameTests
{
    [Theory]
    [InlineData("feature/123-corrigir-animais")]
    [InlineData("bugfix/456-correcao-vacinacao")]
    [InlineData("main")]
    [InlineData("release/6.2")]
    [InlineData("user/adriano/x_y")]
    public void ValidNames_Pass(string name) => GitBranchName.Validate(name).Should().BeNull();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-feature")]
    [InlineData("/feature")]
    [InlineData("feature/")]
    [InlineData("feature.")]
    [InlineData("feature..x")]
    [InlineData("feature//x")]
    [InlineData("feature@{1}")]
    [InlineData("feature x")]
    [InlineData("feature~1")]
    [InlineData("feature^")]
    [InlineData("feature:x")]
    [InlineData("feature?")]
    [InlineData("feature*")]
    [InlineData("feature[x")]
    [InlineData("feature\\x")]
    [InlineData("feature/.hidden")]
    [InlineData("feature/x.lock")]
    [InlineData("@")]
    [InlineData("HEAD")]
    [InlineData("tab\there")]
    public void InvalidNames_AreRefusedWithAReason(string name) =>
        GitBranchName.Validate(name).Should().NotBeNullOrWhiteSpace();

    [Fact]
    public void ATooLongName_IsRefused() =>
        GitBranchName.Validate(new string('a', GitBranchName.MaxLength + 1)).Should().NotBeNull();

    [Theory]
    [InlineData("Corrigir cálculo de animais", "feature/corrigir-calculo-de-animais")]
    [InlineData("  Integração: API / Vacinação!! ", "feature/integracao-api-vacinacao")]
    [InlineData("#123 — Bug no login", "feature/123-bug-no-login")]
    [InlineData("", "feature/tarefa")]
    [InlineData("???", "feature/tarefa")]
    public void Suggest_SlugsTheTitle(string title, string expected) =>
        GitBranchName.Suggest(title).Should().Be(expected);

    [Fact]
    public void Suggest_CapsTheSlug_WithoutEndingInAHyphen()
    {
        var suggestion = GitBranchName.Suggest(string.Join(' ', Enumerable.Repeat("palavra", 20)));

        suggestion.Length.Should().BeLessThanOrEqualTo(GitBranchName.DefaultPrefix.Length + GitBranchName.MaxSlugLength);
        suggestion.Should().NotEndWith("-");
        GitBranchName.Validate(suggestion).Should().BeNull();
    }
}
