using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.PostgreSql;
using MyTaskApp.Infrastructure.Tests.Jira;
using MyTaskApp.Infrastructure.Tests.Processes;
using static MyTaskApp.Infrastructure.Tests.PostgreSql.PostgresTestSupport;

namespace MyTaskApp.Infrastructure.Tests.PostgreSql;

/// <summary>
/// As ferramentas do PostgreSQL sem executar nenhuma (ADR-056): argumentos
/// exatos, senha só no ambiente, saída mascarada, e a guarda recusando antes
/// do processo nascer.
/// </summary>
public class PgToolRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ScriptedProcessRunner _processes = new();

    private readonly CapturingLog _log = new();

    private readonly DatabaseOperationsOptions _options = new();

    private PgToolRunner Runner(string? password = Password) =>
        new(_processes, new StaticToolLocator(Tools()), new StaticPasswordReader(password), _options, _log.For<PgToolRunner>())
        {
            InheritedVariables = () => ["PATH", "PGSERVICE", "PGPASSWORD", "pghost"],
        };

    private static PgDumpRequest Dump(DatabaseConnectionSnapshot connection) =>
        new(connection, @"C:\ws\anonymized\archive", true, true, 4, []);

    [Fact]
    public async Task TheSourceVersion_PicksTheSetThatRuns()
    {
        var newest = Tools(@"C:\pgadmin\runtime");
        var fourteen = Tools(@"C:\pg\14\bin").Tools.Select(tool => tool with { Version = new PostgresVersion(14, 22) }).ToList();
        var installed = newest with
        {
            Tools = newest.Tools.Select(tool => tool with { Version = new PostgresVersion(18, 4) }).ToList(),
            Sets = [newest.Tools.Select(tool => tool with { Version = new PostgresVersion(18, 4) }).ToList(), fourteen],
        };
        var runner = new PgToolRunner(_processes, new StaticToolLocator(installed), new StaticPasswordReader(), _options, _log.For<PgToolRunner>());
        var target = Connection(DatabaseEnvironment.Development);

        await runner.RunAsync(new PgInvocation(PostgresTool.PgRestore, ["--list", "x"], TimeSpan.FromMinutes(1), target, [], new PostgresVersion(14, 10)), null, Ct);
        await runner.RunAsync(new PgInvocation(PostgresTool.PgRestore, ["--list", "x"], TimeSpan.FromMinutes(1), target, []), null, Ct);

        _processes.Requests.Select(request => request.FileName).Should().Equal(
            @"C:\pg\14\bin\pg_restore.exe",
            @"C:\pgadmin\runtime\pg_restore.exe");
    }

    // --- Argumentos -------------------------------------------------------------

    [Fact]
    public void TheStructureDump_UsesDirectoryFormat_AndTheSnapshot()
    {
        var request = Dump(Connection(DatabaseEnvironment.Production)) with { IncludeData = false, Jobs = 1, Snapshot = "00000003-0000001B-1" };

        PgArguments.Dump(request, 60).Should().Equal(
            "--host", "192.168.15.112", "--port", "5432", "--username", "backup_user",
            "--dbname", "eco_core",
            "--format=directory", "--file", @"C:\ws\anonymized\archive",
            "--jobs", "1", "--verbose", "--no-password", "--lock-wait-timeout=60s",
            "--no-subscriptions", "--no-publications",
            "--schema-only", "--snapshot=00000003-0000001B-1");
    }

    [Theory]
    [InlineData("00000003-0000001B")]
    [InlineData("1-2-3")]
    public void AWellFormedSnapshot_IsAccepted(string snapshot)
    {
        PgArguments.Dump(Dump(Connection(DatabaseEnvironment.Development)) with { Snapshot = snapshot }, 60)
            .Should().Contain($"--snapshot={snapshot}");
    }

    [Theory]
    [InlineData("x; --dbname=prod")]
    [InlineData("--help")]
    [InlineData("")]
    public void AStrangeSnapshot_NeverBecomesAnArgument(string snapshot)
    {
        FluentActions.Invoking(() => PgArguments.Dump(Dump(Connection(DatabaseEnvironment.Development)) with { Snapshot = snapshot }, 60))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void SchemaOrDataOnly_AreMappedToTheirFlags()
    {
        var connection = Connection(DatabaseEnvironment.Development);

        PgArguments.Dump(Dump(connection) with { IncludeData = false }, 60).Should().Contain("--schema-only");
        PgArguments.Dump(Dump(connection) with { IncludeSchema = false }, 60).Should().Contain("--data-only");
        PgArguments.Dump(Dump(connection), 60).Should().NotContain(["--schema-only", "--data-only", "--exclude-extension=anon", "--no-security-labels"]);
    }

    [Fact]
    public void TheRestore_CanRunOneSectionAtATime()
    {
        var request = new PgRestoreRequest(Connection(DatabaseEnvironment.Development, "eco_dev"), @"C:\ws\a", true, false, 1, []);

        PgArguments.Restore(request with { Section = RestoreSection.PreData }).Should().Contain("--section=pre-data").And.NotContain("--schema-only");
        PgArguments.Restore(request with { Section = RestoreSection.PostData }).Should().Contain("--section=post-data");
        PgArguments.Restore(request).Should().Contain("--schema-only").And.NotContain(argument => argument.StartsWith("--section", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRestore_DropsOwnersPrivilegesAndLabels_AndStopsAtTheFirstError()
    {
        var arguments = PgArguments.Restore(new PgRestoreRequest(Connection(DatabaseEnvironment.Development, "eco_dev"), @"C:\ws\a", true, false, 2, []));

        arguments.Should().Equal(
            "--host", "192.168.15.112", "--port", "5432", "--username", "backup_user",
            "--dbname", "eco_dev", "--no-owner", "--no-privileges", "--no-security-labels", "--exit-on-error",
            "--jobs", "2", "--verbose", "--no-password", "--schema-only", @"C:\ws\a");
    }

    [Fact]
    public void CreateAndDrop_GoThroughTheMaintenanceDatabase()
    {
        var target = Connection(DatabaseEnvironment.Development, "eco_dev");

        PgArguments.CreateDatabase(target).Should().EndWith(["--maintenance-db=postgres", "--template=template0", "--encoding=UTF8", "--no-password", "eco_dev"]);
        PgArguments.DropDatabase(target, force: true).Should().EndWith(["--if-exists", "--no-password", "--force", "eco_dev"]);
        PgArguments.DropDatabase(target, force: false).Should().NotContain("--force");
        PgArguments.ListArchive(@"C:\a").Should().Equal("--list", @"C:\a");
        PgArguments.Version().Should().Equal("--version");
    }

    // --- Ambiente e senha -----------------------------------------------------

    [Fact]
    public async Task ThePassword_GoesOnlyThroughTheEnvironment()
    {
        await Runner().RunAsync(new PgInvocation(PostgresTool.PgDump, PgArguments.Dump(Dump(Connection(DatabaseEnvironment.Development)), 60),
            TimeSpan.FromMinutes(1), Connection(DatabaseEnvironment.Development)), null, Ct);

        var request = _processes.Requests.Single();
        request.FileName.Should().Be(@"C:\pg\17\bin\pg_dump.exe");
        request.Arguments.Should().NotContain(argument => argument.Contains(Password, StringComparison.Ordinal));
        request.Environment!["PGPASSWORD"].Should().Be(Password);
        request.Environment["PGSSLMODE"].Should().Be("require");
        request.Environment["PGOPTIONS"].Should().Contain("default_transaction_read_only=on");
        request.Environment["LC_MESSAGES"].Should().Be("C");
        request.Environment["PGPASSFILE"].Should().Contain("no-pgpass");
        request.ToString().Should().NotContain(Password);
    }

    [Fact]
    public void InheritedPostgresVariables_AreRemoved()
    {
        var environment = PgEnvironment.Build(null, null, "x", 10, ["PATH", "PGSERVICE", "pghost", "PGPASSWORD"], readOnly: false);

        environment.Should().ContainKey("PGSERVICE").WhoseValue.Should().BeNull();
        environment.Should().ContainKey("pghost").WhoseValue.Should().BeNull();
        environment.Should().ContainKey("PGPASSWORD").WhoseValue.Should().BeNull();
        environment.Should().NotContainKey("PATH");
        environment.Should().NotContainKey("PGOPTIONS", "só o dump nasce só leitura; o restore precisa escrever no destino");
        environment["PGCONNECT_TIMEOUT"].Should().Be("10");
    }

    [Theory]
    [InlineData(DatabaseSslMode.Prefer, "prefer")]
    [InlineData(DatabaseSslMode.Require, "require")]
    [InlineData(DatabaseSslMode.VerifyFull, "verify-full")]
    [InlineData(DatabaseSslMode.Disable, "disable")]
    public void SslModes_AreTranslated(DatabaseSslMode mode, string expected)
    {
        PgEnvironment.SslMode(mode).Should().Be(expected);
    }

    [Fact]
    public async Task OutputLines_AreMasked_ParsedAndKept_AndTheLogHasNoSecret()
    {
        _processes.WhenTool("pg_dump", new ProcessResult(1, string.Empty, string.Empty, false),
            new CommandOutputLine("pg_dump: dumping contents of table \"public.clientes\"", true),
            new CommandOutputLine($"pg_dump: error: password {Password} rejected; PGPASSWORD={Password}", true));
        var events = new List<PgToolEvent>();
        var connection = Connection(DatabaseEnvironment.Development);

        var output = await Runner().RunAsync(
            new PgInvocation(PostgresTool.PgDump, ["--dbname", "x"], TimeSpan.FromMinutes(1), connection),
            new SyncProgress<PgToolEvent>(events.Add),
            Ct);

        events.Should().HaveCount(2);
        events[0].Line.IsError.Should().BeFalse("o --verbose sai pelo stderr, mas não é erro");
        events[1].Line.IsError.Should().BeTrue();
        events[0].Kind.Should().Be(PgToolEventKind.TableData);
        events[0].Table.Should().Be("public.clientes");
        events.Should().OnlyContain(item => !item.Line.Text.Contains(Password, StringComparison.Ordinal));
        output.Run.Succeeded.Should().BeFalse();
        output.Run.ErrorTail.Should().Contain("rejected").And.NotContain(Password);
        _log.Mentions(Password).Should().BeFalse();
        _log.Mentions("PgToolFinished").Should().BeTrue();
    }

    [Fact]
    public async Task OnlyTheEndOfTheErrorIsKept()
    {
        var lines = Enumerable.Range(1, 40).Select(index => new CommandOutputLine($"linha {index}", true)).ToArray();
        _processes.WhenTool("pg_restore", ScriptedProcessRunner.Fail(1), lines);

        var output = await Runner().RunAsync(new PgInvocation(PostgresTool.PgRestore, ["--list", "x"], TimeSpan.FromMinutes(1)), null, Ct);

        output.Run.ErrorTail.Should().Contain("linha 40").And.NotContain("linha 25" + Environment.NewLine);
        output.Run.ErrorTail.Split(Environment.NewLine).Should().HaveCount(PgToolRunner.ErrorTailLines);
    }

    [Fact]
    public async Task ATimeout_IsReported()
    {
        _processes.WhenTool("pg_restore", ScriptedProcessRunner.TimedOut());

        var output = await Runner().RunAsync(new PgInvocation(PostgresTool.PgRestore, ["--list", "x"], TimeSpan.FromSeconds(1)), null, Ct);

        output.Run.TimedOut.Should().BeTrue();
        output.Run.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Canceling_PropagatesToTheCaller()
    {
        _processes.Cancels = true;

        await FluentActions.Awaiting(() => Runner().RunAsync(new PgInvocation(PostgresTool.PgRestore, ["--list", "x"], TimeSpan.FromMinutes(1)), null, Ct))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AToolThatDoesNotStart_IsAPresentableError()
    {
        _processes.StartFailure = new ProcessStartException("pg_dump", new InvalidOperationException());

        await FluentActions.Awaiting(() => Runner().RunAsync(new PgInvocation(PostgresTool.PgRestore, ["--list", "x"], TimeSpan.FromMinutes(1)), null, Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*pg_restore*");
    }

    [Fact]
    public async Task AMissingTool_IsAPresentableError()
    {
        var tools = Tools() with { Tools = Tools().Tools.Where(tool => tool.Tool != PostgresTool.PgRestore).ToList() };
        var runner = new PgToolRunner(_processes, new StaticToolLocator(tools), new StaticPasswordReader(), _options, _log.For<PgToolRunner>());

        await FluentActions.Awaiting(() => runner.RunAsync(new PgInvocation(PostgresTool.PgRestore, ["--list", "x"], TimeSpan.FromMinutes(1)), null, Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*não está instalado*");
        _processes.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAStoredPassword_NoPasswordVariableIsSet()
    {
        await Runner(password: null).RunAsync(
            new PgInvocation(PostgresTool.PgRestore, ["--dbname", "x"], TimeSpan.FromMinutes(1), Connection(DatabaseEnvironment.Development)), null, Ct);

        _processes.Requests.Single().Environment!.Should().ContainKey("PGPASSWORD").WhoseValue.Should().BeNull();
    }

    // --- A guarda -------------------------------------------------------------

    public static TheoryData<PostgresTool> WritingTools() => new() { PostgresTool.PgRestore, PostgresTool.CreateDb, PostgresTool.DropDb };

    [Theory]
    [MemberData(nameof(WritingTools))]
    public async Task NothingThatWrites_EverStartsAgainstProduction(PostgresTool tool)
    {
        foreach (var environment in (DatabaseEnvironment[])[DatabaseEnvironment.Production, DatabaseEnvironment.CriticalProduction])
        {
            var target = Connection(environment, "eco_dev");

            await FluentActions.Awaiting(() => Runner().RunAsync(new PgInvocation(tool, ["--dbname", "eco_dev"], TimeSpan.FromMinutes(1), target), null, Ct))
                .Should().ThrowAsync<DatabaseSecurityException>();
        }

        _processes.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(WritingTools))]
    public async Task ADevelopmentLabelOnAProductionDatabase_IsStoppedToo(PostgresTool tool)
    {
        var production = Connection(DatabaseEnvironment.Production);
        var disguised = Connection(DatabaseEnvironment.Development);

        await FluentActions.Awaiting(() => Runner().RunAsync(
                new PgInvocation(tool, ["x"], TimeSpan.FromMinutes(1), disguised, [production.EndpointKey]), null, Ct))
            .Should().ThrowAsync<DatabaseSecurityException>();

        _processes.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(WritingTools))]
    public async Task NothingThatWrites_StartsOnAServerWithoutADatabase(PostgresTool tool)
    {
        var server = Connection(DatabaseEnvironment.Development, database: null);

        var refusal = await FluentActions.Awaiting(() => Runner().RunAsync(new PgInvocation(tool, ["x"], TimeSpan.FromMinutes(1), server), null, Ct))
            .Should().ThrowAsync<DatabaseSecurityException>();

        refusal.Which.Decision.Has(SecurityViolationCode.DatabaseNotChosen).Should().BeTrue();
        _processes.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(WritingTools))]
    public async Task AProductionServer_ProtectsEveryDatabaseOnIt(PostgresTool tool)
    {
        var productionServer = Connection(DatabaseEnvironment.Production, database: null);
        var sameServer = Connection(DatabaseEnvironment.Development, "copia_local");

        await FluentActions.Awaiting(() => Runner().RunAsync(
                new PgInvocation(tool, ["x"], TimeSpan.FromMinutes(1), sameServer, [productionServer.EndpointKey]), null, Ct))
            .Should().ThrowAsync<DatabaseSecurityException>();

        _processes.Requests.Should().BeEmpty();
    }

    [Fact]
    public void ADumpOfAServerWithoutADatabase_IsStopped()
    {
        var server = Connection(DatabaseEnvironment.Development, database: null);

        FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(PostgresTool.PgDump, ["x"], TimeSpan.Zero, server)))
            .Should().Throw<DatabaseSecurityException>().Which.Decision.Has(SecurityViolationCode.DatabaseNotChosen).Should().BeTrue();
    }

    [Fact]
    public void Arguments_NeverGoOutWithoutADatabase()
    {
        // Sem --dbname, o libpq cairia no banco com o nome do usuário — longe da política.
        var server = Connection(DatabaseEnvironment.Development, database: null);

        FluentActions.Invoking(() => PgArguments.Dump(Dump(server), 30)).Should().Throw<DatabaseSecurityException>();
        FluentActions.Invoking(() => PgArguments.Restore(new PgRestoreRequest(server, @"C:\ws\a", true, true, 2, [])))
            .Should().Throw<DatabaseSecurityException>();
        FluentActions.Invoking(() => PgArguments.CreateDatabase(server)).Should().Throw<DatabaseSecurityException>();
        FluentActions.Invoking(() => PgArguments.DropDatabase(server, force: false)).Should().Throw<DatabaseSecurityException>();

        PgArguments.CreateDatabase(server.WithDatabase("lock_eco_core_1010_20261009_143000"))
            .Should().EndWith("lock_eco_core_1010_20261009_143000");
    }

    [Fact]
    public async Task ASession_NeverOpensWithoutADatabase()
    {
        var sessions = new NpgsqlPostgresSessionFactory(
            new StaticPasswordReader(), _options, Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlPostgresSessionFactory>.Instance);

        await FluentActions.Awaiting(() => sessions.OpenAsync(Connection(DatabaseEnvironment.Development, database: null), null, Ct))
            .Should().ThrowAsync<DatabaseSecurityException>();
    }

    [Fact]
    public void ADumpWithDataOfASourceThatDemandsAnonymization_IsStopped_ButItsStructureIsNot()
    {
        var source = Connection(DatabaseEnvironment.Production) with
        {
            Permissions = ConnectionPermissions.FromFlags(ConnectionPermission.Read | ConnectionPermission.Dump | ConnectionPermission.RequireAnonymization),
        };

        foreach (var arguments in (string[][])[["x"], ["--schema-only", "--data-only"], ["--schema-only", "--section=data"], ["--schema-only", "--section", "data"], ["--schema-only", "-a"]])
        {
            FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(PostgresTool.PgDump, arguments, TimeSpan.Zero, source)))
                .Should().Throw<DatabaseSecurityException>(string.Join(" ", arguments))
                .Which.Decision.Has(SecurityViolationCode.PlainDumpFromProtectedSource).Should().BeTrue();
        }

        FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(PostgresTool.PgDump, ["--dbname", "eco", "--schema-only"], TimeSpan.Zero, source)))
            .Should().NotThrow();
    }

    [Fact]
    public void TheMaskedCopyDestination_IsJudgedLikeARestore()
    {
        var production = Connection(DatabaseEnvironment.Production, database: null);

        FluentActions.Invoking(() => PostgresProcessGuard.CheckDestination(Connection(DatabaseEnvironment.Production, "x"), []))
            .Should().Throw<DatabaseSecurityException>();
        FluentActions.Invoking(() => PostgresProcessGuard.CheckDestination(Connection(DatabaseEnvironment.Development, "copia"), [production.EndpointKey]))
            .Should().Throw<DatabaseSecurityException>();
        FluentActions.Invoking(() => PostgresProcessGuard.CheckDestination(Connection(DatabaseEnvironment.Development, database: null), []))
            .Should().Throw<DatabaseSecurityException>().Which.Decision.Has(SecurityViolationCode.DatabaseNotChosen).Should().BeTrue();
        FluentActions.Invoking(() => PostgresProcessGuard.CheckDestination(Connection(DatabaseEnvironment.Development, "copia"), []))
            .Should().NotThrow();
    }

    [Fact]
    public void ADumpWithoutPermissionOrConnection_IsStopped()
    {
        var noDump = Connection(DatabaseEnvironment.Development) with { Permissions = default };

        FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(PostgresTool.PgDump, ["x"], TimeSpan.Zero, noDump)))
            .Should().Throw<DatabaseSecurityException>();
        FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(PostgresTool.PgDump, ["x"], TimeSpan.Zero)))
            .Should().Throw<DatabaseSecurityException>();
    }

    [Fact]
    public void Psql_OnlyAnswersItsVersion()
    {
        FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(PostgresTool.Psql, ["--version"], TimeSpan.Zero))).Should().NotThrow();
        FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(PostgresTool.Psql, ["-c", "DROP TABLE x"], TimeSpan.Zero, Connection(DatabaseEnvironment.Development))))
            .Should().Throw<DatabaseSecurityException>().Which.Decision.Has(SecurityViolationCode.SqlExecutionForbidden).Should().BeTrue();
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("template1")]
    public void SystemDatabases_AreNeverCreatedOrDropped(string database)
    {
        var target = Connection(DatabaseEnvironment.Development, database);

        FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(PostgresTool.DropDb, ["x"], TimeSpan.Zero, target)))
            .Should().Throw<DatabaseSecurityException>();
    }

    [Fact]
    public void WritesNeedTheDestinationPermissions()
    {
        var target = Connection(DatabaseEnvironment.Development, "eco_dev") with
        {
            Permissions = ConnectionPermissions.FromFlags(ConnectionPermission.Restore),
        };

        FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(PostgresTool.PgRestore, ["x"], TimeSpan.Zero, target)))
            .Should().Throw<DatabaseSecurityException>();
        FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(PostgresTool.PgRestore, ["x"], TimeSpan.Zero)))
            .Should().Throw<DatabaseSecurityException>();
        FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(PostgresTool.CreateDb, ["x"], TimeSpan.Zero)))
            .Should().Throw<DatabaseSecurityException>();
    }

    [Fact]
    public void VersionsAndListings_NeedNoTarget()
    {
        foreach (var tool in Enum.GetValues<PostgresTool>())
        {
            FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(tool, ["--version"], TimeSpan.Zero))).Should().NotThrow();
        }

        FluentActions.Invoking(() => PostgresProcessGuard.Check(new PgInvocation(PostgresTool.PgRestore, ["--list", "x"], TimeSpan.Zero))).Should().NotThrow();
    }

    // --- Serviços ---------------------------------------------------------------

    [Fact]
    public async Task TheDumpService_PassesTheRequestToTheRunner()
    {
        var connection = Connection(DatabaseEnvironment.Development);
        var output = Path.Combine(Path.GetTempPath(), $"mytaskapp-dump-{Guid.NewGuid():N}", "archive");

        try
        {
            var run = await new PostgresDumpService(Runner(), _options).DumpAsync(Dump(connection) with { OutputDirectory = output }, null, Ct);

            run.Succeeded.Should().BeTrue();
            Directory.Exists(Path.GetDirectoryName(output)).Should().BeTrue();
            _processes.Requests.Single().Timeout.Should().Be(_options.DumpTimeout);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(output)!, recursive: true);
        }
    }

    [Fact]
    public async Task TheListing_IsParsed_OrFailsPresentably()
    {
        _processes.WhenTool("pg_restore", ScriptedProcessRunner.Ok("3420; 0 16385 TABLE DATA public clientes postgres\n"));
        var service = new PostgresDumpService(Runner(), _options);

        (await service.ListArchiveAsync(@"C:\a", Ct)).TableDataEntries.Should().Be(1);

        _processes.WhenTool("pg_restore", ScriptedProcessRunner.Fail(1, "pg_restore: error: could not open input file"));
        var failing = new PgToolRunner(new ScriptedProcessRunner().WhenTool("pg_restore", ScriptedProcessRunner.Fail(1), new CommandOutputLine("arquivo ilegível", true)),
            new StaticToolLocator(Tools()), new StaticPasswordReader(), _options, _log.For<PgToolRunner>());

        await FluentActions.Awaiting(() => new PostgresDumpService(failing, _options).ListArchiveAsync(@"C:\a", Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*arquivo ilegível*");
    }

    [Fact]
    public async Task TheRestoreService_RunsEachToolWithItsTimeout()
    {
        var target = Connection(DatabaseEnvironment.Development, "eco_dev");
        var service = new PostgresRestoreService(Runner(), _options);

        (await service.DropDatabaseAsync(target, [], force: true, Ct)).Succeeded.Should().BeTrue();
        (await service.CreateDatabaseAsync(target, [], Ct)).Succeeded.Should().BeTrue();
        (await service.RestoreAsync(new PgRestoreRequest(target, @"C:\a", true, true, 2, []), null, Ct)).Succeeded.Should().BeTrue();

        _processes.Requests.Select(ScriptedProcessRunner.ToolOf).Should().Equal("dropdb", "createdb", "pg_restore");
        _processes.Requests[0].Timeout.Should().Be(_options.CreateDropTimeout);
        _processes.Requests[2].Timeout.Should().Be(_options.RestoreTimeout);
        _processes.Requests[2].Environment!.Should().NotContainKey("PGOPTIONS");
    }

    [Fact]
    public async Task TheRestoreService_RefusesProduction_BeforeAnyProcess()
    {
        var service = new PostgresRestoreService(Runner(), _options);
        var production = Connection(DatabaseEnvironment.Production);

        await FluentActions.Awaiting(() => service.RestoreAsync(new PgRestoreRequest(production, @"C:\a", true, true, 1, []), null, Ct))
            .Should().ThrowAsync<DatabaseSecurityException>();
        await FluentActions.Awaiting(() => service.DropDatabaseAsync(production, [], true, Ct)).Should().ThrowAsync<DatabaseSecurityException>();
        await FluentActions.Awaiting(() => service.CreateDatabaseAsync(production, [], Ct)).Should().ThrowAsync<DatabaseSecurityException>();

        _processes.Requests.Should().BeEmpty();
    }

    [Fact]
    public void TheOptions_HaveSaneFloors()
    {
        var options = new DatabaseOperationsOptions
        {
            VersionTimeoutSeconds = 0,
            CreateDropTimeoutSeconds = 0,
            DumpTimeoutMinutes = 0,
            RestoreTimeoutMinutes = 0,
            ListTimeoutSeconds = 0,
        };

        options.VersionTimeout.Should().Be(TimeSpan.FromSeconds(1));
        options.CreateDropTimeout.Should().Be(TimeSpan.FromSeconds(5));
        options.DumpTimeout.Should().Be(TimeSpan.FromMinutes(1));
        options.RestoreTimeout.Should().Be(TimeSpan.FromMinutes(1));
        options.ListTimeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
