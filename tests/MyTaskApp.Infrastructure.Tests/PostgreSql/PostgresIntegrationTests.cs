using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Infrastructure.PostgreSql;
using MyTaskApp.Infrastructure.Processes;
using Npgsql;

namespace MyTaskApp.Infrastructure.Tests.PostgreSql;

/// <summary>
/// Contra um PostgreSQL de verdade (ADR-056) — só quando
/// <c>MYTASKAPP_TEST_POSTGRES</c> traz uma connection string de um servidor
/// descartável (um container, por exemplo), com usuário que possa criar bancos.
/// No CI não há servidor, e o teste se dispensa.
/// </summary>
/// <remarks>
/// Cria dois bancos de desenvolvimento, faz a cópia com as ferramentas
/// instaladas nesta máquina e confere com os inspetores — o caminho real de
/// tudo, menos o Anonymizer, que depende da extensão no servidor.
/// </remarks>
public sealed class PostgresIntegrationTests : IAsyncLifetime
{
    public const string Variable = "MYTASKAPP_TEST_POSTGRES";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string? _connectionString = Environment.GetEnvironmentVariable(Variable);

    private readonly string _source = $"mytaskapp_src_{Guid.NewGuid():N}"[..30];

    private readonly string _destination = $"mytaskapp_dst_{Guid.NewGuid():N}"[..30];

    private NpgsqlConnectionStringBuilder? _admin;

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        _admin = new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false };
        await ExecuteAsync(_admin.Database ?? "postgres", $"CREATE DATABASE {_source}");
        await ExecuteAsync(_source,
            "CREATE TABLE clientes (id serial PRIMARY KEY, email text NOT NULL, criado timestamptz DEFAULT now());" +
            "CREATE INDEX ix_clientes_email ON clientes (email);" +
            "INSERT INTO clientes (email) SELECT 'pessoa' || g || '@exemplo.com' FROM generate_series(1, 250) g;");
    }

    public async ValueTask DisposeAsync()
    {
        if (_admin is null)
        {
            return;
        }

        foreach (var database in (string[])[_source, _destination])
        {
            await ExecuteAsync(_admin.Database ?? "postgres", $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }

    [Fact]
    public async Task ADevelopmentCopy_RunsThroughTheRealTools_AndVerifies()
    {
        Assert.SkipWhen(_admin is null, $"Sem {Variable}: nenhum PostgreSQL de teste configurado.");

        var options = new DatabaseOperationsOptions();
        var passwords = new StaticPasswordReader(_admin!.Password);
        var locator = PostgresToolLocator.ForCurrentSystem(new ProcessRunner(), options, NullLogger<PostgresToolLocator>.Instance);
        var tools = await locator.DetectAsync(refresh: true, Ct);
        Assert.SkipUnless(tools.Find(PostgresTool.PgDump).Found && tools.Find(PostgresTool.PgRestore).Found, "pg_dump/pg_restore não instalados.");

        var runner = new PgToolRunner(new ProcessRunner(), locator, passwords, options, NullLogger<PgToolRunner>.Instance);
        var inspector = new PostgresServerInspector(
            new NpgsqlPostgresSessionFactory(passwords, options, NullLogger<NpgsqlPostgresSessionFactory>.Instance),
            NullLogger<PostgresServerInspector>.Instance);
        var source = Snapshot(_source);
        var destination = Snapshot(_destination);
        var archive = Path.Combine(Path.GetTempPath(), $"mytaskapp-it-{Guid.NewGuid():N}");

        try
        {
            var test = await inspector.TestAsync(source, cancellationToken: Ct);
            test.Connected.Should().BeTrue(test.Error);

            var dump = await new PostgresDumpService(runner, options).DumpAsync(
                new PgDumpRequest(source, Path.Combine(archive, "archive"), false, true, true, 2, []), null, Ct);
            dump.Succeeded.Should().BeTrue(dump.ErrorTail);

            var listing = await new PostgresDumpService(runner, options).ListArchiveAsync(Path.Combine(archive, "archive"), Ct);
            listing.Tables.Should().Contain("public.clientes");

            var restores = new PostgresRestoreService(runner, options);
            (await restores.CreateDatabaseAsync(destination, [], Ct)).Succeeded.Should().BeTrue();
            (await restores.RestoreAsync(new PgRestoreRequest(destination, Path.Combine(archive, "archive"), true, true, 2, []), null, Ct))
                .Succeeded.Should().BeTrue();

            VerificationEvaluator.CompareStructure(
                    await inspector.GetStructureAsync(source, Ct),
                    await inspector.GetStructureAsync(destination, Ct))
                .Should().OnlyContain(check => check.Outcome == CheckOutcome.Pass);

            var tables = new[] { "public.clientes" };
            VerificationEvaluator.CompareRows(
                    await inspector.CountRowsAsync(source, tables, 1_000_000, Ct),
                    await inspector.CountRowsAsync(destination, tables, 1_000_000, Ct))
                .Outcome.Should().Be(CheckOutcome.Pass);

            // Sem máscara, a coluna chega igual: é exatamente o que a verificação acusa.
            var column = new ColumnReference("public", "clientes", "email");
            VerificationEvaluator.CompareSensitive(
                    await inspector.FingerprintAsync(source, column, "sal", 100, Ct),
                    await inspector.FingerprintAsync(destination, column, "sal", 100, Ct))
                .Outcome.Should().Be(CheckOutcome.Fail);
        }
        finally
        {
            if (Directory.Exists(archive))
            {
                Directory.Delete(archive, recursive: true);
            }
        }
    }

    private DatabaseConnectionSnapshot Snapshot(string database) =>
        DatabaseConnection.Create(
                database, _admin!.Host!, _admin.Port, database, _admin.Username!, DatabaseEnvironment.Development,
                DatabaseSslMode.Prefer, null, ConnectionPermissions.FromFlags(EnvironmentPolicy.For(DatabaseEnvironment.Development).Defaults),
                PostgresTestSupport.Now)
            .Snapshot();

    private async Task ExecuteAsync(string database, string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(_admin!.ConnectionString) { Database = database };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
