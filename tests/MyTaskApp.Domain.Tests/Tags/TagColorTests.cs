using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Domain.Tests.Tags;

public class TagColorTests
{
    [Theory]
    [InlineData("#ff5733", "#FF5733")]
    [InlineData("FF5733", "#FF5733")]
    [InlineData(" #F53 ", "#FF5533")]
    public void Normalize_ReturnsUppercaseSixDigitHex(string input, string expected) =>
        TagColor.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#GGGGGG")]
    [InlineData("#FF5733AA")]
    public void Normalize_RejectsAnythingElse(string? input)
    {
        var normalize = () => TagColor.Normalize(input);

        normalize.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("#FFFFFF", true)]
    [InlineData("#EAB308", true)]
    [InlineData("#000000", false)]
    [InlineData("#1D4ED8", false)]
    [InlineData("#B91C1C", false)]
    public void PrefersDarkText_PicksTheTextThatContrastsMore(string color, bool dark) =>
        TagColor.PrefersDarkText(color).Should().Be(dark);

    [Fact]
    public void Palette_IsAllValidAndDistinct()
    {
        TagColor.Palette.Should().OnlyContain(hex => TagColor.Normalize(hex) == hex);
        TagColor.Palette.Should().OnlyHaveUniqueItems();
    }
}
