using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
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
/// Cria os bancos de teste, faz a cópia com as ferramentas instaladas nesta
/// máquina e confere com os inspetores: a cópia comum e a mascarada na
/// consulta (ADR-058), com partição, coluna gerada, FK e sequence.
/// </remarks>
public sealed class PostgresIntegrationTests : IAsyncLifetime
{
    public const string Variable = "MYTASKAPP_TEST_POSTGRES";

    /// <summary>
    /// Opcional: a pasta <c>bin</c> das ferramentas a usar. Sem ela, vale o localizador do app, que
    /// prefere o <c>pg_dump</c> mais novo — e um <c>pg_restore</c> 17+ não restaura num servidor mais antigo.
    /// </summary>
    public const string ToolsVariable = "MYTASKAPP_TEST_PG_BIN";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string? _connectionString = Environment.GetEnvironmentVariable(Variable);

    private readonly string _source = $"mytaskapp_src_{Guid.NewGuid():N}"[..30];

    private readonly string _destination = $"mytaskapp_dst_{Guid.NewGuid():N}"[..30];

    private readonly string _masked = $"mytaskapp_msk_{Guid.NewGuid():N}"[..30];

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
            "CREATE TABLE clientes (id serial PRIMARY KEY, email text NOT NULL, cpf varchar(14), nome text, " +
            "nascimento date, nome_busca text GENERATED ALWAYS AS (lower(nome)) STORED, criado timestamptz DEFAULT now());" +
            "CREATE UNIQUE INDEX ux_clientes_email ON clientes (email);" +
            "CREATE INDEX ix_clientes_email ON clientes (email);" +
            "INSERT INTO clientes (email, cpf, nome, nascimento) SELECT 'pessoa' || g || '@exemplo.com', " +
            "lpad(g::text, 3, '0') || '.456.789-09', 'Fulano ' || g, date '1980-01-01' + g FROM generate_series(1, 250) g;" +
            "CREATE TABLE pedidos (id serial, cliente_id int NOT NULL REFERENCES clientes (id), criado date NOT NULL, valor numeric(10,2), " +
            "PRIMARY KEY (id, criado)) PARTITION BY RANGE (criado);" +
            "CREATE TABLE pedidos_2026 PARTITION OF pedidos FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');" +
            "INSERT INTO pedidos (cliente_id, criado, valor) SELECT 1 + g % 250, date '2026-01-01' + g % 300, g * 1.5 FROM generate_series(1, 600) g;" +
            "CREATE TABLE auditoria (id serial PRIMARY KEY, cliente_id int REFERENCES clientes (id), ip inet NOT NULL);" +
            "INSERT INTO auditoria (cliente_id, ip) SELECT 1 + g % 250, '198.51.100.1' FROM generate_series(1, 100) g;");
    }

    public async ValueTask DisposeAsync()
    {
        if (_admin is null)
        {
            return;
        }

        foreach (var database in (string[])[_source, _destination, _masked])
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
        var locator = Locator(options);
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
                new PgDumpRequest(source, Path.Combine(archive, "archive"), true, true, 2, [], SourceVersion: test.ServerVersion), null, Ct);
            dump.Succeeded.Should().BeTrue(dump.ErrorTail);

            var listing = await new PostgresDumpService(runner, options).ListArchiveAsync(Path.Combine(archive, "archive"), Ct);
            listing.Tables.Should().Contain("public.clientes");

            var restores = new PostgresRestoreService(runner, options);
            (await restores.CreateDatabaseAsync(destination, [], Ct)).Succeeded.Should().BeTrue();
            var restored = await restores.RestoreAsync(new PgRestoreRequest(destination, Path.Combine(archive, "archive"), true, true, 2, [], SourceVersion: test.ServerVersion), null, Ct);
            restored.Succeeded.Should().BeTrue(restored.ErrorTail);

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
                    await inspector.FingerprintAsync(source, column, "sal", 100, cancellationToken: Ct),
                    await inspector.FingerprintAsync(destination, column, "sal", 100, cancellationToken: Ct))
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

    [Fact]
    public async Task AMaskedCopy_ReadsOnlyMaskedDataFromTheSource_AndKeepsKeysAndPartitions()
    {
        Assert.SkipWhen(_admin is null, $"Sem {Variable}: nenhum PostgreSQL de teste configurado.");

        var options = new DatabaseOperationsOptions();
        var passwords = new StaticPasswordReader(_admin!.Password);
        var locator = Locator(options);
        var tools = await locator.DetectAsync(refresh: true, Ct);
        Assert.SkipUnless(tools.Find(PostgresTool.PgDump).Found && tools.Find(PostgresTool.PgRestore).Found, "pg_dump/pg_restore não instalados.");

        var runner = new PgToolRunner(new ProcessRunner(), locator, passwords, options, NullLogger<PgToolRunner>.Instance);
        var sessions = new NpgsqlPostgresSessionFactory(passwords, options, NullLogger<NpgsqlPostgresSessionFactory>.Instance);
        var copier = new NpgsqlMaskedCopier(sessions, passwords, options, NullLogger<NpgsqlMaskedCopier>.Instance);
        var source = Snapshot(_source);
        var destination = Snapshot(_masked);
        var archive = Path.Combine(Path.GetTempPath(), $"mytaskapp-it-{Guid.NewGuid():N}", "archive");

        var profile = AnonymizationProfile.Create("LGPD", null, Guid.CreateVersion7(), PostgresTestSupport.Now);
        profile.ReplaceRules(
        [
            new AnonymizationRuleSpec("public", "clientes", "email", MaskingMethod.FakeEmail, null, ColumnSensitivity.High),
            new AnonymizationRuleSpec("public", "clientes", "cpf", MaskingMethod.Partial, "0,2", ColumnSensitivity.High),
            new AnonymizationRuleSpec("public", "clientes", "nome", MaskingMethod.FakeName, null, ColumnSensitivity.Medium),
            new AnonymizationRuleSpec("public", "clientes", "nascimento", MaskingMethod.DateShift, "30", ColumnSensitivity.Medium),
            new AnonymizationRuleSpec("public", "pedidos", "valor", MaskingMethod.NumberNoise, "20", ColumnSensitivity.Low),
        ], PostgresTestSupport.Now);
        profile.ReplaceSkippedTables([new SkippedTableSpec("public", "auditoria")], PostgresTestSupport.Now);
        profile.ReplaceRowFilters([new RowFilterSpec("public", "clientes", "email = 'pessoa2@exemplo.com'")], PostgresTestSupport.Now);
        var inspector = new PostgresServerInspector(sessions, NullLogger<PostgresServerInspector>.Instance);

        try
        {
            // Como a cópia: a versão da origem escolhe o conjunto de ferramentas.
            var version = (await inspector.TestAsync(source, cancellationToken: Ct)).ServerVersion;
            var catalog = await copier.ReadCatalogAsync(source, Ct);
            var validation = await UnmaskedRows.CountAsync(inspector, source, MaskingPlanner.Plan(profile, catalog), Ct);
            validation.Problems.Should().BeEmpty();
            validation.UnmaskedRows.Should().Equal(new UnmaskedRowCount("public.clientes", "email = 'pessoa2@exemplo.com'", 1));

            // Uma condição que o servidor recusa vira problema antes da cópia, com a mensagem dele.
            var refused = AnonymizationProfile.Create("x", null, Guid.CreateVersion7(), PostgresTestSupport.Now);
            refused.ReplaceRules([new AnonymizationRuleSpec("public", "clientes", "email", MaskingMethod.FakeEmail, null, ColumnSensitivity.High)], PostgresTestSupport.Now);
            refused.ReplaceRowFilters([new RowFilterSpec("public", "clientes", "nao_existe = 1")], PostgresTestSupport.Now);
            (await UnmaskedRows.CountAsync(inspector, source, MaskingPlanner.Plan(refused, catalog), Ct)).Problems
                .Should().ContainSingle().Which.Should().Contain("42703");

            // Na origem só-leitura, nem uma função que escreve passa.
            refused.ReplaceRowFilters([new RowFilterSpec("public", "clientes", "nextval('clientes_id_seq') > 0")], PostgresTestSupport.Now);
            (await UnmaskedRows.CountAsync(inspector, source, MaskingPlanner.Plan(refused, catalog), Ct)).Problems
                .Should().ContainSingle().Which.Should().Contain("25006");
            validation.Tables.Select(table => table.QualifiedName).Should().BeEquivalentTo(["public.auditoria", "public.clientes", "public.pedidos_2026"]);
            validation.Tables.Single(table => table.Table == "auditoria").SkipData.Should().BeTrue();

            // As FKs vêm do catálogo de verdade: clientes sem dados quebraria pedidos (pela partição) e auditoria.
            var withoutClients = AnonymizationProfile.Create("x", null, Guid.CreateVersion7(), PostgresTestSupport.Now);
            withoutClients.ReplaceSkippedTables([new SkippedTableSpec("public", "clientes")], PostgresTestSupport.Now);
            MaskingPlanner.Plan(withoutClients, catalog).Problems.Should().BeEquivalentTo(
            [
                "public.auditoria tem FK para public.clientes, que está sem dados: marque public.auditoria também, ou copie os dados de public.clientes.",
                "public.pedidos tem FK para public.clientes, que está sem dados: marque public.pedidos também, ou copie os dados de public.clientes.",
            ]);
            validation.Tables.Single(table => table.Table == "pedidos_2026").Columns.Single(column => column.Name == "valor").IsMasked
                .Should().BeTrue("a regra da tabela particionada vale para a partição");

            var preview = await copier.PreviewAsync(source, validation.Tables, 300, Ct);
            preview.Single(column => column.Column == "email").Values.Should().HaveCount(249, "a linha sem máscara fica fora da pré-visualização")
                .And.OnlyContain(value => value!.EndsWith("@exemplo.invalid"));

            await using (var session = await copier.OpenAsync(source, Ct))
            {
                var dumps = new PostgresDumpService(runner, options);
                (await dumps.DumpAsync(new PgDumpRequest(source, archive, true, false, 1, [], session.SnapshotId, version), null, Ct))
                    .Succeeded.Should().BeTrue();
                (await dumps.ListArchiveAsync(archive, Ct)).TableDataEntries.Should().Be(0, "só a estrutura vai para o disco");

                var restores = new PostgresRestoreService(runner, options);
                (await restores.CreateDatabaseAsync(destination, [], Ct)).Succeeded.Should().BeTrue();
                var preData = await restores.RestoreAsync(new PgRestoreRequest(destination, archive, true, false, 1, [], RestoreSection.PreData, version), null, Ct);
                preData.Succeeded.Should().BeTrue(preData.ErrorTail);

                await session.ConnectDestinationAsync(destination, [], Ct);

                foreach (var table in validation.Tables.Where(table => !table.SkipData))
                {
                    (await session.CopyTableAsync(table, null, Ct)).Should().BeGreaterThan(0);
                }

                var postData = await restores.RestoreAsync(new PgRestoreRequest(destination, archive, true, false, 1, [], RestoreSection.PostData, version), null, Ct);
                postData.Succeeded.Should().BeTrue("índice único, PK e FK aceitam os dados mascarados: " + postData.ErrorTail);
                (await session.CopySequencesAsync(Ct)).Should().BeGreaterThan(0);
            }

            VerificationEvaluator.CompareRows(
                    await inspector.CountRowsAsync(source, ["public.clientes", "public.pedidos"], 1_000_000, Ct),
                    await inspector.CountRowsAsync(destination, ["public.clientes", "public.pedidos"], 1_000_000, Ct))
                .Outcome.Should().Be(CheckOutcome.Pass);
            VerificationEvaluator.CompareSkipped(await inspector.CountRowsAsync(destination, ["public.auditoria"], 1_000_000, Ct))
                .Outcome.Should().Be(CheckOutcome.Pass, "a tabela sem dados existe no destino, vazia");
            (await QueryRowAsync(_masked, "SELECT count(*) FROM pg_constraint WHERE conrelid = 'auditoria'::regclass AND contype = 'f'"))[0]
                .Should().Be(1L, "a FK da tabela vazia também é criada");

            var row = await QueryRowAsync(_masked,
                "SELECT email, cpf, nome, nome_busca, nextval('clientes_id_seq') FROM clientes WHERE id = 1");
            ((string)row[0]!).Should().MatchRegex("^user_[0-9a-f]{12}@exemplo\\.invalid$");
            row[1].Should().Be("************09");
            ((string)row[2]!).Should().NotStartWith("Fulano");
            row[3].Should().Be(((string)row[2]!).ToLowerInvariant(), "a coluna gerada é recalculada a partir do nome mascarado");
            ((long)row[4]!).Should().BeGreaterThan(250, "a sequence continua de onde a origem parou");

            // A linha do filtro (ADR-080) chega com os valores reais; a verificação a deixa de fora.
            var kept = await QueryRowAsync(_masked, "SELECT email, cpf, nome FROM clientes WHERE id = 2");
            kept.Should().Equal("pessoa2@exemplo.com", "002.456.789-09", "Fulano 2");
            var email = new ColumnReference("public", "clientes", "email");
            VerificationEvaluator.CompareSensitive(
                    await inspector.FingerprintAsync(source, email, "sal", 1000, "email = 'pessoa2@exemplo.com'", Ct),
                    await inspector.FingerprintAsync(destination, email, "sal", 1000, cancellationToken: Ct))
                .Should().Match<CheckResult>(check => check.Outcome == CheckOutcome.Pass && check.Detail!.StartsWith("249 de 249"));

            // Nada foi instalado nem marcado na origem.
            (await QueryRowAsync(_source, "SELECT count(*) FROM pg_extension WHERE extname = 'anon'"))[0].Should().Be(0L);
            (await QueryRowAsync(_source, "SELECT count(*) FROM pg_seclabel"))[0].Should().Be(0L);
        }
        finally
        {
            var root = Path.GetDirectoryName(archive)!;

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ThePostCopyScript_RunsInOneTransaction_AndAFailureUndoesAllOfIt()
    {
        Assert.SkipWhen(_admin is null, $"Sem {Variable}: nenhum PostgreSQL de teste configurado.");

        await ExecuteAsync(_admin!.Database ?? "postgres", $"CREATE DATABASE {_destination}");
        await ExecuteAsync(_destination,
            "CREATE TABLE config (chave text PRIMARY KEY, valor text NOT NULL);" +
            "INSERT INTO config VALUES ('url', 'https://producao.invalid'), ('email', 'equipe@producao.invalid');");

        var runner = new NpgsqlScriptRunner(
            new StaticPasswordReader(_admin.Password), new DatabaseOperationsOptions(), NullLogger<NpgsqlScriptRunner>.Instance);
        var target = Snapshot(_destination, ConnectionPermission.All & ~ConnectionPermission.RequireAnonymization);
        var notices = new List<string>();

        // Texto com @ e $1 dentro de aspas, e um bloco DO com dollar-quoting: o Npgsql não pode confundir com parâmetro.
        var run = await runner.RunAsync(target,
            "update config set valor = 'https://dev.local/$1' where chave = 'url';\n" +
            "update config set valor = 'dev@empresa.invalid' where chave = 'email';\n" +
            "do $$ begin raise notice 'ajustado: %', (select count(*) from config); end $$;",
            [], new SynchronousProgress<string>(notices.Add), Ct);

        run.RowsAffected.Should().Be(2);
        notices.Should().ContainSingle().Which.Should().Be("NOTICE: ajustado: 2");
        (await QueryRowAsync(_destination, "SELECT string_agg(valor, ',' ORDER BY chave) FROM config"))[0]
            .Should().Be("dev@empresa.invalid,https://dev.local/$1");

        var failure = await FluentActions.Awaiting(() => runner.RunAsync(target,
                "update config set valor = 'apagado';\nselect * from nao_existe;", [], null, Ct))
            .Should().ThrowAsync<DomainException>();
        failure.Which.Message.Should().Contain("42P01").And.Contain("desfeito");
        (await QueryRowAsync(_destination, "SELECT count(*) FROM config WHERE valor = 'apagado'"))[0]
            .Should().Be(0L, "o update de antes do erro foi desfeito junto");

        // O domínio recusa COMMIT ao salvar; um que chegasse aqui vira mensagem, e não "erro inesperado".
        await FluentActions.Awaiting(() => runner.RunAsync(target, "update config set valor = 'x' where chave = 'url'; commit;", [], null, Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*encerrou a transação*");

        // Sem "Executar SQL", recusa antes de conectar.
        await FluentActions.Awaiting(() => runner.RunAsync(Snapshot(_destination), "select 1", [], null, Ct))
            .Should().ThrowAsync<DatabaseSecurityException>();
    }

    private async Task<object?[]> QueryRowAsync(string database, string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(_admin!.ConnectionString) { Database = database };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var row = new object?[reader.FieldCount];

        for (var index = 0; index < row.Length; index++)
        {
            row[index] = await reader.IsDBNullAsync(index) ? null : reader.GetValue(index);
        }

        return row;
    }

    private static IPostgresToolLocator Locator(DatabaseOperationsOptions options)
    {
        if (Environment.GetEnvironmentVariable(ToolsVariable) is not { Length: > 0 } bin)
        {
            return PostgresToolLocator.ForCurrentSystem(new ProcessRunner(), options, NullLogger<PostgresToolLocator>.Instance);
        }

        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return new StaticToolLocator(new PostgresClientTools(
            Enum.GetValues<PostgresTool>()
                .Select(tool => new PostgresToolStatus(tool, PostgresToolNames.Of(tool), Path.Combine(bin, PostgresToolNames.Of(tool) + extension), null, null))
                .ToList(),
            new ToolInstallGuide("teste", [], [])));
    }

    private DatabaseConnectionSnapshot Snapshot(string database, ConnectionPermission? permissions = null) =>
        DatabaseConnection.Create(
                database, _admin!.Host!, _admin.Port, database, _admin.Username!, DatabaseEnvironment.Development,
                DatabaseSslMode.Prefer, null,
                ConnectionPermissions.FromFlags(permissions ?? EnvironmentPolicy.For(DatabaseEnvironment.Development).Defaults),
                PostgresTestSupport.Now)
            .Snapshot();

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private async Task ExecuteAsync(string database, string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(_admin!.ConnectionString) { Database = database };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
