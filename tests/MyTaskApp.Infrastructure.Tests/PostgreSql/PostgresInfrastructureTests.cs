using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Infrastructure.PostgreSql;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.Storage;
using MyTaskApp.Infrastructure.Tests.Jira;
using MyTaskApp.Infrastructure.Tests.Processes;
using static MyTaskApp.Infrastructure.Tests.PostgreSql.PostgresTestSupport;

namespace MyTaskApp.Infrastructure.Tests.PostgreSql;

/// <summary>O localizador das ferramentas, com um disco de mentira (ADR-056).</summary>
public class PostgresToolLocatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ScriptedProcessRunner _processes = new();

    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string[]> _directories = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string?> _variables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ProgramFiles"] = @"C:\Program Files",
        ["LOCALAPPDATA"] = @"C:\Users\u\AppData\Local",
    };

    private PostgresToolLocator Locator(bool isWindows = true) => new(
        (name, _) => _variables.GetValueOrDefault(name),
        _files.Contains,
        directory => _directories.GetValueOrDefault(directory) ?? [],
        isWindows,
        _processes,
        new DatabaseOperationsOptions(),
        NullLogger<PostgresToolLocator>.Instance);

    private void Install(string directory, int major, params string[] tools)
    {
        foreach (var tool in tools)
        {
            var path = Path.Combine(directory, tool);
            _files.Add(path);
            _processes.When(request => request.FileName.Equals(path, StringComparison.OrdinalIgnoreCase),
                ScriptedProcessRunner.Ok($"{Path.GetFileNameWithoutExtension(tool)} (PostgreSQL) {major}.1\n"));
        }
    }

    private static readonly string[] AllWindowsTools = ["psql.exe", "pg_dump.exe", "pg_restore.exe", "pg_isready.exe", "createdb.exe", "dropdb.exe"];

    [Fact]
    public async Task OnWindows_TheNewestInstalledSetWins()
    {
        _directories[@"C:\Program Files\PostgreSQL"] = [@"C:\Program Files\PostgreSQL\14", @"C:\Program Files\PostgreSQL\17"];
        Install(@"C:\Program Files\PostgreSQL\14\bin", 14, AllWindowsTools);
        Install(@"C:\Program Files\PostgreSQL\17\bin", 17, AllWindowsTools);

        var tools = await Locator().DetectAsync(refresh: true, Ct);

        tools.AllFound.Should().BeTrue();
        tools.Tools.Should().OnlyContain(tool => tool.Path!.Contains(@"\17\bin") && tool.Version == new PostgresVersion(17, 1));
        tools.Guide.Platform.Should().Be("Windows");
        tools.Guide.CheckCommands.Should().Contain("where.exe pg_dump").And.Contain("pg_dump --version");
    }

    [Fact]
    public async Task ToolsMissingFromThePreferredFolder_ComeFromElsewhere_AndMissingOnesSaySo()
    {
        _directories[@"C:\Program Files\PostgreSQL"] = [@"C:\Program Files\PostgreSQL\17"];
        Install(@"C:\Program Files\PostgreSQL\17\bin", 17, "pg_dump.exe", "pg_restore.exe");
        Install(@"C:\Program Files\pgAdmin 4\runtime", 16, "psql.exe");

        var tools = await Locator().DetectAsync(refresh: true, Ct);

        tools.Find(PostgresTool.Psql).Path.Should().Contain("pgAdmin");
        tools.Missing.Should().BeEquivalentTo("pg_isready", "createdb", "dropdb");
    }

    [Fact]
    public async Task TheAnswerIsCached_UntilRefreshIsAsked()
    {
        Install(@"C:\Program Files\pgAdmin 4\runtime", 16, "pg_dump.exe");
        var locator = Locator();

        await locator.DetectAsync(refresh: false, Ct);
        var calls = _processes.Requests.Count;
        await locator.DetectAsync(refresh: false, Ct);
        _processes.Requests.Should().HaveCount(calls);

        await locator.DetectAsync(refresh: true, Ct);
        _processes.Requests.Count.Should().BeGreaterThan(calls);
    }

    [Fact]
    public async Task AToolThatFailsItsVersion_IsFoundWithoutVersion()
    {
        _files.Add(@"C:\Program Files\pgAdmin 4\runtime\psql.exe");
        _processes.When(_ => true, ScriptedProcessRunner.Fail(1));

        var psql = (await Locator().DetectAsync(true, Ct)).Find(PostgresTool.Psql);

        psql.Found.Should().BeTrue();
        psql.Version.Should().BeNull();
    }

    [Fact]
    public async Task AToolThatCannotStart_IsFoundWithoutVersion()
    {
        _files.Add(@"C:\Program Files\pgAdmin 4\runtime\pg_dump.exe");
        _processes.StartFailure = new ProcessStartException("pg_dump", new InvalidOperationException());

        (await Locator().DetectAsync(true, Ct)).Find(PostgresTool.PgDump).Version.Should().BeNull();
    }

    [Fact]
    public async Task OnLinux_TheVersionedFoldersComeFirst()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Caminhos Unix só se montam num Unix.");

        _directories["/usr/lib/postgresql"] = ["/usr/lib/postgresql/16", "/usr/lib/postgresql/17"];
        Install("/usr/lib/postgresql/17/bin", 17, "pg_dump", "pg_restore");
        Install("/usr/bin", 15, "pg_dump", "psql");

        var tools = await Locator(isWindows: false).DetectAsync(true, Ct);

        tools.Find(PostgresTool.PgDump).Path.Should().Be("/usr/lib/postgresql/17/bin/pg_dump");
        tools.Find(PostgresTool.Psql).Path.Should().Be("/usr/bin/psql");
        tools.Guide.Platform.Should().Be("Linux");
    }

    [Fact]
    public void TheLinuxGuide_UsesWhichAndTheVersions()
    {
        var guide = PostgresInstallGuides.For(isWindows: false, (_, _) => null);

        guide.CheckCommands.Should().Contain(["which psql", "which pg_dump", "which pg_restore", "which pg_isready", "pg_isready --version"]);
        guide.Steps.Should().NotBeEmpty();
        PostgresInstallGuides.For(false, (_, _) => "rhel fedora").Steps.Should().Contain(step => step.Contains("dnf"));
    }
}

/// <summary>A saída das ferramentas lida para o progresso e para conferir o dump.</summary>
public class PgOutputParsersTests
{
    [Theory]
    [InlineData("pg_dump: dumping contents of table \"public.clientes\"", PgToolEventKind.TableData, "public.clientes")]
    [InlineData("pg_restore: processing data for table \"vendas.pedidos\"", PgToolEventKind.TableData, "vendas.pedidos")]
    [InlineData("pg_restore: finished item 3420 TABLE DATA public pedidos", PgToolEventKind.TableData, "public.pedidos")]
    [InlineData("pg_dump: finished item 3420 TABLE DATA pedidos", PgToolEventKind.TableData, "pedidos")]
    [InlineData("pg_restore: creating INDEX \"public.ix_clientes_email\"", PgToolEventKind.PostData, null)]
    [InlineData("pg_restore: creating FK CONSTRAINT \"public.pedidos fk\"", PgToolEventKind.PostData, null)]
    [InlineData("pg_dump: reading extensions", PgToolEventKind.Line, null)]
    public void VerboseLines_AreRecognized(string text, PgToolEventKind kind, string? table)
    {
        var parsed = PgVerboseOutputParser.Parse(new CommandOutputLine(text, true));

        parsed.Kind.Should().Be(kind);
        parsed.Table.Should().Be(table);
        parsed.Line.Text.Should().Be(text);
    }

    private const string Listing = """
        ;
        ; Archive created at 2026-10-08 18:41:02 UTC
        ;     dbname: eco_core
        ;
        3200; 1259 16386 TABLE public clientes postgres
        3420; 0 16385 TABLE DATA public clientes postgres
        3421; 0 16390 TABLE DATA public pedidos postgres
        3250; 1259 16400 INDEX public ix_clientes postgres
        3260; 2606 16401 CONSTRAINT public clientes clientes_pkey postgres
        3270; 2606 16402 FK CONSTRAINT public pedidos pedidos_cliente_fk postgres
        3271; 0 0 SEQUENCE SET public clientes_id_seq postgres
        """;

    [Fact]
    public void TheListing_CountsDataIndexesAndConstraints()
    {
        var summary = PgArchiveListParser.Parse(Listing);

        summary.TableDataEntries.Should().Be(2);
        summary.Tables.Should().Equal("public.clientes", "public.pedidos");
        summary.IndexEntries.Should().Be(1);
        summary.ConstraintEntries.Should().Be(2);
        summary.SecurityLabelEntries.Should().Be(0);
        summary.HasAnonExtension.Should().BeFalse();
    }

    [Fact]
    public void LabelsAndTheAnonExtension_AreSpotted()
    {
        var summary = PgArchiveListParser.Parse(Listing + "\r\n3280; 3079 16384 EXTENSION - anon \r\n3290; 3596 0 SECURITY LABEL public COLUMN clientes.email postgres\r\n");

        summary.HasAnonExtension.Should().BeTrue();
        summary.SecurityLabelEntries.Should().Be(1);
        PgArchiveListParser.Parse("3281; 2615 16383 SCHEMA - anon postgres").HasAnonExtension.Should().BeTrue();
    }
}

/// <summary>Toda SQL enviada a um servidor é só leitura — conferido no texto, antes de qualquer conexão.</summary>
public partial class PostgresQueriesTests
{
    [Fact]
    public void EveryQuery_OnlyReads()
    {
        foreach (var sql in PostgresQueries.All())
        {
            sql.TrimStart().Should().MatchRegex("^(?i)(SELECT|WITH)\\b");
            Forbidden().IsMatch(sql).Should().BeFalse(sql);
        }
    }

    [Fact]
    public void NoQuery_CallsStaticMasking()
    {
        PostgresQueries.All().Should().NotContain(sql => sql.Contains("anonymize", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DynamicNames_AreQuoted()
    {
        PostgresQueries.CountRows("public.x\"; DROP TABLE y; --").Should().Be("SELECT count(*) FROM \"public\".\"x\"\"; DROP TABLE y; --\"");
        PostgresQueries.QuoteTable("semponto").Should().Be("\"semponto\"");
        PostgresQueries.Fingerprint(new ColumnReference("s", "t", "c"), ["a", "b"], 50)
            .Should().Be("SELECT md5($1 || ROW(\"a\", \"b\")::text), md5($1 || (\"c\")::text) FROM \"s\".\"t\" ORDER BY \"a\", \"b\" LIMIT 50");
    }

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE|ALTER|DROP|CREATE|GRANT|REVOKE|TRUNCATE|COPY|VACUUM|SECURITY\s+LABEL|CALL|DO)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();
}

/// <summary>Os inspetores com um servidor de mentira: o que perguntam e como leem a resposta.</summary>
public class PostgresInspectorsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakePostgresSessions _sessions = new();

    private PostgresServerInspector Inspector() => new(_sessions, NullLogger<PostgresServerInspector>.Instance);

    [Fact]
    public async Task Testing_ReadsVersionUserSchemasTablesAndPrivileges()
    {
        _sessions
            .Answer(PostgresQueries.ServerInfo, ["PostgreSQL 14.10 on x86_64", "backup_user", "eco_core", 123456L])
            .Answer(PostgresQueries.Schemas, ["public"], ["vendas"])
            .Answer(PostgresQueries.Tables, ["public", "clientes", 100L, 10L])
            .Answer(PostgresQueries.Privileges, [false, true, -1, true, 0L]);

        var result = await Inspector().TestAsync(Connection(DatabaseEnvironment.Production), new SecretText("digitada"), Ct);

        result.Connected.Should().BeTrue();
        result.ServerVersion.Should().Be(new PostgresVersion(14, 10));
        result.CurrentUser.Should().Be("backup_user");
        result.DatabaseSizeBytes.Should().Be(123456);
        result.Schemas.Should().Equal("public", "vendas");
        result.TableCount.Should().Be(1);
        result.Privileges.CanReadAllData.Should().BeTrue();
        result.Privileges.CanCreateDatabase.Should().BeTrue();
        _sessions.Opened.Single().Password!.Reveal().Should().Be("digitada");
    }

    [Fact]
    public async Task AFailedConnection_IsAnAnswer_NotAnException()
    {
        _sessions.OpenFailure = new DomainException("password authentication failed");

        var result = await Inspector().TestAsync(Connection(DatabaseEnvironment.Development), cancellationToken: Ct);

        result.Connected.Should().BeFalse();
        result.Error.Should().Contain("authentication");
    }

    [Fact]
    public async Task ColumnsTablesAndStructure_AreRead()
    {
        _sessions
            .Answer(PostgresQueries.Columns, ["public", "clientes", "email", "text", "e-mail do cliente"], ["public", "clientes", "id", "integer", null])
            .Answer(PostgresQueries.Tables, ["public", "clientes", 100L, 10L])
            .Answer(PostgresQueries.Schemas, ["public"])
            .Answer(PostgresQueries.ConstraintsByType, ["p", 2L], ["f", 1L])
            .Answer(PostgresQueries.IndexCount, [3L])
            .Answer(PostgresQueries.SequenceCount, [1L]);
        var connection = Connection(DatabaseEnvironment.Development);

        var columns = await Inspector().ListColumnsAsync(connection, Ct);
        columns.Should().HaveCount(2);
        columns[0].Comment.Should().Be("e-mail do cliente");
        columns[1].Comment.Should().BeNull();

        (await Inspector().ListTablesAsync(connection, Ct)).Single().QualifiedName.Should().Be("public.clientes");

        var structure = await Inspector().GetStructureAsync(connection, Ct);
        structure.Tables.Should().Equal("public.clientes");
        structure.ConstraintsByType["p"].Should().Be(2);
        structure.Indexes.Should().Be(3);
        structure.Sequences.Should().Be(1);
    }

    [Fact]
    public async Task BigTables_AreEstimated_SmallOnesCounted_MissingOnesSkipped()
    {
        _sessions
            .AnswerWhen(sql => sql == PostgresQueries.EstimatedRows, parameters => parameters[0] switch
            {
                "\"public\".\"grande\"" => [[5_000_000L]],
                "\"public\".\"pequena\"" => [[10L]],
                _ => [],
            })
            .AnswerWhen(sql => sql.StartsWith("SELECT count(*) FROM", StringComparison.Ordinal), _ => [[12L]]);

        var counts = await Inspector().CountRowsAsync(Connection(DatabaseEnvironment.Development), ["public.grande", "public.pequena", "public.sumiu"], 1_000_000, Ct);

        counts.Should().Equal(new RowCount("public.grande", 5_000_000, true), new RowCount("public.pequena", 12, false));
    }

    [Fact]
    public async Task TheFingerprint_UsesThePrimaryKey_AndTheSalt()
    {
        _sessions
            .Answer(PostgresQueries.PrimaryKey, ["id"])
            .AnswerWhen(sql => sql.StartsWith("SELECT md5", StringComparison.Ordinal), parameters =>
            [
                ["h1", "v1"],
                ["h2", null],
            ]);

        var fingerprint = await Inspector().FingerprintAsync(Connection(DatabaseEnvironment.Development), new ColumnReference("public", "clientes", "email"), "sal", 100, Ct);

        fingerprint.Values.Should().HaveCount(2);
        fingerprint.Values["h2"].Should().BeNull();
        _sessions.Queries.Should().Contain(query => query.Sql.StartsWith("SELECT md5", StringComparison.Ordinal) && (string)query.Parameters[0]! == "sal");
        _sessions.Queries.Single(query => query.Sql == PostgresQueries.PrimaryKey).Parameters[0].Should().Be("\"public\".\"clientes\"");
    }

    [Fact]
    public async Task WithoutAPrimaryKey_ThereIsNothingToPair()
    {
        var fingerprint = await Inspector().FingerprintAsync(Connection(DatabaseEnvironment.Development), new ColumnReference("public", "log", "texto"), "sal", 100, Ct);

        fingerprint.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task TheAnonymizer_IsReadFromTheCatalog()
    {
        _sessions
            .Answer(PostgresQueries.AnonymizerAvailable, ["2.1.0", "2.1.0"])
            .Answer(PostgresQueries.TransparentMasking, ["on"])
            .Answer(PostgresQueries.RoleIsMasked, [true])
            .Answer(PostgresQueries.AnonymizerFunctions, [true])
            .Answer(PostgresQueries.MaskingRules, ["public", "clientes", "email", "MASKED WITH FUNCTION anon.partial_email(email)"]);

        var status = await new PostgresAnonymizerInspector(_sessions).GetStatusAsync(Connection(DatabaseEnvironment.Production), "anon", Ct);

        status.Installed.Should().BeTrue();
        status.IsSupportedVersion.Should().BeTrue();
        status.TransparentMaskingOn.Should().BeTrue();
        status.CurrentRoleMasked.Should().BeTrue();
        status.CanExecuteFunctions.Should().BeTrue();
        status.Rules.Single().ColumnKey.Should().Be("public.clientes.email");
        _sessions.Queries.Single(query => query.Sql == PostgresQueries.MaskingRules).Parameters.Should().Equal("anon");
    }

    [Fact]
    public async Task WithoutTheExtension_TheAnonymizerSaysSo()
    {
        var inspector = new PostgresAnonymizerInspector(_sessions);

        (await inspector.GetStatusAsync(Connection(DatabaseEnvironment.Production), "anon", Ct)).Should().Be(AnonymizerStatus.Unavailable);

        _sessions.Answer(PostgresQueries.AnonymizerAvailable, ["2.1.0", null]);
        var available = await inspector.GetStatusAsync(Connection(DatabaseEnvironment.Production), "anon", Ct);
        available.Available.Should().BeTrue();
        available.Installed.Should().BeFalse();
        available.AvailableVersion.Should().Be("2.1.0");
    }

    [Fact]
    public void ServerErrors_KeepOnlyTheMessage_Masked()
    {
        var error = NpgsqlPostgresSessionFactory.Presentable(new TimeoutException("Timeout connecting to postgresql://u:s3nh4@db"), "s3nh4");

        error.Message.Should().NotContain("s3nh4").And.Contain("Timeout");
    }

    [Fact]
    public async Task AnUnreachableServer_IsAPresentableError()
    {
        var factory = new NpgsqlPostgresSessionFactory(
            new StaticPasswordReader(),
            new DatabaseOperationsOptions { ConnectTimeoutSeconds = 2 },
            NullLogger<NpgsqlPostgresSessionFactory>.Instance);

        // Porta 1 do próprio computador: recusa na hora, sem rede.
        var nowhere = Connection(DatabaseEnvironment.Development, host: "127.0.0.1") with { Port = 1, SslMode = DatabaseSslMode.Disable };

        await FluentActions.Awaiting(() => factory.OpenAsync(nowhere, null, Ct)).Should().ThrowAsync<DomainException>();
    }
}

/// <summary>As senhas das conexões no cofre: só os segredos de conexão, e nunca o do Jira.</summary>
public class PostgresCredentialStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APassword_RoundTrips_UnderTheConnectionsName()
    {
        var secrets = new InMemorySecretStore();
        var log = new CapturingLog();
        var store = new PostgresCredentialStore(secrets, log.For<PostgresCredentialStore>());
        var id = Guid.CreateVersion7();

        var reference = await store.StoreAsync(id, new SecretText(Password), Ct);

        reference.Should().Be($"postgres-{id:N}");
        (await store.HasAsync(reference, Ct)).Should().BeTrue();
        (await store.ReadAsync(reference, Ct)).Should().Be(Password);
        log.Mentions(Password).Should().BeFalse();

        await store.DeleteAsync(reference, Ct);
        (await store.HasAsync(reference, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task OtherSecrets_AreOutOfReach()
    {
        var secrets = new InMemorySecretStore();
        secrets.Secrets["jira"] = "token-do-jira";
        var store = new PostgresCredentialStore(secrets, NullLogger<PostgresCredentialStore>.Instance);

        (await store.ReadAsync("jira", Ct)).Should().BeNull();
        (await store.HasAsync("jira", Ct)).Should().BeFalse();
        (await store.HasAsync(null, Ct)).Should().BeFalse();
        await store.DeleteAsync("jira", Ct);
        secrets.Secrets.Should().ContainKey("jira");
    }
}

/// <summary>Os diretórios isolados: estrutura, limpeza e a varredura do que uma queda deixou.</summary>
public sealed class DatabaseOperationWorkspaceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mytaskapp-ws-{Guid.NewGuid():N}");

    private DatabaseOperationWorkspaceFactory Factory() => new(_root, NullLogger<DatabaseOperationWorkspaceFactory>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static DatabaseOperationMetadata Metadata(Guid id, bool kept) => new(
        id, DatabaseOperationType.CopyAndAnonymize, DatabaseOperationStatus.Succeeded, Now, Now, "ECO Produção", "ECO Desenvolvimento",
        "ECO LGPD", "pg_dump 17.2", 10, 2, ["Gerando dump anônimo: Succeeded"], kept);

    [Fact]
    public async Task AWorkspace_HasItsFolders_AndAUniqueName()
    {
        var first = await Factory().CreateAsync(Guid.CreateVersion7(), Now, Ct);
        var second = await Factory().CreateAsync(Guid.CreateVersion7(), Now, Ct);

        Path.GetFileName(first.Root).Should().Be("operation-20261008-184102");
        Path.GetFileName(second.Root).Should().Be("operation-20261008-184102-2");
        Directory.Exists(first.DumpDirectory).Should().BeTrue();
        Directory.Exists(first.AnonymizedDirectory).Should().BeTrue();
        Directory.Exists(first.LogsDirectory).Should().BeTrue();
        first.AvailableBytes().Should().BePositive();
    }

    [Fact]
    public async Task OnUnix_TheFoldersAreOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Modo de arquivo é do Unix.");

        var workspace = await Factory().CreateAsync(Guid.CreateVersion7(), Now, Ct);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.GetUnixFileMode(workspace.Root).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Cleanup_RemovesWhatIsSensitive_AndKeepsTheMetadata()
    {
        var workspace = await Factory().CreateAsync(Guid.CreateVersion7(), Now, Ct);
        await File.WriteAllTextAsync(Path.Combine(workspace.DumpDirectory, "toc.dat"), "bruto", Ct);
        await File.WriteAllTextAsync(Path.Combine(workspace.AnonymizedDirectory, "toc.dat"), "anônimo", Ct);
        await File.WriteAllTextAsync(Path.Combine(workspace.LogsDirectory, "log.txt"), "log", Ct);

        workspace.SizeOf(workspace.AnonymizedDirectory).Should().BeGreaterThan(0);
        workspace.SizeOf(Path.Combine(workspace.AnonymizedDirectory, "toc.dat")).Should().BeGreaterThan(0);
        workspace.SizeOf(Path.Combine(_root, "nada")).Should().Be(0);

        (await workspace.CleanupAsync(keepAnonymized: false, Ct)).Should().BeNull();
        await workspace.WriteMetadataAsync(Metadata(Guid.CreateVersion7(), kept: false), Ct);

        Directory.Exists(workspace.DumpDirectory).Should().BeFalse();
        Directory.Exists(workspace.AnonymizedDirectory).Should().BeFalse();
        Directory.Exists(workspace.LogsDirectory).Should().BeFalse();
        File.ReadAllText(Path.Combine(workspace.Root, "metadata.json")).Should().Contain("ECO LGPD");
    }

    [Fact]
    public async Task Cleanup_CanKeepTheAnonymousDump_ButNeverTheRawOne()
    {
        var workspace = await Factory().CreateAsync(Guid.CreateVersion7(), Now, Ct);
        await File.WriteAllTextAsync(Path.Combine(workspace.DumpDirectory, "toc.dat"), "bruto", Ct);

        (await workspace.CleanupAsync(keepAnonymized: true, Ct)).Should().Be(workspace.AnonymizedDirectory);

        Directory.Exists(workspace.DumpDirectory).Should().BeFalse();
        Directory.Exists(workspace.AnonymizedDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task TheSweep_CleansWhatACrashLeft_AndSparesTheLiveAndTheKept()
    {
        var crashed = await Factory().CreateAsync(Guid.CreateVersion7(), Now, Ct);
        await File.WriteAllTextAsync(Path.Combine(crashed.AnonymizedDirectory, "toc.dat"), "x", Ct);

        var liveId = Guid.CreateVersion7();
        var live = await Factory().CreateAsync(liveId, Now, Ct);
        await File.WriteAllTextAsync(Path.Combine(live.DumpDirectory, "toc.dat"), "x", Ct);

        var keptId = Guid.CreateVersion7();
        var kept = await Factory().CreateAsync(keptId, Now, Ct);
        await File.WriteAllTextAsync(Path.Combine(kept.AnonymizedDirectory, "toc.dat"), "x", Ct);
        await kept.WriteMetadataAsync(Metadata(keptId, kept: true), Ct);

        (await Factory().SweepAsync([liveId], Ct)).Should().Be(1);

        Directory.Exists(crashed.AnonymizedDirectory).Should().BeFalse();
        File.Exists(Path.Combine(live.DumpDirectory, "toc.dat")).Should().BeTrue();
        File.Exists(Path.Combine(kept.AnonymizedDirectory, "toc.dat")).Should().BeTrue();
        (await Factory().SweepAsync([], Ct)).Should().Be(1, "agora a que estava viva também é varrida");
    }

    [Fact]
    public async Task SweepingAnEmptyOrBrokenRoot_IsHarmless()
    {
        (await Factory().SweepAsync([], Ct)).Should().Be(0);

        var workspace = await Factory().CreateAsync(Guid.CreateVersion7(), Now, Ct);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "metadata.json"), "{ quebrado", Ct);
        await File.WriteAllTextAsync(Path.Combine(workspace.AnonymizedDirectory, "toc.dat"), "x", Ct);

        (await Factory().SweepAsync([], Ct)).Should().Be(1);
    }

    [Fact]
    public void TheDefaultRoot_IsLocal_AndFollowsTheOverride()
    {
        var overridden = UserDataLocation.For(_root);
        overridden.DatabaseOperations.Should().Be(Path.Combine(_root, "database-operations"));

        var system = UserDataLocation.Resolve(null);
        system.DatabaseOperations.Should().StartWith(system.LocalRoot);
        system.DatabaseOperations.Should().EndWith(Path.Combine("MyTaskApp", "database-operations"));
    }
}
