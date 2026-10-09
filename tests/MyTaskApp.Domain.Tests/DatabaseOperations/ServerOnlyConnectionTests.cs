using MyTaskApp.Domain.DatabaseOperations;
using static MyTaskApp.Domain.Tests.DatabaseOperations.TestConnections;

namespace MyTaskApp.Domain.Tests.DatabaseOperations;

/// <summary>
/// Conexão só de servidor e apelidos (ADR-057): o banco é escolhido na cópia,
/// e o banco copiado nasce com o apelido e a data no nome.
/// </summary>
public class ServerOnlyConnectionTests
{
    private readonly DatabaseSecurityPolicy _policy = new();

    private static DatabaseConnectionSnapshot ServerOnly(
        DatabaseEnvironment environment,
        string? name = null,
        string host = "db.local",
        ConnectionPermissions? permissions = null) =>
        DatabaseConnection.Create(
            name ?? environment + " (servidor)",
            host,
            5432,
            database: null,
            "app",
            environment,
            DatabaseSslMode.Prefer,
            description: null,
            permissions ?? ConnectionPermissions.FromFlags(EnvironmentPolicy.For(environment).Defaults),
            Now).Snapshot();

    // --- A conexão -----------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AConnection_CanBeJustTheServer(string? database)
    {
        var connection = DatabaseConnection.Create(
            "ECO", "192.168.15.112", 5432, database, "backup_user",
            DatabaseEnvironment.Production, DatabaseSslMode.Prefer, null, Everything, Now);

        connection.Database.Should().BeNull();
        connection.Snapshot().HasDatabase.Should().BeFalse();
        connection.Snapshot().EndpointKey.Should().Be("192.168.15.112:5432/*");
    }

    [Fact]
    public void ChoosingTheDatabase_ValidatesItLikeTheConnectionWould()
    {
        var server = ServerOnly(DatabaseEnvironment.Production);

        var chosen = server.WithDatabase(" eco_core_1010 ");

        chosen.Database.Should().Be("eco_core_1010");
        chosen.EndpointKey.Should().Be("db.local:5432/eco_core_1010");
        chosen.Id.Should().Be(server.Id);

        foreach (var invalid in (string[])["", "--help", "host=prod dbname=eco", "postgresql://prod/eco", "*"])
        {
            FluentActions.Invoking(() => server.WithDatabase(invalid)).Should().Throw<DomainException>(invalid);
        }
    }

    [Fact]
    public void AProductionServer_ProtectsEveryDatabaseOnIt()
    {
        var keys = new[] { ServerOnly(DatabaseEnvironment.Production).EndpointKey };

        Snapshot(DatabaseEnvironment.Development, database: "qualquer").IsAmong(keys).Should().BeTrue();
        Snapshot(DatabaseEnvironment.Development, host: "outro.local", database: "qualquer").IsAmong(keys).Should().BeFalse();
        Snapshot(DatabaseEnvironment.Development, database: "qualquer").IsAmong(null).Should().BeFalse();
    }

    // --- A política ----------------------------------------------------------------

    [Fact]
    public void ACopy_FromAServerWithoutChoosingTheDatabase_IsRefused()
    {
        var source = ServerOnly(DatabaseEnvironment.Production, permissions: Everything);
        var destination = Snapshot(DatabaseEnvironment.Development, host: "localhost", database: "eco_dev");

        var decision = _policy.Evaluate(CopyAndAnonymize(source, destination));

        decision.Has(SecurityViolationCode.DatabaseNotChosen).Should().BeTrue();
    }

    [Fact]
    public void ADevelopmentDestination_OnAProductionServer_IsRefused_WhateverTheDatabase()
    {
        var production = ServerOnly(DatabaseEnvironment.Production, permissions: Everything);
        var source = production.WithDatabase("eco_core_1010");
        var sneaky = Snapshot(DatabaseEnvironment.Development, database: "copia_local");

        var request = CopyAndAnonymize(source, sneaky) with { ProtectedEndpoints = [production.EndpointKey] };

        _policy.Evaluate(request).Has(SecurityViolationCode.DestinationMatchesProtectedEndpoint).Should().BeTrue();
    }

    [Fact]
    public void ANewDestinationDatabase_NeedsCreate_ButNotDrop()
    {
        var source = ServerOnly(DatabaseEnvironment.Production, permissions: Everything).WithDatabase("eco_core_1010");
        var withoutDrop = ConnectionPermissions.FromFlags(ConnectionPermission.All & ~ConnectionPermission.DropDatabase);
        var local = ServerOnly(DatabaseEnvironment.Development, "Local", "localhost", withoutDrop)
            .WithDatabase("lock_eco_core_1010_20261009_143000");

        var recreating = CopyAndAnonymize(source, local);
        var creating = recreating with { NewDestinationDatabase = true };

        _policy.Evaluate(recreating).Has(SecurityViolationCode.MissingPermission).Should().BeTrue("recriar apaga antes");
        _policy.Evaluate(creating).IsAllowed.Should().BeTrue();

        var withoutCreate = ServerOnly(DatabaseEnvironment.Development, "Local", "localhost",
                ConnectionPermissions.FromFlags(ConnectionPermission.All & ~ConnectionPermission.CreateDatabase))
            .WithDatabase("lock_eco_core_1010_20261009_143000");

        _policy.Evaluate(creating with { Destination = withoutCreate }).Has(SecurityViolationCode.MissingPermission).Should().BeTrue();
    }

    [Theory]
    [InlineData(DatabaseOperationType.Restore)]
    [InlineData(DatabaseOperationType.CreateDatabase)]
    [InlineData(DatabaseOperationType.DropDatabase)]
    public void WritingToAServer_WithoutADatabase_IsRefused(DatabaseOperationType operation)
    {
        var local = ServerOnly(DatabaseEnvironment.Development, permissions: Everything with { RequireAnonymization = false });

        _policy.Evaluate(new DatabaseOperationRequest(operation, Destination: local))
            .Has(SecurityViolationCode.DatabaseNotChosen).Should().BeTrue();
    }

    [Fact]
    public void TestingAServerOnlyConnection_IsAllowed()
    {
        _policy.Evaluate(new DatabaseOperationRequest(DatabaseOperationType.TestConnection, ServerOnly(DatabaseEnvironment.Production)))
            .IsAllowed.Should().BeTrue();
    }

    // --- O apelido -----------------------------------------------------------------

    [Fact]
    public void AnAlias_IsSavedLowercase_WithItsDatabaseAndAnonymization()
    {
        var connection = Guid.CreateVersion7();
        var profile = Guid.CreateVersion7();

        var saved = SavedDatabase.Create(" Lock_Eco_Core_1010 ", connection, " eco_core_1010 ", profile, Now);

        saved.Alias.Should().Be("lock_eco_core_1010");
        saved.DatabaseName.Should().Be("eco_core_1010");
        saved.ConnectionId.Should().Be(connection);
        saved.AnonymizationProfileId.Should().Be(profile);

        saved.Update("lock_eco_core_1010_v2", connection, "eco_core_1010", null, Now.AddMinutes(1));
        saved.Alias.Should().Be("lock_eco_core_1010_v2");
        saved.AnonymizationProfileId.Should().BeNull();
        saved.UpdatedAt.Should().Be(Now.AddMinutes(1));
    }

    [Theory]
    [InlineData("", "apelido")]
    [InlineData("1010_eco", "número")]
    [InlineData("eco-core", "letras")]
    [InlineData("eco core", "letras")]
    [InlineData("ecó", "letras")]
    public void AnInvalidAlias_IsRefused(string alias, string message)
    {
        FluentActions.Invoking(() => SavedDatabase.Create(alias, Guid.CreateVersion7(), "eco", null, Now))
            .Should().Throw<DomainException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void TheAlias_LeavesRoomForTheDate_InThePostgresLimit()
    {
        var longest = new string('a', SavedDatabase.MaxAliasLength);

        SavedDatabase.Create(longest, Guid.CreateVersion7(), "eco", null, Now).Alias.Should().Be(longest);
        FluentActions.Invoking(() => SavedDatabase.Create(longest + "a", Guid.CreateVersion7(), "eco", null, Now))
            .Should().Throw<DomainException>().WithMessage("*data*");

        SavedDatabase.CopyName(longest, Now).Length.Should().Be(DatabaseConnection.MaxIdentifierLength);
    }

    [Fact]
    public void AnAlias_NeedsAConnectionAndAValidDatabase()
    {
        FluentActions.Invoking(() => SavedDatabase.Create("eco", Guid.Empty, "eco", null, Now))
            .Should().Throw<DomainException>().WithMessage("*conexão*");
        FluentActions.Invoking(() => SavedDatabase.Create("eco", Guid.CreateVersion7(), "--help", null, Now))
            .Should().Throw<DomainException>().WithMessage("*hífen*");
    }

    [Theory]
    [InlineData("lock_eco_core_1010", "lock_eco_core_1010_20261009_143005")]
    [InlineData("ECO-Core", "eco_core_20261009_143005")]
    [InlineData("1010", "db_1010_20261009_143005")]
    public void TheCopiedDatabase_IsNamedAfterThePrefix_AndTheMoment(string prefix, string expected)
    {
        var at = new DateTimeOffset(2026, 10, 9, 14, 30, 5, TimeSpan.FromHours(-3));

        SavedDatabase.CopyName(prefix, at).Should().Be(expected);
    }

    [Fact]
    public void ALongSourceDatabase_IsCut_ToFitWithTheDate()
    {
        var name = SavedDatabase.CopyName(new string('x', 63), Now);

        name.Length.Should().Be(DatabaseConnection.MaxIdentifierLength);
        name.Should().EndWith("_20261008_184102");
    }

    [Fact]
    public void ACopyProfile_KeepsTheChosenSourceDatabase()
    {
        var options = DatabaseCopyOptions.Default with { RequireAnonymization = false };
        var profile = DatabaseCopyProfile.Create("x", Guid.CreateVersion7(), Guid.CreateVersion7(), null, options, Now, " eco_core_1010 ");

        profile.SourceDatabase.Should().Be("eco_core_1010");

        profile.Update("x", profile.SourceConnectionId, profile.DestinationConnectionId, null, options, Now, sourceDatabase: " ");
        profile.SourceDatabase.Should().BeNull();

        FluentActions.Invoking(() => profile.Update("x", profile.SourceConnectionId, profile.DestinationConnectionId, null, options, Now, "--x"))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void TheAudit_RecordsBothDatabases()
    {
        var source = ServerOnly(DatabaseEnvironment.Production).WithDatabase("eco_core_1010");
        var destination = ServerOnly(DatabaseEnvironment.Development, "Local").WithDatabase("lock_eco_core_1010_20261009_143000");

        var audit = DatabaseOperationAudit.Start(DatabaseOperationType.CopyAndAnonymize, source, destination, null, null, "PC", "eu", Now);

        audit.SourceDatabase.Should().Be("eco_core_1010");
        audit.DestinationDatabase.Should().Be("lock_eco_core_1010_20261009_143000");
    }
}
