using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Domain.Tests.DatabaseOperations;

/// <summary>O catálogo de máscaras (ADR-058): o que cabe em cada tipo, o que mantém únicos, e os nomes citados.</summary>
public class MaskingCatalogTests
{
    [Theory]
    [InlineData("text", MaskedValueType.Text)]
    [InlineData("character varying(14)", MaskedValueType.Text)]
    [InlineData("character(11)", MaskedValueType.Text)]
    [InlineData("citext", MaskedValueType.Text)]
    [InlineData("integer", MaskedValueType.Number)]
    [InlineData("numeric(10,2)", MaskedValueType.Number)]
    [InlineData("double precision", MaskedValueType.Number)]
    [InlineData("money", MaskedValueType.Number)]
    [InlineData("date", MaskedValueType.Temporal)]
    [InlineData("timestamp with time zone", MaskedValueType.Temporal)]
    [InlineData("timestamp(3) without time zone", MaskedValueType.Temporal)]
    [InlineData("text[]", MaskedValueType.Other)]
    [InlineData("jsonb", MaskedValueType.Other)]
    [InlineData("uuid", MaskedValueType.Other)]
    [InlineData(null, MaskedValueType.Other)]
    public void TheColumnType_IsReadFromThePostgresName(string? dataType, MaskedValueType expected)
    {
        MaskingCatalog.ValueTypeOf(dataType).Should().Be(expected);
    }

    [Fact]
    public void EachMask_FitsItsTypes()
    {
        MaskingCatalog.Fits(MaskingMethod.FakeEmail, MaskedValueType.Text).Should().BeTrue();
        MaskingCatalog.Fits(MaskingMethod.FakeEmail, MaskedValueType.Number).Should().BeFalse();
        MaskingCatalog.Fits(MaskingMethod.NumberNoise, MaskedValueType.Number).Should().BeTrue();
        MaskingCatalog.Fits(MaskingMethod.DateShift, MaskedValueType.Temporal).Should().BeTrue();
        MaskingCatalog.Fits(MaskingMethod.DateShift, MaskedValueType.Text).Should().BeFalse();
        MaskingCatalog.Fits(MaskingMethod.Null, MaskedValueType.Other).Should().BeTrue();
    }

    [Fact]
    public void OnlyHashAndFakeEmail_KeepUniqueness()
    {
        MaskingCatalog.All.Where(info => info.KeepsUniqueness).Select(info => info.Method)
            .Should().BeEquivalentTo([MaskingMethod.Hash, MaskingMethod.FakeEmail]);
    }

    [Fact]
    public void EveryMask_IsInTheCatalog_WithAnExplanation()
    {
        foreach (var method in Enum.GetValues<MaskingMethod>())
        {
            var info = MaskingCatalog.Of(method);
            info.Label.Should().NotBeNullOrWhiteSpace();
            info.Description.Should().NotBeNullOrWhiteSpace();

            if (info.NeedsArgument)
            {
                MaskingCatalog.NormalizeArgument(method, info.DefaultArgument, "c").Should().NotBeNull("o padrão é válido");
            }
            else
            {
                MaskingCatalog.NormalizeArgument(method, "qualquer", "c").Should().BeNull("quem não pede parâmetro ignora o que vier");
            }
        }

        FluentActions.Invoking(() => MaskingCatalog.Of((MaskingMethod)99)).Should().Throw<DomainException>();
    }

    [Fact]
    public void ThePartialArgument_IsReadAsStartAndEnd()
    {
        MaskingCatalog.PartialKeep("3,2").Should().Be((3, 2));
        MaskingCatalog.PartialKeep(null).Should().Be((0, 0));
    }

    [Fact]
    public void Identifiers_AndText_StayInsideTheirQuotes()
    {
        SqlQuoting.QuoteIdentifier("c\"; drop table x; --").Should().Be("\"c\"\"; drop table x; --\"");
        SqlQuoting.QuoteLiteral("d'Ávila'; drop").Should().Be("'d''Ávila''; drop'");
    }
}
