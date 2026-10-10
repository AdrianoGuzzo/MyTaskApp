using MyTaskApp.Domain.DatabaseOperations;
using static MyTaskApp.Domain.Tests.DatabaseOperations.TestConnections;

namespace MyTaskApp.Domain.Tests.DatabaseOperations;

/// <summary>Os perfis de anonimização e de cópia (ADR-056).</summary>
public class ProfileTests
{
    private static readonly Guid Columns = Guid.CreateVersion7();

    private static AnonymizationRuleSpec Rule(string column, MaskingMethod method = MaskingMethod.FakeEmail, string? argument = null) =>
        new("public", "clientes", column, method, argument, ColumnSensitivity.High);

    [Fact]
    public void AnAnonymizationProfile_StartsEnabled_AndEmpty()
    {
        var profile = AnonymizationProfile.Create(" ECO LGPD ", " LGPD ", Columns, Now);

        profile.Name.Should().Be("ECO LGPD");
        profile.Description.Should().Be("LGPD");
        profile.ConnectionId.Should().Be(Columns);
        profile.IsEnabled.Should().BeTrue();
        profile.Rules.Should().BeEmpty();

        profile.Update("Outro", null, Columns, Now.AddMinutes(1));
        profile.Name.Should().Be("Outro");
        profile.UpdatedAt.Should().Be(Now.AddMinutes(1));

        profile.SetEnabled(false, Now.AddMinutes(2));
        profile.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void AnInvalidAnonymizationProfile_IsRefused()
    {
        FluentActions.Invoking(() => AnonymizationProfile.Create("", null, Columns, Now))
            .Should().Throw<DomainException>().WithMessage("*nome*");
        FluentActions.Invoking(() => AnonymizationProfile.Create(new string('x', 81), null, Columns, Now))
            .Should().Throw<DomainException>().WithMessage("*80*");
        FluentActions.Invoking(() => AnonymizationProfile.Create("x", new string('d', 501), Columns, Now))
            .Should().Throw<DomainException>().WithMessage("*500*");
        FluentActions.Invoking(() => AnonymizationProfile.Create("x", null, Guid.Empty, Now))
            .Should().Throw<DomainException>().WithMessage("*ler as colunas*");
    }

    [Fact]
    public void Rules_AreReplacedAsAWhole()
    {
        var profile = AnonymizationProfile.Create("x", "d", Columns, Now);

        profile.ReplaceRules([Rule("email"), Rule("cpf", MaskingMethod.Partial, " 0 , 2 ")], Now.AddMinutes(1));
        profile.Rules.Should().HaveCount(2);
        profile.Rules.Should().OnlyContain(rule => rule.ProfileId == profile.Id && rule.ConfirmedAt == Now.AddMinutes(1));
        profile.Rules.Single(rule => rule.Column == "cpf").Argument.Should().Be("0,2");
        profile.Rules.Single(rule => rule.Column == "email").TableKey.Should().Be("public.clientes");

        profile.ReplaceRules([Rule("telefone", MaskingMethod.Null)], Now.AddMinutes(2));
        profile.Rules.Should().ContainSingle().Which.QualifiedName.Should().Be("public.clientes.telefone");
        profile.UpdatedAt.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public void SkippedTables_AreReplacedAsAWhole_AndValidated()
    {
        var profile = AnonymizationProfile.Create("x", null, Columns, Now);

        profile.ReplaceSkippedTables([new SkippedTableSpec(" public ", "auditoria"), new SkippedTableSpec("logs", "Eventos")], Now.AddMinutes(1));
        profile.SkippedTables.Select(table => table.TableKey).Should().Equal("public.auditoria", "logs.Eventos");
        profile.SkippedTables.Should().OnlyContain(table => table.ProfileId == profile.Id && table.ConfirmedAt == Now.AddMinutes(1));
        profile.UpdatedAt.Should().Be(Now.AddMinutes(1));

        FluentActions.Invoking(() => profile.ReplaceSkippedTables(
                [new SkippedTableSpec("public", "filas"), new SkippedTableSpec("public", "filas")], Now))
            .Should().Throw<DomainException>().WithMessage("*duas vezes*");
        FluentActions.Invoking(() => profile.ReplaceSkippedTables([new SkippedTableSpec("public", " ")], Now))
            .Should().Throw<DomainException>().WithMessage("*tabela*");
        profile.SkippedTables.Should().HaveCount(2, "uma lista inválida não muda nada");

        profile.ReplaceSkippedTables([], Now.AddMinutes(2));
        profile.SkippedTables.Should().BeEmpty();
    }

    [Fact]
    public void TheSameColumnTwice_IsRefused_AndNothingChanges()
    {
        var profile = AnonymizationProfile.Create("x", null, Columns, Now);
        profile.ReplaceRules([Rule("email")], Now);

        FluentActions.Invoking(() => profile.ReplaceRules([Rule("cpf"), Rule("cpf")], Now))
            .Should().Throw<DomainException>().WithMessage("*duas vezes*");

        profile.Rules.Should().ContainSingle().Which.Column.Should().Be("email");
    }

    [Theory]
    [InlineData(MaskingMethod.Partial, null, "início,fim")]
    [InlineData(MaskingMethod.Partial, "2", "início,fim")]
    [InlineData(MaskingMethod.Partial, "-1,2", "início,fim")]
    [InlineData(MaskingMethod.Partial, "0,51", "início,fim")]
    [InlineData(MaskingMethod.Partial, "0,2); drop table x; --", "início,fim")]
    [InlineData(MaskingMethod.FixedNumber, "abc", "número")]
    [InlineData(MaskingMethod.FixedNumber, "1; drop table x", "número")]
    [InlineData(MaskingMethod.DateShift, "0", "dias")]
    [InlineData(MaskingMethod.DateShift, "3651", "dias")]
    [InlineData(MaskingMethod.NumberNoise, "0", "ruído")]
    [InlineData(MaskingMethod.NumberNoise, "101", "ruído")]
    [InlineData(MaskingMethod.FixedText, "linha\nquebrada", "quebra")]
    public void AnInvalidArgument_IsRefused(MaskingMethod method, string? argument, string message)
    {
        var profile = AnonymizationProfile.Create("x", null, Columns, Now);

        FluentActions.Invoking(() => profile.ReplaceRules([Rule("campo", method, argument)], Now))
            .Should().Throw<DomainException>().WithMessage($"*{message}*");
    }

    [Theory]
    [InlineData(MaskingMethod.Hash, "ignorado", null)]
    [InlineData(MaskingMethod.FakeEmail, null, null)]
    [InlineData(MaskingMethod.Null, null, null)]
    [InlineData(MaskingMethod.FakeName, null, null)]
    [InlineData(MaskingMethod.Partial, "3,0", "3,0")]
    [InlineData(MaskingMethod.FixedText, " d'Ávila ", " d'Ávila ")]
    [InlineData(MaskingMethod.FixedText, "", "")]
    [InlineData(MaskingMethod.FixedNumber, " 12.50 ", "12.50")]
    [InlineData(MaskingMethod.DateShift, "30", "30")]
    [InlineData(MaskingMethod.NumberNoise, "20", "20")]
    public void SafeMasks_AreAccepted_WithTheirNormalizedArgument(MaskingMethod method, string? argument, string? expected)
    {
        var profile = AnonymizationProfile.Create("x", null, Columns, Now);

        profile.ReplaceRules([Rule("campo", method, argument)], Now);

        profile.Rules.Single().Method.Should().Be(method);
        profile.Rules.Single().Argument.Should().Be(expected);
    }

    [Fact]
    public void AnInvalidIdentifierOrMethodInARule_IsRefused()
    {
        var profile = AnonymizationProfile.Create("x", null, Columns, Now);

        FluentActions.Invoking(() => profile.ReplaceRules([new AnonymizationRuleSpec("", "t", "c", MaskingMethod.Hash, null, ColumnSensitivity.Low)], Now))
            .Should().Throw<DomainException>();
        FluentActions.Invoking(() => profile.ReplaceRules([new AnonymizationRuleSpec("s", "t", new string('c', 64), MaskingMethod.Hash, null, ColumnSensitivity.Low)], Now))
            .Should().Throw<DomainException>();
        FluentActions.Invoking(() => profile.ReplaceRules([new AnonymizationRuleSpec("s", "t", "c", (MaskingMethod)99, null, ColumnSensitivity.Low)], Now))
            .Should().Throw<DomainException>().WithMessage("*desconhecida*");
        FluentActions.Invoking(() => profile.ReplaceRules([new AnonymizationRuleSpec("s", "t", "c", MaskingMethod.Hash, null, (ColumnSensitivity)9)], Now))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void TooManyRules_AreRefused()
    {
        var profile = AnonymizationProfile.Create("x", null, Columns, Now);
        var rules = Enumerable.Range(0, AnonymizationProfile.MaxRules + 1).Select(index => Rule($"c{index}", MaskingMethod.Hash));

        FluentActions.Invoking(() => profile.ReplaceRules(rules, Now)).Should().Throw<DomainException>().WithMessage("*2000*");
    }

    [Fact]
    public void ACopyProfile_KeepsItsOptions()
    {
        var source = Guid.CreateVersion7();
        var destination = Guid.CreateVersion7();
        var anonymization = Guid.CreateVersion7();

        var profile = DatabaseCopyProfile.Create("ECO Produção → ECO Desenvolvimento", source, destination, anonymization, DatabaseCopyOptions.Default, Now);

        profile.SourceConnectionId.Should().Be(source);
        profile.DestinationConnectionId.Should().Be(destination);
        profile.AnonymizationProfileId.Should().Be(anonymization);
        profile.Options.Should().Be(DatabaseCopyOptions.Default);
        profile.IsEnabled.Should().BeTrue();

        profile.Update("Outro", source, destination, null, DatabaseCopyOptions.Default with { RequireAnonymization = false }, Now.AddDays(1));
        profile.AnonymizationProfileId.Should().BeNull();
        profile.UpdatedAt.Should().Be(Now.AddDays(1));

        profile.SetEnabled(false, Now.AddDays(2));
        profile.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void AnInvalidCopyProfile_IsRefused()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        FluentActions.Invoking(() => DatabaseCopyProfile.Create("x", a, a, null, DatabaseCopyOptions.Default with { RequireAnonymization = false }, Now))
            .Should().Throw<DomainException>().WithMessage("*diferentes*");
        FluentActions.Invoking(() => DatabaseCopyProfile.Create("x", a, b, null, DatabaseCopyOptions.Default, Now))
            .Should().Throw<DomainException>().WithMessage("*perfil de anonimização*");
        FluentActions.Invoking(() => DatabaseCopyProfile.Create("x", a, b, null, new DatabaseCopyOptions(false, false, false, false, false, false), Now))
            .Should().Throw<DomainException>().WithMessage("*estrutura*");
        FluentActions.Invoking(() => DatabaseCopyProfile.Create(" ", a, b, null, DatabaseCopyOptions.Default with { RequireAnonymization = false }, Now))
            .Should().Throw<DomainException>();
        FluentActions.Invoking(() => DatabaseCopyProfile.Create("x", Guid.Empty, b, null, DatabaseCopyOptions.Default with { RequireAnonymization = false }, Now))
            .Should().Throw<DomainException>();
    }
}
