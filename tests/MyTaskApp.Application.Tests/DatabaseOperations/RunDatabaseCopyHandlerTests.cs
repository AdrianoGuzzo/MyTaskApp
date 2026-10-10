using MyTaskApp.Application.Commands;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Tests.DatabaseOperations;

/// <summary>
/// O fluxo Copiar + Anonimizar (ADR-056) sem servidor nem processo: a ordem
/// das etapas, as barreiras antes de cada passo perigoso, a limpeza que
/// sempre acontece e a auditoria de cada desfecho.
/// </summary>
public class RunDatabaseCopyHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DatabaseCopyScenario _scenario = new();

    private Task<DatabaseCopyResult> RunAsync(
        DatabaseCopyRequest? request = null,
        bool confirmed = true,
        string? typed = null,
        IProgress<DatabaseCopyProgress>? progress = null,
        CancellationToken? cancellationToken = null) =>
        _scenario.RunHandler().HandleAsync(
            new RunDatabaseCopy(request ?? _scenario.Request(), confirmed, typed),
            progress,
            cancellationToken ?? Ct);

    [Fact]
    public async Task ProductionToDevelopment_RunsEveryStep_InOrder_AndSucceeds()
    {
        var progress = new ProgressLog<DatabaseCopyProgress>();

        var result = await RunAsync(progress: progress);

        result.Succeeded.Should().BeTrue(result.Error);
        result.Steps.Values.Should().OnlyContain(state => state == CommandStepState.Succeeded);
        _scenario.Tools.Calls.Should().Equal(
            "catalog:ECO Produção:eco_core",
            "open:ECO Produção:eco_core",
            "schema-dump",
            "list",
            "dropdb:eco_dev",
            "createdb:eco_dev",
            "restore:pre-data",
            "connect:eco_dev",
            "copy:public.clientes",
            "copy:public.pedidos",
            "restore:post-data",
            "sequences",
            "close");

        progress.Items.Where(item => item.State == CommandStepState.Running).Select(item => item.Step).Distinct()
            .Should().BeInAscendingOrder();
        progress.Items.Select(item => item.Percent).Should().BeInAscendingOrder();
        progress.Items.Last().Percent.Should().Be(100);
        progress.Items.Should().Contain(item => item.Line != null && item.Line.Text.Contains("dumping contents"));
        result.Verification!.Result.Should().Be("SUCCESS");
    }

    [Fact]
    public async Task OnlyTheStructure_IsDumped_FromTheSameSnapshotAsTheData()
    {
        await RunAsync();

        var dump = _scenario.Tools.Dumps.Should().ContainSingle().Subject;
        dump.SchemaOnly.Should().BeTrue("nenhuma linha de produção vai para o disco");
        dump.Snapshot.Should().Be(FakeMaskedCopier.Snapshot);
        dump.Connection.Id.Should().Be(_scenario.Production.Id);
        dump.OutputDirectory.Should().Contain("anonymized");
        dump.ProtectedEndpoints.Should().Contain(_scenario.Production.Snapshot().EndpointKey);

        _scenario.Tools.Restores.Select(restore => restore.Section).Should().Equal(RestoreSection.PreData, RestoreSection.PostData);
        _scenario.Tools.Restores.Should().OnlyContain(restore => restore.Target.Id == _scenario.Development.Id && restore.ArchiveDirectory == dump.OutputDirectory);
    }

    [Fact]
    public async Task ASkippedTable_IsCreatedButNotCopied_AndMustArriveEmpty()
    {
        _scenario.Profile.ReplaceSkippedTables([new SkippedTableSpec("public", "pedidos")], DatabaseCopyScenario.Now);
        _scenario.Inspector.RowsOf = (connection, table) => connection == "ECO Desenvolvimento" && table == "public.pedidos" ? 0 : null;

        var result = await RunAsync();

        result.Error.Should().BeNull();
        _scenario.Copier.Copied.Select(table => table.Table).Should().Equal("clientes");
        _scenario.Tools.Restores.Select(restore => restore.Section).Should().Equal(RestoreSection.PreData, RestoreSection.PostData);
        result.Steps[DatabaseCopyStep.Verify].Should().Be(CommandStepState.Succeeded);
        result.Verification!.Checks.Should().Contain(check => check.Name == "Tabelas sem dados" && check.Outcome == CheckOutcome.Pass);
        result.Verification.Checks.Single(check => check.Name == "Linhas por tabela").Detail.Should().Contain("1 tabela(s)");
    }

    [Fact]
    public async Task ASkippedTable_WithRowsInTheDestination_FailsTheVerification()
    {
        _scenario.Profile.ReplaceSkippedTables([new SkippedTableSpec("public", "pedidos")], DatabaseCopyScenario.Now);

        var result = await RunAsync();

        result.Steps[DatabaseCopyStep.Verify].Should().Be(CommandStepState.Failed);
        result.Verification!.Checks.Single(check => check.Name == "Tabelas sem dados").Detail.Should().Contain("public.pedidos: 10");
    }

    [Fact]
    public async Task TheData_IsCopiedMaskedInTheSelect_WithoutGeneratedColumns()
    {
        await RunAsync();

        var clientes = _scenario.Copier.Copied.Single(table => table.Table == "clientes");
        clientes.Columns.Select(column => column.Name).Should().Equal("id", "email", "cpf");
        clientes.Columns.Single(column => column.Name == "email").Method.Should().Be(MaskingMethod.FakeEmail);
        clientes.Columns.Single(column => column.Name == "cpf").Should().Match<MaskedColumnPlan>(
            column => column.Method == MaskingMethod.Partial && column.Argument == "0,2");
        clientes.Columns.Single(column => column.Name == "id").IsMasked.Should().BeFalse();
        _scenario.Copier.Copied.Single(table => table.Table == "pedidos").HasMaskedColumns.Should().BeFalse();
        _scenario.Copier.Destination!.Id.Should().Be(_scenario.Development.Id);
        _scenario.Copier.Closed.Should().BeTrue();
    }

    [Fact]
    public async Task TheAudit_IsSavedRunningBeforeAnyProcess_AndCompleteAtTheEnd()
    {
        var result = await RunAsync();

        var entry = _scenario.Catalog.Audit.Entries.Should().ContainSingle().Subject;
        _scenario.Catalog.SavedAuditStates.First().Status.Should().Be(DatabaseOperationStatus.Running);
        entry.Id.Should().Be(result.AuditId!.Value);
        entry.Status.Should().Be(DatabaseOperationStatus.Succeeded);
        entry.OperationType.Should().Be(DatabaseOperationType.CopyAndAnonymize);
        entry.SourceConnectionName.Should().Be("ECO Produção");
        entry.DestinationConnectionName.Should().Be("ECO Desenvolvimento");
        entry.AnonymizationProfile.Should().Be("ECO LGPD");
        entry.MaskedColumnsCount.Should().Be(2);
        entry.ToolVersions.Should().Contain("pg_dump 17.2");
        entry.SourceDatabaseVersion.Should().Contain("17.4");
        entry.AnonymousDumpSize.Should().Be(4096, "o tamanho do dump da estrutura");
        entry.DumpSize.Should().BeNull("o dump com dados de produção não existe");
        entry.RowsProcessed.Should().Be(20);
        entry.User.Should().Be("adriano");
        entry.Summary.Should().Contain("Result: SUCCESS");
    }

    [Fact]
    public async Task TheWorkspace_IsCleaned_AndOnlyMetadataStays()
    {
        await RunAsync();

        var workspace = _scenario.Workspaces.Created.Should().ContainSingle().Subject;
        workspace.CleanedKeepingAnonymized.Should().BeFalse();
        workspace.Metadata!.Status.Should().Be(DatabaseOperationStatus.Succeeded);
        workspace.Metadata.KeptAnonymizedArtifact.Should().BeFalse();
    }

    [Fact]
    public async Task KeepingTheAnonymousDump_IsHonoured_WhenAsked()
    {
        var result = await RunAsync(_scenario.Request(DatabaseCopyOptions.Default with { KeepAnonymizedArtifact = true }));

        _scenario.Workspaces.Created.Single().CleanedKeepingAnonymized.Should().BeTrue();
        result.KeptArtifactPath.Should().Contain("anonymized");
    }

    // --- Barreiras ---------------------------------------------------------------

    [Fact]
    public async Task AProductionDestination_IsRefusedBeforeAnything_AndAuditedAsBlocked()
    {
        var otherProduction = DatabaseConnection.Create("Outra produção", "10.0.0.9", 5432, "outra", "u",
            DatabaseEnvironment.Production, DatabaseSslMode.Prefer, null, default, DatabaseCopyScenario.Now);
        _scenario.Catalog.Connections.Seed(otherProduction);

        var request = _scenario.Request() with { DestinationConnectionId = otherProduction.Id };

        await FluentActions.Awaiting(() => RunAsync(request)).Should().ThrowAsync<DatabaseSecurityException>()
            .WithMessage("*produção*");

        _scenario.Tools.Calls.Should().BeEmpty();
        _scenario.Inspector.Calls.Should().BeEmpty();
        _scenario.Workspaces.Created.Should().BeEmpty();
        _scenario.Catalog.Audit.Entries.Should().ContainSingle().Which.Status.Should().Be(DatabaseOperationStatus.Blocked);
    }

    [Fact]
    public async Task DevelopmentToProduction_IsRefused()
    {
        var request = new DatabaseCopyRequest(
            _scenario.Development.Id,
            _scenario.Production.Id,
            DatabaseOperationType.Copy,
            null,
            DatabaseCopyOptions.Default with { RequireAnonymization = false });

        await FluentActions.Awaiting(() => RunAsync(request)).Should().ThrowAsync<DatabaseSecurityException>();

        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task APlainCopyOfProduction_IsRefused()
    {
        var request = _scenario.Request(operation: DatabaseOperationType.Copy);

        var exception = await FluentActions.Awaiting(() => RunAsync(request)).Should().ThrowAsync<DatabaseSecurityException>();

        exception.Which.Decision.Has(SecurityViolationCode.AnonymizationRequired).Should().BeTrue();
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutTheExplicitConfirmation_ProductionIsNotCopied()
    {
        var exception = await FluentActions.Awaiting(() => RunAsync(confirmed: false)).Should().ThrowAsync<DatabaseSecurityException>();

        exception.Which.Decision.Has(SecurityViolationCode.ConfirmationRequired).Should().BeTrue();
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CriticalProduction_DemandsTheDatabaseNameTyped()
    {
        var critical = new DatabaseCopyScenario(DatabaseEnvironment.CriticalProduction);
        var handler = critical.RunHandler();

        await FluentActions.Awaiting(() => handler.HandleAsync(new RunDatabaseCopy(critical.Request(), true, "eco"), null, Ct))
            .Should().ThrowAsync<DatabaseSecurityException>().WithMessage("*eco_core*");

        var result = await handler.HandleAsync(new RunDatabaseCopy(critical.Request(), true, "eco_core"), null, Ct);
        result.Succeeded.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task AMaskThatDoesNotFitTheColumn_StopsBeforeAnything()
    {
        var catalog = FakeMaskedCopier.DefaultCatalog();
        var clientes = catalog.Tables[0] with
        {
            Columns = [.. catalog.Tables[0].Columns.Select(column => column.Name == "cpf" ? column with { DataType = "bigint" } : column)],
        };
        _scenario.Copier.Catalog = catalog with { Tables = [clientes, catalog.Tables[1]] };

        var result = await RunAsync();

        result.Status.Should().Be(DatabaseOperationStatus.Failed);
        result.Steps[DatabaseCopyStep.ValidateMasking].Should().Be(CommandStepState.Failed);
        result.Steps[DatabaseCopyStep.Dump].Should().Be(CommandStepState.NotRun);
        result.Error.Should().Contain("public.clientes.cpf é bigint");
        _scenario.Tools.Calls.Should().Equal("catalog:ECO Produção:eco_core");
        _scenario.Workspaces.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task MaskingAKey_OrAMissingColumn_IsRefused()
    {
        _scenario.Profile.ReplaceRules(
        [
            new AnonymizationRuleSpec("public", "pedidos", "cliente_id", MaskingMethod.FixedNumber, "0", ColumnSensitivity.Low),
            new AnonymizationRuleSpec("public", "clientes", "sumiu", MaskingMethod.Hash, null, ColumnSensitivity.Low),
        ], DatabaseCopyScenario.Now);

        var result = await RunAsync();

        result.Error.Should().Contain("public.pedidos.cliente_id é chave").And.Contain("public.clientes.sumiu não existe");
        _scenario.Tools.Calls.Should().NotContain(call => call.StartsWith("open", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LargeObjects_AreAWarning_NotAStop()
    {
        _scenario.Copier.Catalog = FakeMaskedCopier.DefaultCatalog() with { LargeObjects = 3 };
        var progress = new ProgressLog<DatabaseCopyProgress>();

        var result = await RunAsync(progress: progress);

        result.Succeeded.Should().BeTrue(result.Error);
        progress.Items.Should().Contain(item => item.Step == DatabaseCopyStep.ValidateMasking && item.Detail!.Contains("3 objeto(s) grande(s)"));
    }

    [Fact]
    public async Task AStructureDumpThatBroughtData_IsNotRestored_AndTheSessionCloses()
    {
        _scenario.Tools.SchemaArchive = _scenario.Tools.SchemaArchive with { TableDataEntries = 1 };

        var result = await RunAsync();

        result.Steps[DatabaseCopyStep.CheckArtifact].Should().Be(CommandStepState.Failed);
        result.Error.Should().Contain("trouxe dados");
        _scenario.Tools.Calls.Should().NotContain(call => call.StartsWith("restore", StringComparison.Ordinal) || call.StartsWith("dropdb", StringComparison.Ordinal));
        _scenario.Copier.Closed.Should().BeTrue();
        _scenario.Workspaces.Created.Single().CleanedKeepingAnonymized.Should().BeFalse();
    }

    [Fact]
    public async Task AFailedTableCopy_FailsTheCopy_WithoutTheIndexes()
    {
        _scenario.Copier.CopyFailure = ("public.pedidos", new DomainException("public.pedidos: valor fora do tipo"));

        var result = await RunAsync();

        result.Status.Should().Be(DatabaseOperationStatus.Failed);
        result.Steps[DatabaseCopyStep.Restore].Should().Be(CommandStepState.Failed);
        result.Error.Should().Contain("public.pedidos");
        _scenario.Tools.Calls.Should().NotContain("restore:post-data");
        _scenario.Copier.Closed.Should().BeTrue();
    }

    [Fact]
    public async Task Canceling_DuringTheCopy_StopsAndClosesTheSession()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _scenario.Copier.DuringCopy = cancel.Cancel;

        var result = await RunAsync(cancellationToken: cancel.Token);

        result.Status.Should().Be(DatabaseOperationStatus.Canceled);
        result.Steps[DatabaseCopyStep.Restore].Should().Be(CommandStepState.Canceled);
        _scenario.Copier.Copied.Should().ContainSingle();
        _scenario.Copier.Closed.Should().BeTrue();
    }

    [Fact]
    public async Task DataOnly_KeepsTheDestinationStructure()
    {
        var result = await RunAsync(_scenario.Request(DatabaseCopyOptions.Default with { IncludeSchema = false, RecreateDestination = false }));

        result.Succeeded.Should().BeTrue(result.Error);
        result.Steps[DatabaseCopyStep.CheckArtifact].Should().Be(CommandStepState.NotRun);
        _scenario.Tools.Dumps.Should().BeEmpty();
        _scenario.Tools.Calls.Should().NotContain(call => call.StartsWith("restore", StringComparison.Ordinal));
        _scenario.Copier.Copied.Should().HaveCount(2);
    }

    // --- Falhas, cancelamento e tempo -----------------------------------------------

    [Fact]
    public async Task ADumpThatFails_LeavesTheRestNotRun_CleansUp_AndAuditsTheMaskedError()
    {
        _scenario.Tools.DumpResult = new PgToolRun(1, false, TimeSpan.FromSeconds(1),
            "pg_dump: error: connection to postgresql://dump_anon:s3nh4@db failed");

        var result = await RunAsync();

        result.Status.Should().Be(DatabaseOperationStatus.Failed);
        result.Error.Should().Contain("código 1").And.NotContain("s3nh4");
        result.Steps[DatabaseCopyStep.Dump].Should().Be(CommandStepState.Failed);
        result.Steps[DatabaseCopyStep.Restore].Should().Be(CommandStepState.NotRun);
        result.Steps[DatabaseCopyStep.Cleanup].Should().Be(CommandStepState.Succeeded);
        _scenario.Workspaces.Created.Single().CleanedKeepingAnonymized.Should().BeFalse();

        var entry = _scenario.Catalog.Audit.Entries.Single();
        entry.Status.Should().Be(DatabaseOperationStatus.Failed);
        entry.Error.Should().NotContain("s3nh4");
    }

    [Fact]
    public async Task ATimeout_IsReportedAsSuch()
    {
        _scenario.Tools.RestoreResult = new PgToolRun(-1, true, TimeSpan.FromHours(6), string.Empty);

        var result = await RunAsync();

        result.Error.Should().Contain("tempo limite");
        result.Steps[DatabaseCopyStep.Restore].Should().Be(CommandStepState.Failed);
    }

    [Fact]
    public async Task AFailedRestore_NeverKeepsTheArtifact()
    {
        _scenario.Tools.RestoreResult = new PgToolRun(1, false, TimeSpan.Zero, "erro");

        await RunAsync(_scenario.Request(DatabaseCopyOptions.Default with { KeepAnonymizedArtifact = true }));

        _scenario.Workspaces.Created.Single().CleanedKeepingAnonymized.Should().BeFalse();
    }

    [Fact]
    public async Task Canceling_DuringTheRestore_StopsCleansUpAndAuditsCanceled()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _scenario.Tools.DuringRestore = cancel.Cancel;

        var result = await RunAsync(cancellationToken: cancel.Token);

        result.Status.Should().Be(DatabaseOperationStatus.Canceled);
        result.Steps[DatabaseCopyStep.Restore].Should().Be(CommandStepState.Canceled);
        result.Steps[DatabaseCopyStep.Verify].Should().Be(CommandStepState.NotRun);
        _scenario.Workspaces.Created.Single().CleanedKeepingAnonymized.Should().BeFalse();
        _scenario.Catalog.Audit.Entries.Single().Status.Should().Be(DatabaseOperationStatus.Canceled);
    }

    [Fact]
    public async Task AnUnexpectedError_IsLoggedAndShownGenerically()
    {
        _scenario.Inspector.Failure = new InvalidOperationException("detalhe interno com Password=s3nh4");

        var result = await RunAsync();

        result.Status.Should().Be(DatabaseOperationStatus.Failed);
        result.Error.Should().Contain("erro inesperado").And.NotContain("s3nh4");
    }

    [Fact]
    public async Task AnUnreachableSource_FailsTheFirstStep()
    {
        _scenario.Inspector.Diagnostics["ECO Produção"] = ServerDiagnostics.Failed("timeout");

        var result = await RunAsync();

        result.Steps[DatabaseCopyStep.ValidateSource].Should().Be(CommandStepState.Failed);
        result.Error.Should().Contain("timeout");
    }

    [Fact]
    public async Task AnUnreachableDestination_FailsTheSecondStep_TestingTheMaintenanceDatabase()
    {
        _scenario.Inspector.Diagnostics["ECO Desenvolvimento"] = ServerDiagnostics.Failed("recusado");

        var result = await RunAsync();

        result.Steps[DatabaseCopyStep.ValidateDestination].Should().Be(CommandStepState.Failed);
        _scenario.Inspector.Calls.Should().Contain("test:ECO Desenvolvimento:postgres");
    }

    [Fact]
    public async Task OldTools_AreRefused_BeforeTheDump()
    {
        _scenario.Locator.Tools = FakeToolLocator.WithVersion(16, 4);
        _scenario.Inspector.Diagnostics["ECO Produção"] = FakeServerInspector.Connected("17.1");

        var result = await RunAsync();

        result.Steps[DatabaseCopyStep.ValidatePermissions].Should().Be(CommandStepState.Failed);
        result.Error.Should().Contain("17");
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RecreatingWithoutCreatedb_IsRefused()
    {
        _scenario.Locator.Tools = FakeToolLocator.WithVersion(17, 2, PostgresTool.CreateDb);

        (await RunAsync()).Error.Should().Contain("createdb");
    }

    [Fact]
    public async Task NotEnoughDisk_IsRefused_BeforeAPlainDump_ButTheMaskedCopyNeedsNoDisk()
    {
        _scenario.Workspaces.AvailableBytes = 1024;

        (await RunAsync()).Succeeded.Should().BeTrue("só a estrutura vai para o disco");

        var test = DatabaseConnection.Create("ECO Teste", "localhost", 5432, "eco_test", "postgres",
            DatabaseEnvironment.Test, DatabaseSslMode.Prefer, null, ConnectionPermissions.FromFlags(ConnectionPermission.All), DatabaseCopyScenario.Now);
        _scenario.Catalog.Connections.Seed(test);
        _scenario.Tools.Calls.Clear();

        var plain = await RunAsync(new DatabaseCopyRequest(
            _scenario.Development.Id, test.Id, DatabaseOperationType.Copy, null, DatabaseCopyOptions.Default with { RequireAnonymization = false }), confirmed: false);

        plain.Error.Should().Contain("Espaço insuficiente");
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ASecondCopyAtTheSameTime_IsRefused()
    {
        using var busy = _scenario.Gate.Enter(Guid.CreateVersion7());

        await FluentActions.Awaiting(() => RunAsync()).Should().ThrowAsync<DomainException>().WithMessage("*em andamento*");
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TheGate_IsReleasedAfterwards()
    {
        await RunAsync();

        _scenario.Gate.IsBusy.Should().BeFalse();
    }

    // --- Opções -------------------------------------------------------------

    [Fact]
    public async Task WithoutRecreating_TheDestinationIsNotDroppedNorCreated()
    {
        var result = await RunAsync(_scenario.Request(DatabaseCopyOptions.Default with { RecreateDestination = false }));

        result.Steps[DatabaseCopyStep.PrepareDestination].Should().Be(CommandStepState.NotRun);
        _scenario.Tools.Calls.Should().NotContain(call => call.StartsWith("dropdb", StringComparison.Ordinal) || call.StartsWith("createdb", StringComparison.Ordinal));
        _scenario.Inspector.Calls.Should().Contain("test:ECO Desenvolvimento:eco_dev");
    }

    [Fact]
    public async Task ForceDrop_DependsOnTheDestinationVersion()
    {
        _scenario.Locator.Tools = FakeToolLocator.WithVersion(14, 22);
        _scenario.Inspector.Diagnostics["ECO Produção"] = FakeServerInspector.Connected("14.10");
        _scenario.Inspector.Diagnostics["ECO Desenvolvimento"] = FakeServerInspector.Connected("12.9");

        await RunAsync();

        _scenario.Tools.Calls.Should().Contain(call => call.StartsWith("dropdb", StringComparison.Ordinal));
        _scenario.Tools.DropForced.Should().BeFalse();
    }

    [Fact]
    public async Task TheCopy_UsesTheOldestToolSetThatReadsTheSource_ForTheDumpAndTheRestore()
    {
        // O pgAdmin 18 instalado ao lado do PostgreSQL 14: o 18 não restaura num servidor 14.
        var eighteen = FakeToolLocator.WithVersion(18, 4);
        var fourteen = FakeToolLocator.WithVersion(14, 22);
        _scenario.Locator.Tools = eighteen with { Sets = [eighteen.Tools, fourteen.Tools] };
        _scenario.Inspector.Diagnostics["ECO Produção"] = FakeServerInspector.Connected("14.10");
        _scenario.Inspector.Diagnostics["ECO Desenvolvimento"] = FakeServerInspector.Connected("14.10");

        var result = await RunAsync();

        result.Error.Should().BeNull();
        _scenario.Catalog.Audit.Entries.Single().ToolVersions.Should().Contain("pg_dump 14.22").And.NotContain("18.4");
        _scenario.Tools.Dumps.Should().NotBeEmpty().And.OnlyContain(dump => dump.SourceVersion == new PostgresVersion(14, 10));
        _scenario.Tools.Restores.Should().NotBeEmpty().And.OnlyContain(restore => restore.SourceVersion == new PostgresVersion(14, 10));
    }

    [Fact]
    public async Task OnlyANewerRestore_ForAnOlderDestination_IsRefusedBeforeTheDump()
    {
        _scenario.Locator.Tools = FakeToolLocator.WithVersion(18, 4);
        _scenario.Inspector.Diagnostics["ECO Produção"] = FakeServerInspector.Connected("14.10");
        _scenario.Inspector.Diagnostics["ECO Desenvolvimento"] = FakeServerInspector.Connected("14.10");

        var result = await RunAsync();

        result.Steps[DatabaseCopyStep.ValidatePermissions].Should().Be(CommandStepState.Failed);
        result.Error.Should().Contain("transaction_timeout");
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutVerification_TheVerifyStepIsSkipped()
    {
        var result = await RunAsync(_scenario.Request(DatabaseCopyOptions.Default with { VerifyAfterRestore = false }));

        result.Succeeded.Should().BeTrue();
        result.Steps[DatabaseCopyStep.Verify].Should().Be(CommandStepState.NotRun);
        result.Verification.Should().BeNull();
    }

    [Fact]
    public async Task AFailedVerification_FailsTheCopy()
    {
        _scenario.Inspector.DestinationStructure = _scenario.Inspector.Structure with { Indexes = 1 };

        var result = await RunAsync();

        result.Status.Should().Be(DatabaseOperationStatus.Failed);
        result.Verification!.Result.Should().Be("FAIL");
        result.Steps[DatabaseCopyStep.Verify].Should().Be(CommandStepState.Failed);
        _scenario.Catalog.Audit.Entries.Single().Summary.Should().Contain("Indexes: FAIL");
    }

    [Fact]
    public async Task SensitiveDataThatArrivedIntact_FailsTheVerification()
    {
        _scenario.Inspector.Unmasked.Add(_scenario.Development.Id);

        var result = await RunAsync();

        result.Verification!.Checks.Should().Contain(check =>
            check.Category == VerificationEvaluator.SensitiveData && check.Outcome == CheckOutcome.Fail);
        result.Status.Should().Be(DatabaseOperationStatus.Failed);
    }

    [Fact]
    public async Task ADevelopmentCopy_SkipsTheAnonymizer_AndDumpsPlainly()
    {
        var test = DatabaseConnection.Create("ECO Teste", "localhost", 5432, "eco_test", "postgres",
            DatabaseEnvironment.Test, DatabaseSslMode.Prefer, null, ConnectionPermissions.FromFlags(ConnectionPermission.All), DatabaseCopyScenario.Now);
        _scenario.Catalog.Connections.Seed(test);
        var request = new DatabaseCopyRequest(
            _scenario.Development.Id, test.Id, DatabaseOperationType.Copy, null,
            DatabaseCopyOptions.Default with { RequireAnonymization = false });

        var result = await RunAsync(request, confirmed: false);

        result.Succeeded.Should().BeTrue(result.Error);
        result.Steps[DatabaseCopyStep.ValidateMasking].Should().Be(CommandStepState.NotRun);
        var dump = _scenario.Tools.Dumps.Single();
        dump.SchemaOnly.Should().BeFalse();
        dump.Snapshot.Should().BeNull();
        dump.OutputDirectory.Should().Contain("dump");
        _scenario.Copier.Copied.Should().BeEmpty();
        _scenario.Catalog.Audit.Entries.Single().DumpSize.Should().Be(4096);
    }

    [Fact]
    public async Task SchemaOnly_DoesNotCountRows()
    {
        var result = await RunAsync(_scenario.Request(DatabaseCopyOptions.Default with { IncludeData = false }));

        result.Succeeded.Should().BeTrue(result.Error);
        _scenario.Inspector.Calls.Should().NotContain(call => call.StartsWith("rows:", StringComparison.Ordinal));
        _scenario.Tools.Dumps.Single().Jobs.Should().Be(1);
        _scenario.Copier.Copied.Should().BeEmpty();
        _scenario.Tools.Calls.Should().NotContain("sequences");
    }

    [Fact]
    public async Task AnUnknownConnection_IsADomainError()
    {
        var request = _scenario.Request() with { DestinationConnectionId = Guid.CreateVersion7() };

        await FluentActions.Awaiting(() => RunAsync(request)).Should().ThrowAsync<DomainException>().WithMessage("*destino*");
    }

    [Fact]
    public async Task OnlyCopyOperations_AreAccepted()
    {
        var request = _scenario.Request() with { Operation = DatabaseOperationType.Restore };

        await FluentActions.Awaiting(() => RunAsync(request)).Should().ThrowAsync<DomainException>();
    }
}
