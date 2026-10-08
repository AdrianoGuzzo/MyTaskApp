using MyTaskApp.Domain.DatabaseOperations;
using static MyTaskApp.Domain.Tests.DatabaseOperations.TestConnections;

namespace MyTaskApp.Domain.Tests.DatabaseOperations;

/// <summary>A conexão cadastrada (ADR-056): validação, encaixe no ambiente e a referência ao segredo.</summary>
public class DatabaseConnectionTests
{
    [Fact]
    public void ANewConnection_KeepsWhatWasTyped_AndIsEnabled()
    {
        var connection = DatabaseConnection.Create(
            "  ECO Produção ",
            "192.168.15.112",
            5432,
            "eco_core",
            "backup_user",
            DatabaseEnvironment.Production,
            DatabaseSslMode.Require,
            "  Banco principal ",
            Everything,
            Now);

        connection.Name.Should().Be("ECO Produção");
        connection.Host.Should().Be("192.168.15.112");
        connection.Database.Should().Be("eco_core");
        connection.Username.Should().Be("backup_user");
        connection.Description.Should().Be("Banco principal");
        connection.SslMode.Should().Be(DatabaseSslMode.Require);
        connection.IsEnabled.Should().BeTrue();
        connection.SecretReference.Should().BeNull();
        connection.CreatedAt.Should().Be(Now);
        connection.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void ChangingTheEnvironmentToProduction_CutsWhatProductionDoesNotAdmit()
    {
        var connection = Connection(DatabaseEnvironment.Development, permissions: Everything);
        connection.CanRestore.Should().BeTrue();

        connection.Update("x", "h", 5432, "d", "u", DatabaseEnvironment.Production, DatabaseSslMode.Prefer, null, Everything, Now.AddHours(1));

        connection.CanRestore.Should().BeFalse();
        connection.AllowAsDestination.Should().BeFalse();
        connection.StoredPermissions.HasFlag(ConnectionPermission.Restore).Should().BeFalse();
        connection.UpdatedAt.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void BackToDevelopment_DoesNotGiveBackWhatWasCut()
    {
        var connection = Connection(DatabaseEnvironment.Production, permissions: Everything);

        connection.Update("x", "h", 5432, "d", "u", DatabaseEnvironment.Development, DatabaseSslMode.Prefer, null,
            connection.Permissions, Now);

        connection.CanRestore.Should().BeFalse("o que se pede agora é o que estava valendo");
        connection.RequireAnonymization.Should().BeTrue("piso de produção que ficou como escolha");
    }

    [Theory]
    [InlineData("", "h", 5432, "d", "u", "nome")]
    [InlineData("x", "", 5432, "d", "u", "servidor")]
    [InlineData("x", "h o s t", 5432, "d", "u", "espaços")]
    [InlineData("x", "h", 0, "d", "u", "porta")]
    [InlineData("x", "h", 70000, "d", "u", "porta")]
    [InlineData("x", "h", 5432, "", "u", "banco")]
    [InlineData("x", "h", 5432, "--help", "u", "hífen")]
    [InlineData("x", "-h", 5432, "d", "u", "hífen")]
    [InlineData("x", "h", 5432, "d", "-U", "hífen")]
    [InlineData("x", "h", 5432, "d", "", "usuário")]
    [InlineData("x", "h", 5432, "d\nx", "u", "inválidos")]
    public void InvalidFields_AreRefused(string name, string host, int port, string database, string username, string message)
    {
        FluentActions.Invoking(() => DatabaseConnection.Create(
                name, host, port, database, username, DatabaseEnvironment.Development, DatabaseSslMode.Prefer, null, Everything, Now))
            .Should().Throw<DomainException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void TooLongFields_AreRefused()
    {
        FluentActions.Invoking(() => DatabaseConnection.Create(
                new string('x', DatabaseConnection.MaxNameLength + 1), "h", 5432, "d", "u",
                DatabaseEnvironment.Development, DatabaseSslMode.Prefer, null, Everything, Now))
            .Should().Throw<DomainException>().WithMessage("*80*");

        FluentActions.Invoking(() => DatabaseConnection.Create(
                "x", "h", 5432, new string('d', 64), "u",
                DatabaseEnvironment.Development, DatabaseSslMode.Prefer, null, Everything, Now))
            .Should().Throw<DomainException>().WithMessage("*63*");

        FluentActions.Invoking(() => DatabaseConnection.Create(
                "x", "h", 5432, "d", "u",
                DatabaseEnvironment.Development, DatabaseSslMode.Prefer, new string('a', 501), Everything, Now))
            .Should().Throw<DomainException>().WithMessage("*500*");
    }

    [Fact]
    public void UnknownEnvironmentOrSsl_AreRefused()
    {
        FluentActions.Invoking(() => DatabaseConnection.Create(
                "x", "h", 5432, "d", "u", (DatabaseEnvironment)42, DatabaseSslMode.Prefer, null, Everything, Now))
            .Should().Throw<DomainException>();
        FluentActions.Invoking(() => DatabaseConnection.Create(
                "x", "h", 5432, "d", "u", DatabaseEnvironment.Test, (DatabaseSslMode)42, null, Everything, Now))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void AFailedUpdate_ChangesNothing()
    {
        var connection = Connection(DatabaseEnvironment.Development, name: "Original");

        FluentActions.Invoking(() => connection.Update("Novo", "h", 0, "d", "u", DatabaseEnvironment.Test, DatabaseSslMode.Prefer, null, Everything, Now))
            .Should().Throw<DomainException>();

        connection.Name.Should().Be("Original");
        connection.Environment.Should().Be(DatabaseEnvironment.Development);
    }

    [Fact]
    public void TheSecretReference_IsDerivedFromTheId_AndNothingElseIsAccepted()
    {
        var connection = Connection(DatabaseEnvironment.Production);
        var reference = connection.SecretNameForThis();

        reference.Should().StartWith("postgres-").And.HaveLength("postgres-".Length + 32);
        FluentActions.Invoking(() => connection.AttachSecret("postgres/eco-production", Now)).Should().Throw<DomainException>();

        connection.AttachSecret(reference, Now);
        connection.SecretReference.Should().Be(reference);
        connection.Snapshot().SecretReference.Should().Be(reference);

        connection.DetachSecret(Now);
        connection.SecretReference.Should().BeNull();
        connection.DetachSecret(Now);
    }

    [Fact]
    public void Enabling_IsIdempotent()
    {
        var connection = Connection(DatabaseEnvironment.Test);

        connection.SetEnabled(true, Now.AddDays(1));
        connection.UpdatedAt.Should().Be(Now);

        connection.SetEnabled(false, Now.AddDays(1));
        connection.IsEnabled.Should().BeFalse();
        connection.UpdatedAt.Should().Be(Now.AddDays(1));
        connection.Snapshot().IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void TheSnapshot_CarriesTheEffectivePermissions_AndComparesEndpointsWithoutCase()
    {
        var a = Snapshot(DatabaseEnvironment.Production, host: "DB.Local", database: "eco", username: "a");
        var b = Snapshot(DatabaseEnvironment.Development, host: "db.local ", database: "eco", username: "b");

        a.EndpointKey.Should().Be(b.EndpointKey);
        a.ServerKey.Should().Be("db.local:5432");
        a.Permissions.CanRestore.Should().BeFalse();
        a.IsProtected.Should().BeTrue();
        b.IsProtected.Should().BeFalse();
    }
}
