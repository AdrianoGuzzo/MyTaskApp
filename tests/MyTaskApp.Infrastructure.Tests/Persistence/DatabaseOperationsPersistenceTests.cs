using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// As conexões, os perfis e a trilha das operações de banco contra SQLite de
/// verdade (ADR-056): ida e volta, encaixe no ambiente na leitura, e nenhuma
/// coluna onde uma senha caberia.
/// </summary>
public class DatabaseOperationsPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 18, 41, 2, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ConnectionPermissions Everything = ConnectionPermissions.FromFlags(ConnectionPermission.All);

    private static DatabaseConnection Connection(string name, DatabaseEnvironment environment, string? database) =>
        DatabaseConnection.Create(
            name, "192.168.15.112", 5432, database, "backup_user", environment, DatabaseSslMode.Require, "descrição", Everything, Now);

    private static async Task SeedAsync(TempSqliteDatabase db, params object[] entities)
    {
        await using var context = db.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task AConnection_RoundTrips_WithItsSecretReference()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var production = Connection("ECO Produção", DatabaseEnvironment.Production, "eco_core");
        production.AttachSecret(production.SecretNameForThis(), Now);
        await SeedAsync(db, production);

        await using var read = db.CreateContext();
        var connections = new DatabaseConnectionRepository(read);
        var reloaded = await connections.FindByIdAsync(production.Id, Ct);

        reloaded.Should().NotBeNull();
        reloaded!.Name.Should().Be("ECO Produção");
        reloaded.Host.Should().Be("192.168.15.112");
        reloaded.Environment.Should().Be(DatabaseEnvironment.Production);
        reloaded.SslMode.Should().Be(DatabaseSslMode.Require);
        reloaded.SecretReference.Should().Be(production.SecretNameForThis());
        reloaded.CreatedAt.Should().Be(Now);
        reloaded.Permissions.Should().Be(production.Permissions);
        (await connections.NameExistsAsync("eco produção", null, Ct)).Should().BeTrue();
        (await connections.NameExistsAsync("eco produção", production.Id, Ct)).Should().BeFalse();
        (await connections.ListAsync(Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task PermissionsWrittenByHand_AreStillCutByTheEnvironment()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var production = Connection("ECO Produção", DatabaseEnvironment.Production, "eco_core");
        await SeedAsync(db, production);

        await using (var write = db.CreateContext())
        {
            // Alguém abriu o SQLite e ligou tudo.
            await write.Database.ExecuteSqlAsync(
                $"UPDATE DatabaseConnections SET Permissions = {(int)ConnectionPermission.All} WHERE Id = {production.Id}",
                Ct);
        }

        await using var read = db.CreateContext();
        var reloaded = await new DatabaseConnectionRepository(read).FindByIdAsync(production.Id, Ct);

        reloaded!.StoredPermissions.Should().Be(ConnectionPermission.All);
        reloaded.CanRestore.Should().BeFalse();
        reloaded.CanDropDatabase.Should().BeFalse();
        reloaded.CanExecuteSql.Should().BeFalse();
        reloaded.AllowAsDestination.Should().BeFalse();
        reloaded.Snapshot().Permissions.CanModify.Should().BeFalse();
    }

    [Fact]
    public async Task NoColumn_CanHoldAPassword()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var connection = new SqliteConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);

        foreach (var table in (string[])["DatabaseConnections", "AnonymizationProfiles", "AnonymizationRules", "DatabaseCopyProfiles", "DatabaseOperationAudits"])
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
            var columns = new List<string>();

            await using (var reader = await command.ExecuteReaderAsync(Ct))
            {
                while (await reader.ReadAsync(Ct))
                {
                    columns.Add(reader.GetString(0));
                }
            }

            columns.Should().NotBeEmpty();
            columns.Should().NotContain(column =>
                column.Contains("password", StringComparison.OrdinalIgnoreCase)
                || column.Contains("senha", StringComparison.OrdinalIgnoreCase)
                || column.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase)
                || column.Equals("Secret", StringComparison.OrdinalIgnoreCase), table);
        }
    }

    [Fact]
    public async Task AnAnonymizationProfile_RoundTripsWithItsRules_AndTakesThemWhenDeleted()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var masked = Connection("ECO Produção (anon)", DatabaseEnvironment.Production, "eco_core");
        var profile = AnonymizationProfile.Create("ECO LGPD", "LGPD", masked.Id, Now);
        profile.ReplaceRules(
        [
            new AnonymizationRuleSpec("public", "clientes", "email", MaskingMethod.FakeEmail, null, ColumnSensitivity.High),
            new AnonymizationRuleSpec("public", "clientes", "obs", MaskingMethod.FixedText, "(removido)", ColumnSensitivity.Low),
        ], Now);
        profile.ReplaceSkippedTables([new SkippedTableSpec("public", "auditoria"), new SkippedTableSpec("public", "Auditoria")], Now);
        await SeedAsync(db, masked, profile);

        await using (var read = db.CreateContext())
        {
            var profiles = new AnonymizationProfileRepository(read);
            var reloaded = await profiles.FindByIdAsync(profile.Id, Ct);

            reloaded!.Rules.Should().HaveCount(2);
            reloaded.Rules.Select(rule => rule.QualifiedName).Should().BeEquivalentTo("public.clientes.email", "public.clientes.obs");
            reloaded.Rules.Single(rule => rule.Column == "email").Sensitivity.Should().Be(ColumnSensitivity.High);
            reloaded.Rules.Single(rule => rule.Column == "email").Method.Should().Be(MaskingMethod.FakeEmail);
            reloaded.Rules.Single(rule => rule.Column == "obs").Should().Match<AnonymizationRule>(
                rule => rule.Method == MaskingMethod.FixedText && rule.Argument == "(removido)");
            (await profiles.ListAsync(Ct)).Single().Rules.Should().HaveCount(2);
            (await profiles.AnyUsesConnectionAsync(masked.Id, Ct)).Should().BeTrue();

            // Maiúsculas contam: "Auditoria" citada é outra tabela no PostgreSQL.
            reloaded.SkippedTables.Select(table => table.TableKey).Should().BeEquivalentTo("public.auditoria", "public.Auditoria");
            (await profiles.ListAsync(Ct)).Single().SkippedTables.Should().HaveCount(2);

            reloaded.ReplaceRules([new AnonymizationRuleSpec("public", "clientes", "cpf", MaskingMethod.Partial, "0,2", ColumnSensitivity.High)], Now);
            reloaded.ReplaceSkippedTables([new SkippedTableSpec("logs", "eventos")], Now);
            await read.SaveChangesAsync(Ct);
        }

        await using (var read = db.CreateContext())
        {
            (await read.Set<AnonymizationRule>().CountAsync(Ct)).Should().Be(1);
            (await read.Set<AnonymizationSkippedTable>().Select(table => table.Schema + "." + table.Table).ToListAsync(Ct))
                .Should().Equal("logs.eventos");
            var profiles = new AnonymizationProfileRepository(read);
            profiles.Remove((await profiles.FindByIdAsync(profile.Id, Ct))!);
            await read.SaveChangesAsync(Ct);
        }

        await using var check = db.CreateContext();
        (await check.Set<AnonymizationRule>().CountAsync(Ct)).Should().Be(0);
        (await check.Set<AnonymizationSkippedTable>().CountAsync(Ct)).Should().Be(0, "as tabelas sem dados saem com o perfil");
    }

    [Fact]
    public async Task AConnectionInUse_CannotBeDeleted()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var production = Connection("ECO Produção", DatabaseEnvironment.Production, "eco_core");
        var development = Connection("ECO Desenvolvimento", DatabaseEnvironment.Development, "eco_dev");
        var copy = DatabaseCopyProfile.Create(
            "Prod → Dev", production.Id, development.Id, null, DatabaseCopyOptions.Default with { RequireAnonymization = false }, Now);
        await SeedAsync(db, production, development, copy);

        await using var write = db.CreateContext();
        var copies = new DatabaseCopyProfileRepository(write);
        (await copies.AnyUsesConnectionAsync(development.Id, Ct)).Should().BeTrue();
        (await copies.AnyUsesConnectionAsync(Guid.CreateVersion7(), Ct)).Should().BeFalse();

        var connections = new DatabaseConnectionRepository(write);
        connections.Remove((await connections.FindByIdAsync(development.Id, Ct))!);

        await FluentActions.Awaiting(() => write.SaveChangesAsync(Ct)).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ACopyProfile_RoundTrips()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var production = Connection("ECO Produção", DatabaseEnvironment.Production, "eco_core");
        var masked = Connection("ECO Produção (anon)", DatabaseEnvironment.Production, "eco_core");
        var development = Connection("ECO Desenvolvimento", DatabaseEnvironment.Development, "eco_dev");
        var anonymization = AnonymizationProfile.Create("ECO LGPD", null, masked.Id, Now);
        var options = DatabaseCopyOptions.Default with { KeepAnonymizedArtifact = true };
        var copy = DatabaseCopyProfile.Create("ECO Production → ECO Development", production.Id, development.Id, anonymization.Id, options, Now);
        await SeedAsync(db, production, masked, development, anonymization, copy);

        await using var read = db.CreateContext();
        var copies = new DatabaseCopyProfileRepository(read);
        var reloaded = await copies.FindByIdAsync(copy.Id, Ct);

        reloaded!.Options.Should().Be(options);
        reloaded.AnonymizationProfileId.Should().Be(anonymization.Id);
        (await copies.ListAsync(Ct)).Should().ContainSingle();
        (await copies.AnyUsesAnonymizationProfileAsync(anonymization.Id, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task TheAudit_SurvivesTheDeletedConnection_AndRunningOnesAreFound()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var development = Connection("ECO Desenvolvimento", DatabaseEnvironment.Development, "eco_dev");
        var finished = DatabaseOperationAudit.Start(DatabaseOperationType.Restore, null, development.Snapshot(), null, null, "PC", "adriano", Now);
        finished.RecordTools("pg_restore 17.2");
        finished.RecordSizes(null, 4096);
        finished.Succeed(Now.AddMinutes(2));
        var running = DatabaseOperationAudit.Start(DatabaseOperationType.Copy, development.Snapshot(), null, null, null, "PC", "adriano", Now.AddHours(1));
        await SeedAsync(db, development, finished, running);

        await using (var write = db.CreateContext())
        {
            write.Remove((await write.DatabaseConnections.SingleAsync(Ct))!);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var log = new EfDatabaseOperationAuditLog(read);
        var recent = await log.ListRecentAsync(10, Ct);

        recent.Select(audit => audit.Id).Should().Equal(running.Id, finished.Id);
        var reloaded = recent[1];
        reloaded.DestinationConnectionName.Should().Be("ECO Desenvolvimento");
        reloaded.Duration.Should().Be(TimeSpan.FromMinutes(2));
        reloaded.AnonymousDumpSize.Should().Be(4096);
        reloaded.Status.Should().Be(DatabaseOperationStatus.Succeeded);
        (await log.ListRunningAsync(Ct)).Select(audit => audit.Id).Should().Equal(running.Id);

        await log.RecordAsync(DatabaseOperationAudit.Start(DatabaseOperationType.Diagnose, null, null, null, null, "PC", "u", Now), Ct);
        await read.SaveChangesAsync(Ct);
        (await log.ListRecentAsync(1, Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task AServerOnlyConnection_RoundTrips_WithoutADatabase()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var server = Connection("ECO Servidor", DatabaseEnvironment.Production, null);
        await SeedAsync(db, server);

        await using var read = db.CreateContext();
        var reloaded = await new DatabaseConnectionRepository(read).FindByIdAsync(server.Id, Ct);

        reloaded!.Database.Should().BeNull();
        reloaded.Snapshot().EndpointKey.Should().Be("192.168.15.112:5432/*");
    }

    [Fact]
    public async Task AnAlias_RoundTrips_AndHoldsItsConnectionAndAnonymization()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var server = Connection("ECO Servidor", DatabaseEnvironment.Production, null);
        var masked = Connection("ECO Servidor (anon)", DatabaseEnvironment.Production, null);
        var anonymization = AnonymizationProfile.Create("ECO 1010 LGPD", null, masked.Id, Now);
        var saved = SavedDatabase.Create("lock_eco_core_1010", server.Id, "eco_core_1010", anonymization.Id, Now);
        await SeedAsync(db, server, masked, anonymization, saved);

        await using (var read = db.CreateContext())
        {
            var aliases = new SavedDatabaseRepository(read);
            var reloaded = await aliases.FindByIdAsync(saved.Id, Ct);

            reloaded!.Alias.Should().Be("lock_eco_core_1010");
            reloaded.DatabaseName.Should().Be("eco_core_1010");
            reloaded.ConnectionId.Should().Be(server.Id);
            reloaded.AnonymizationProfileId.Should().Be(anonymization.Id);
            reloaded.CreatedAt.Should().Be(Now);
            (await aliases.ListAsync(Ct)).Should().ContainSingle();
            (await aliases.AliasExistsAsync("LOCK_ECO_CORE_1010", null, Ct)).Should().BeTrue();
            (await aliases.AliasExistsAsync("lock_eco_core_1010", saved.Id, Ct)).Should().BeFalse();
            (await aliases.AnyUsesConnectionAsync(server.Id, Ct)).Should().BeTrue();
            (await aliases.AnyUsesConnectionAsync(masked.Id, Ct)).Should().BeFalse();
            (await aliases.AnyUsesAnonymizationProfileAsync(anonymization.Id, Ct)).Should().BeTrue();
        }

        // Restrict: a conexão não sai por baixo do apelido.
        await using (var write = db.CreateContext())
        {
            write.Remove((await write.DatabaseConnections.SingleAsync(connection => connection.Id == server.Id, Ct))!);
            await FluentActions.Awaiting(() => write.SaveChangesAsync(Ct)).Should().ThrowAsync<DbUpdateException>();
        }

        await using (var write = db.CreateContext())
        {
            var aliases = new SavedDatabaseRepository(write);
            aliases.Remove((await aliases.FindByIdAsync(saved.Id, Ct))!);
            await write.SaveChangesAsync(Ct);
        }

        await using var check = db.CreateContext();
        (await check.SavedDatabases.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task TheAliasIsUnique_IgnoringCase()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var server = Connection("ECO Servidor", DatabaseEnvironment.Production, null);
        await SeedAsync(db, server, SavedDatabase.Create("eco", server.Id, "eco_core_1010", null, Now));

        await FluentActions.Awaiting(() => SeedAsync(db, SavedDatabase.Create("eco", server.Id, "eco_core_2020", null, Now)))
            .Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ACopyProfileAndTheAudit_KeepTheChosenDatabases()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var server = Connection("ECO Servidor", DatabaseEnvironment.Production, null);
        var local = Connection("Local", DatabaseEnvironment.Development, null);
        var options = DatabaseCopyOptions.Default with { RequireAnonymization = false };
        var copy = DatabaseCopyProfile.Create("ECO 1010 → Local", server.Id, local.Id, null, options, Now, "eco_core_1010");
        var audit = DatabaseOperationAudit.Start(
            DatabaseOperationType.Copy,
            server.Snapshot().WithDatabase("eco_core_1010"),
            local.Snapshot().WithDatabase("lock_eco_core_1010_20261008_184102"),
            copy.Id, copy.Name, "PC", "adriano", Now);
        await SeedAsync(db, server, local, copy, audit);

        await using var read = db.CreateContext();
        (await new DatabaseCopyProfileRepository(read).FindByIdAsync(copy.Id, Ct))!.SourceDatabase.Should().Be("eco_core_1010");
        var reloaded = (await new EfDatabaseOperationAuditLog(read).ListRecentAsync(1, Ct)).Single();
        reloaded.SourceDatabase.Should().Be("eco_core_1010");
        reloaded.DestinationDatabase.Should().Be("lock_eco_core_1010_20261008_184102");
    }

    [Fact]
    public async Task UpgradingFromDatabaseOperations_KeepsEveryConnectionsDatabase_AndItsProfiles()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("DatabaseOperations", Ct);
        var production = Connection("ECO Produção", DatabaseEnvironment.Production, "eco_core");
        var masked = Connection("ECO Produção (anon)", DatabaseEnvironment.Production, "eco_core");
        await SeedAsync(db, production, masked);
        await InsertOldProfileAsync(db, Guid.CreateVersion7(), masked.Id, []);

        // A coluna Database vira opcional: no SQLite, a tabela é recriada.
        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        var connections = await read.DatabaseConnections.OrderBy(connection => connection.Name).ToListAsync(Ct);
        connections.Select(connection => connection.Database).Should().Equal("eco_core", "eco_core");
        (await read.AnonymizationProfiles.SingleAsync(Ct)).ConnectionId.Should().Be(masked.Id);
        (await read.SavedDatabases.CountAsync(Ct)).Should().Be(0);

        await using var command = read.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        await read.Database.OpenConnectionAsync(Ct);
        await using var violations = await command.ExecuteReaderAsync(Ct);
        (await violations.ReadAsync(Ct)).Should().BeFalse("a recriação da tabela não pode deixar chave órfã");
    }

    [Fact]
    public async Task UpgradingFromSavedDatabases_TurnsTheAnonymizerExpressions_IntoCatalogMasks()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("SavedDatabases", Ct);
        var source = Connection("ECO Produção", DatabaseEnvironment.Production, "eco_core");
        await SeedAsync(db, source);
        var profileId = Guid.CreateVersion7();
        await InsertOldProfileAsync(db, profileId, source.Id,
        [
            ("email", 1, "anon.partial_email(email)"),
            ("cpf", 1, "anon.partial(cpf,0,$$*********$$,2)"),
            ("telefone", 1, "anon.partial(telefone,2,$$*******$$,2)"),
            ("nome", 1, "anon.dummy_first_name()"),
            ("nascimento", 1, "anon.random_date()"),
            ("salario", 1, "anon.noise(salario, 0.2)"),
            ("cidade", 1, "anon.dummy_city_name()"),
            ("ip", 2, "NULL"),
            ("obs", 2, "'d''Ávila'"),
            ("nota", 2, "0"),
        ]);

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        var rules = (await read.AnonymizationProfiles.Include(profile => profile.Rules).SingleAsync(Ct)).Rules
            .ToDictionary(rule => rule.Column, rule => (rule.Method, rule.Argument));

        rules.Should().BeEquivalentTo(new Dictionary<string, (MaskingMethod, string?)>
        {
            ["email"] = (MaskingMethod.FakeEmail, null),
            ["cpf"] = (MaskingMethod.Partial, "0,2"),
            ["telefone"] = (MaskingMethod.Partial, "2,2"),
            ["nome"] = (MaskingMethod.FakeName, null),
            ["nascimento"] = (MaskingMethod.DateShift, "365"),
            ["salario"] = (MaskingMethod.NumberNoise, "20"),
            ["cidade"] = (MaskingMethod.Hash, null),
            ["ip"] = (MaskingMethod.Null, null),
            ["obs"] = (MaskingMethod.FixedText, "d'Ávila"),
            ["nota"] = (MaskingMethod.FixedNumber, "0"),
        });
    }

    /// <summary>Um perfil como as versões com o PostgreSQL Anonymizer gravavam: com política e expressões.</summary>
    private static async Task InsertOldProfileAsync(
        TempSqliteDatabase db,
        Guid profileId,
        Guid connectionId,
        IReadOnlyList<(string Column, int Kind, string Expression)> rules)
    {
        await using var context = db.CreateContext();
        var ticks = Now.UtcTicks;

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"AnonymizationProfiles\" (\"Id\", \"Name\", \"Description\", \"ConnectionId\", \"PolicyName\", \"IsEnabled\", \"CreatedAt\", \"UpdatedAt\") " +
            "VALUES ({0}, 'ECO LGPD', NULL, {1}, 'anon', 1, {2}, {2})",
            [profileId.ToString().ToUpperInvariant(), connectionId.ToString().ToUpperInvariant(), ticks],
            Ct);

        foreach (var (column, kind, expression) in rules)
        {
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"AnonymizationRules\" (\"Id\", \"ProfileId\", \"Schema\", \"Table\", \"Column\", \"Kind\", \"Expression\", \"Sensitivity\", \"ConfirmedAt\") " +
                "VALUES ({0}, {1}, 'public', 'clientes', {2}, {3}, {4}, 3, {5})",
                [Guid.CreateVersion7().ToString().ToUpperInvariant(), profileId.ToString().ToUpperInvariant(), column, kind, expression, ticks],
                Ct);
        }
    }

    [Fact]
    public async Task UpgradingFromDirectoryOnlyCommands_KeepsTheData_AndStartsWithNoConnections()
    {
        await using var db = await new TempSqliteDatabase().MigrateToAsync("DirectoryOnlyCommands", Ct);
        var task = TaskItem.Create("Copiar a base de produção", Now);
        await SeedAsync(db, task);

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        (await read.Tasks.SingleAsync(Ct)).Title.Should().Be("Copiar a base de produção");
        (await read.DatabaseConnections.CountAsync(Ct)).Should().Be(0);
        (await read.DatabaseOperationAudits.CountAsync(Ct)).Should().Be(0);
    }
}
