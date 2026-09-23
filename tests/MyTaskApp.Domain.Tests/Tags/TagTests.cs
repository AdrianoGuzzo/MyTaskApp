using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Domain.Tests.Tags;

public class TagTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_TrimsTheNameAndNormalizesTheColor()
    {
        var tag = Tag.Create("  Urgente ", "#ef4444", Now);

        tag.Name.Should().Be("Urgente");
        tag.ColorHex.Should().Be("#EF4444");
        tag.CreatedAt.Should().Be(Now);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutName_IsRejected(string? name)
    {
        var create = () => Tag.Create(name!, "#EF4444", Now);

        create.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_BeyondMaximumLength_IsRejected()
    {
        var create = () => Tag.Create(new string('a', Tag.MaxNameLength + 1), "#EF4444", Now);

        create.Should().Throw<DomainException>();
    }

    [Fact]
    public void Update_ReplacesNameAndColor()
    {
        var tag = Tag.Create("Urgente", "#EF4444", Now);

        tag.Update("Financeiro", "#3b82f6");

        tag.Name.Should().Be("Financeiro");
        tag.ColorHex.Should().Be("#3B82F6");
    }

    [Fact]
    public void Update_WithInvalidColor_ChangesNothing()
    {
        var tag = Tag.Create("Urgente", "#EF4444", Now);

        var update = () => tag.Update("Financeiro", "azul");

        update.Should().Throw<DomainException>();
        tag.Name.Should().Be("Urgente");
        tag.ColorHex.Should().Be("#EF4444");
    }
}
