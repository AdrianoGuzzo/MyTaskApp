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
        _scenario.Tools.Calls.Should().Equal("anonymous-dump", "list", "dropdb:eco_dev", "createdb:eco_dev", "restore");

        progress.Items.Where(item => item.State == CommandStepState.Running).Select(item => item.Step).Distinct()
            .Should().BeInAscendingOrder();
        progress.Items.Select(item => item.Percent).Should().BeInAscendingOrder();
        progress.Items.Last().Percent.Should().Be(100);
        progress.Items.Should().Contain(item => item.Line != null && item.Line.Text.Contains("dumping contents"));
        result.Verification!.Result.Should().Be("SUCCESS");
    }

    [Fact]
    public async Task TheDump_GoesThroughTheMaskedConnection_IntoTheAnonymizedFolder()
    {
        await RunAsync();

        var dump = _scenario.Tools.Dumps.Should().ContainSingle().Subject;
        dump.Anonymous.Should().BeTrue();
        dump.Connection.Id.Should().Be(_scenario.Masked.Id);
        dump.OutputDirectory.Should().Contain("anonymized");
        dump.ProtectedEndpoints.Should().Contain(_scenario.Production.Snapshot().EndpointKey);

        var restore = _scenario.Tools.Restores.Should().ContainSingle().Subject;
        restore.Target.Id.Should().Be(_scenario.Development.Id);
        restore.ArchiveDirectory.Should().Be(dump.OutputDirectory);
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
        entry.SourceDatabaseVersion.Should().Contain("16.4");
        entry.AnonymousDumpSize.Should().Be(4096);
        entry.DumpSize.Should().BeNull("o dump bruto de produção não existe");
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
    public async Task AMaskedRoleThatIsNotMasked_StopsBeforeTheDump()
    {
        _scenario.Anonymizer.Status = FakeAnonymizerInspector.Healthy() with { CurrentRoleMasked = false };

        var result = await RunAsync();

        result.Status.Should().Be(DatabaseOperationStatus.Failed);
        result.Steps[DatabaseCopyStep.ValidateAnonymizer].Should().Be(CommandStepState.Failed);
        result.Steps[DatabaseCopyStep.Dump].Should().Be(CommandStepState.NotRun);
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TransparentMaskingOff_StopsBeforeTheDump()
    {
        _scenario.Anonymizer.Status = FakeAnonymizerInspector.Healthy() with { TransparentMaskingOn = false };

        var result = await RunAsync();

        result.Error.Should().Contain("transparent_dynamic_masking");
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RulesMissingOnTheServer_StopBeforeTheDump()
    {
        _scenario.Anonymizer.Status = FakeAnonymizerInspector.Healthy(
            new ServerMaskingRule("public", "clientes", "email", "MASKED WITH FUNCTION anon.partial_email(email)"));

        var result = await RunAsync();

        result.Error.Should().Contain("public.clientes.cpf");
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAnonymizerOneX_IsNotSupported()
    {
        _scenario.Anonymizer.Status = FakeAnonymizerInspector.Healthy() with { InstalledVersion = "1.3.2" };

        var result = await RunAsync();

        result.Error.Should().Contain("2.x");
        _scenario.Tools.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TheCanary_AbortsWhenTheMaskedConnectionSeesTheRealData()
    {
        _scenario.Inspector.Unmasked.Add(_scenario.Masked.Id);

        var result = await RunAsync();

        result.Status.Should().Be(DatabaseOperationStatus.Failed);
        result.Error.Should().Contain("Nenhum dump foi feito");
        _scenario.Tools.Calls.Should().BeEmpty();
        _scenario.Workspaces.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task ADumpCarryingTheAnonRules_IsNotRestored_AndIsCleaned()
    {
        _scenario.Tools.Archive = _scenario.Tools.Archive with { SecurityLabelEntries = 3 };

        var result = await RunAsync();

        result.Steps[DatabaseCopyStep.CheckArtifact].Should().Be(CommandStepState.Failed);
        _scenario.Tools.Calls.Should().NotContain("restore").And.NotContain(call => call.StartsWith("dropdb", StringComparison.Ordinal));
        _scenario.Workspaces.Created.Single().CleanedKeepingAnonymized.Should().BeFalse();
    }

    [Fact]
    public async Task ADumpWithTheAnonExtension_IsNotRestored()
    {
        _scenario.Tools.Archive = _scenario.Tools.Archive with { HasAnonExtension = true };

        (await RunAsync()).Steps[DatabaseCopyStep.CheckArtifact].Should().Be(CommandStepState.Failed);
    }

    [Fact]
    public async Task AnEmptyDump_IsNotRestored()
    {
        _scenario.Tools.Archive = _scenario.Tools.Archive with { TableDataEntries = 0 };

        (await RunAsync()).Error.Should().Contain("sem dados");
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
    public async Task NotEnoughDisk_IsRefused_BeforeTheDump()
    {
        _scenario.Workspaces.AvailableBytes = 1024;

        var result = await RunAsync();

        result.Error.Should().Contain("Espaço insuficiente");
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
        _scenario.Tools.Calls.Should().Equal("anonymous-dump", "list", "restore");
        _scenario.Inspector.Calls.Should().Contain("test:ECO Desenvolvimento:eco_dev");
    }

    [Fact]
    public async Task ForceDrop_DependsOnTheDestinationVersion()
    {
        _scenario.Inspector.Diagnostics["ECO Desenvolvimento"] = FakeServerInspector.Connected("12.9");

        await RunAsync();

        _scenario.Tools.DropForced.Should().BeFalse();
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
        result.Steps[DatabaseCopyStep.ValidateAnonymizer].Should().Be(CommandStepState.NotRun);
        var dump = _scenario.Tools.Dumps.Single();
        dump.Anonymous.Should().BeFalse();
        dump.OutputDirectory.Should().Contain("dump");
        _scenario.Catalog.Audit.Entries.Single().DumpSize.Should().Be(4096);
    }

    [Fact]
    public async Task SchemaOnly_DoesNotCountRows()
    {
        var result = await RunAsync(_scenario.Request(DatabaseCopyOptions.Default with { IncludeData = false }));

        result.Succeeded.Should().BeTrue(result.Error);
        _scenario.Inspector.Calls.Should().NotContain(call => call.StartsWith("rows:", StringComparison.Ordinal));
        _scenario.Tools.Dumps.Single().Jobs.Should().Be(1);
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
