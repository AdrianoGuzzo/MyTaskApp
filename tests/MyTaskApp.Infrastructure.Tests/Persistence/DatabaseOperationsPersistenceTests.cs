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

    private static DatabaseConnection Connection(string name, DatabaseEnvironment environment, string database) =>
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
        var profile = AnonymizationProfile.Create("ECO LGPD", "LGPD", masked.Id, "anon", Now);
        profile.ReplaceRules(
        [
            new AnonymizationRuleSpec("public", "clientes", "email", MaskingKind.Function, "anon.fake_email()", ColumnSensitivity.High),
            new AnonymizationRuleSpec("public", "clientes", "obs", MaskingKind.Value, "NULL", ColumnSensitivity.Low),
        ], Now);
        await SeedAsync(db, masked, profile);

        await using (var read = db.CreateContext())
        {
            var profiles = new AnonymizationProfileRepository(read);
            var reloaded = await profiles.FindByIdAsync(profile.Id, Ct);

            reloaded!.Rules.Should().HaveCount(2);
            reloaded.Rules.Select(rule => rule.QualifiedName).Should().BeEquivalentTo("public.clientes.email", "public.clientes.obs");
            reloaded.Rules.Single(rule => rule.Column == "email").Sensitivity.Should().Be(ColumnSensitivity.High);
            (await profiles.ListAsync(Ct)).Single().Rules.Should().HaveCount(2);
            (await profiles.AnyUsesConnectionAsync(masked.Id, Ct)).Should().BeTrue();

            reloaded.ReplaceRules([new AnonymizationRuleSpec("public", "clientes", "cpf", MaskingKind.Function, "anon.hash(cpf)", ColumnSensitivity.High)], Now);
            await read.SaveChangesAsync(Ct);
        }

        await using (var read = db.CreateContext())
        {
            (await read.Set<AnonymizationRule>().CountAsync(Ct)).Should().Be(1);
            var profiles = new AnonymizationProfileRepository(read);
            profiles.Remove((await profiles.FindByIdAsync(profile.Id, Ct))!);
            await read.SaveChangesAsync(Ct);
        }

        await using var check = db.CreateContext();
        (await check.Set<AnonymizationRule>().CountAsync(Ct)).Should().Be(0);
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
        var anonymization = AnonymizationProfile.Create("ECO LGPD", null, masked.Id, null, Now);
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
