using MyTaskApp.Domain.DatabaseOperations;
using static MyTaskApp.Domain.Tests.DatabaseOperations.TestConnections;

namespace MyTaskApp.Domain.Tests.DatabaseOperations;

/// <summary>Os perfis de anonimização e de cópia (ADR-056).</summary>
public class ProfileTests
{
    private static readonly Guid Masked = Guid.CreateVersion7();

    private static AnonymizationRuleSpec Rule(string column, string expression = "anon.fake_email()", MaskingKind kind = MaskingKind.Function) =>
        new("public", "clientes", column, kind, expression, ColumnSensitivity.High);

    [Fact]
    public void AnAnonymizationProfile_StartsEnabled_WithTheDefaultPolicy()
    {
        var profile = AnonymizationProfile.Create(" ECO LGPD ", null, Masked, null, Now);

        profile.Name.Should().Be("ECO LGPD");
        profile.PolicyName.Should().Be("anon");
        profile.IsEnabled.Should().BeTrue();
        profile.Rules.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "anon", "nome")]
    [InlineData("x", "Anon", "política")]
    [InlineData("x", "anon; drop", "política")]
    public void AnInvalidAnonymizationProfile_IsRefused(string name, string policy, string message)
    {
        FluentActions.Invoking(() => AnonymizationProfile.Create(name, null, Masked, policy, Now))
            .Should().Throw<DomainException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void AProfileNeedsTheMaskedConnection()
    {
        FluentActions.Invoking(() => AnonymizationProfile.Create("x", null, Guid.Empty, null, Now))
            .Should().Throw<DomainException>().WithMessage("*conexão mascarada*");
    }

    [Fact]
    public void Rules_AreReplacedAsAWhole()
    {
        var profile = AnonymizationProfile.Create("x", "d", Masked, "anon", Now);

        profile.ReplaceRules([Rule("email"), Rule("cpf", "anon.partial(cpf,2,$$*******$$,2)")], Now.AddMinutes(1));
        profile.Rules.Should().HaveCount(2);
        profile.Rules.Should().OnlyContain(rule => rule.ProfileId == profile.Id && rule.ConfirmedAt == Now.AddMinutes(1));

        profile.ReplaceRules([Rule("telefone", "NULL", MaskingKind.Value)], Now.AddMinutes(2));
        profile.Rules.Should().ContainSingle().Which.QualifiedName.Should().Be("public.clientes.telefone");
        profile.UpdatedAt.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public void TheSameColumnTwice_IsRefused_AndNothingChanges()
    {
        var profile = AnonymizationProfile.Create("x", null, Masked, null, Now);
        profile.ReplaceRules([Rule("email")], Now);

        FluentActions.Invoking(() => profile.ReplaceRules([Rule("cpf"), Rule("cpf")], Now))
            .Should().Throw<DomainException>().WithMessage("*duas vezes*");

        profile.Rules.Should().ContainSingle().Which.Column.Should().Be("email");
    }

    [Theory]
    [InlineData(MaskingKind.Function, "anon.anonymize_database()")]
    [InlineData(MaskingKind.Function, "anon.anonymize_table('public.x')")]
    [InlineData(MaskingKind.Function, "anon.shuffle_column('x','y','id')")]
    [InlineData(MaskingKind.Function, "md5(email)")]
    [InlineData(MaskingKind.Function, "anon.fake_email(); drop table x")]
    [InlineData(MaskingKind.Function, "anon.fake_email() -- x")]
    [InlineData(MaskingKind.Function, "anon.fake_email() /* x */")]
    [InlineData(MaskingKind.Function, "")]
    [InlineData(MaskingKind.Value, "email")]
    [InlineData(MaskingKind.Value, "'aberto")]
    public void UnsafeOrStaticMasks_AreRefused(MaskingKind kind, string expression)
    {
        var profile = AnonymizationProfile.Create("x", null, Masked, null, Now);

        FluentActions.Invoking(() => profile.ReplaceRules([Rule("email", expression, kind)], Now))
            .Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(MaskingKind.Value, "NULL")]
    [InlineData(MaskingKind.Value, "0")]
    [InlineData(MaskingKind.Value, "'CONFIDENCIAL'")]
    [InlineData(MaskingKind.Value, "'d''Ávila'")]
    [InlineData(MaskingKind.Function, "anon.dummy_first_name()")]
    public void SafeMasks_AreAccepted(MaskingKind kind, string expression)
    {
        var profile = AnonymizationProfile.Create("x", null, Masked, null, Now);

        profile.ReplaceRules([Rule("campo", expression, kind)], Now);

        profile.Rules.Single().Expression.Should().Be(expression);
    }

    [Fact]
    public void AnInvalidIdentifierInARule_IsRefused()
    {
        var profile = AnonymizationProfile.Create("x", null, Masked, null, Now);

        FluentActions.Invoking(() => profile.ReplaceRules([new AnonymizationRuleSpec("", "t", "c", MaskingKind.Function, "anon.fake_email()", ColumnSensitivity.Low)], Now))
            .Should().Throw<DomainException>();
        FluentActions.Invoking(() => profile.ReplaceRules([new AnonymizationRuleSpec("s", "t", new string('c', 64), MaskingKind.Function, "anon.fake_email()", ColumnSensitivity.Low)], Now))
            .Should().Throw<DomainException>();
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
