using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.Tests.DatabaseOperations;

/// <summary>O cadastro de conexões e perfis (ADR-056): a senha vai ao cofre, e só a referência fica.</summary>
public class DatabaseCatalogHandlersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DatabaseCopyScenario _scenario = new();

    private FakeDatabaseCatalog Catalog => _scenario.Catalog;

    private static SaveDatabaseConnection NewConnection(
        string name = "ECO Homologação",
        DatabaseEnvironment environment = DatabaseEnvironment.Staging,
        string? password = "s3nh4-forte") =>
        new(null, name, "10.0.0.5", 5432, "eco_hml", "app", environment, DatabaseSslMode.Require, "homologação",
            ConnectionPermissions.FromFlags(ConnectionPermission.All), SecretText.FromOptional(password));

    private SaveDatabaseConnectionHandler SaveHandler() =>
        new(Catalog.Connections, _scenario.Credentials, Catalog, _scenario.Clock, NullLogger<SaveDatabaseConnectionHandler>.Instance);

    // --- Conexões ---------------------------------------------------------------

    [Fact]
    public async Task SavingANewConnection_StoresThePasswordInTheVault_AndOnlyTheReference()
    {
        var id = await SaveHandler().HandleAsync(NewConnection(), Ct);

        var saved = Catalog.Connections.Items.Single(connection => connection.Id == id);
        saved.SecretReference.Should().Be(saved.SecretNameForThis());
        _scenario.Credentials.Secrets[saved.SecretReference!].Should().Be("s3nh4-forte");
        Catalog.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task AVaultThatRefuses_SavesNothing()
    {
        _scenario.Credentials.StoreFailure = Failures.Domain("Guardar credenciais precisa do secret-tool.");

        await FluentActions.Awaiting(() => SaveHandler().HandleAsync(NewConnection(), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*secret-tool*");

        Catalog.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task EditingWithoutAPassword_KeepsTheStoredOne()
    {
        var id = await SaveHandler().HandleAsync(NewConnection(), Ct);

        await SaveHandler().HandleAsync(NewConnection(name: "ECO HML", password: null) with { Id = id }, Ct);

        var saved = Catalog.Connections.Items.Single(connection => connection.Id == id);
        saved.Name.Should().Be("ECO HML");
        _scenario.Credentials.Secrets[saved.SecretReference!].Should().Be("s3nh4-forte");
    }

    [Fact]
    public async Task ARepeatedName_IsRefused()
    {
        await FluentActions.Awaiting(() => SaveHandler().HandleAsync(NewConnection(name: "eco produção"), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*Já existe*");
    }

    [Fact]
    public async Task SavingAProductionConnection_CutsWhatProductionDoesNotAdmit()
    {
        var id = await SaveHandler().HandleAsync(NewConnection("Prod 2", DatabaseEnvironment.Production), Ct);

        var saved = Catalog.Connections.Items.Single(connection => connection.Id == id);
        saved.CanRestore.Should().BeFalse();
        saved.CanExecuteSql.Should().BeFalse();
        saved.RequireAnonymization.Should().BeTrue();
    }

    [Fact]
    public async Task TheListShowsWhetherAPasswordIsStored_NeverThePassword()
    {
        await SaveHandler().HandleAsync(NewConnection(), Ct);

        var rows = await new GetDatabaseConnectionsHandler(Catalog.Connections, _scenario.Credentials)
            .HandleAsync(new GetDatabaseConnections(), Ct);

        rows.Single(row => row.Name == "ECO Homologação").HasPassword.Should().BeTrue();
        rows.Single(row => row.Name == "ECO Produção").HasPassword.Should().BeFalse();
        rows.Single(row => row.Name == "ECO Produção").IsProtected.Should().BeTrue();
    }

    [Fact]
    public async Task NoPublicResult_HasAPlaceForAPassword()
    {
        var types = typeof(GetDatabaseConnections).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(GetDatabaseConnections).Namespace && type.IsPublic);

        foreach (var type in types)
        {
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => property.Name)
                .Should().NotContain(name => name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Secret", StringComparison.OrdinalIgnoreCase) && name != "SecretReference", type.Name);
        }

        await Task.CompletedTask;
    }

    [Fact]
    public void ASaveCommand_PrintsWithoutThePassword()
    {
        NewConnection().ToString().Should().NotContain("s3nh4");
    }

    [Fact]
    public async Task Disabling_IsSaved()
    {
        await new SetDatabaseConnectionEnabledHandler(Catalog.Connections, Catalog, _scenario.Clock)
            .HandleAsync(new SetDatabaseConnectionEnabled(_scenario.Development.Id, false), Ct);

        _scenario.Development.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task AConnectionUsedByAProfile_CannotBeDeleted()
    {
        var handler = new DeleteDatabaseConnectionHandler(
            Catalog.Connections, Catalog.AnonymizationProfiles, Catalog.CopyProfiles, Catalog.SavedDatabases, _scenario.Credentials, Catalog,
            NullLogger<DeleteDatabaseConnectionHandler>.Instance);

        await FluentActions.Awaiting(() => handler.HandleAsync(new DeleteDatabaseConnection(_scenario.Production.Id), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*perfil*");

        Catalog.CopyProfiles.Items.Add(DatabaseCopyProfile.Create("x", _scenario.Production.Id, _scenario.Development.Id,
            _scenario.Profile.Id, DatabaseCopyOptions.Default, DatabaseCopyScenario.Now));
        await FluentActions.Awaiting(() => handler.HandleAsync(new DeleteDatabaseConnection(_scenario.Development.Id), Ct))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task DeletingAConnection_TakesItsPasswordFromTheVault()
    {
        var id = await SaveHandler().HandleAsync(NewConnection(), Ct);
        var handler = new DeleteDatabaseConnectionHandler(
            Catalog.Connections, Catalog.AnonymizationProfiles, Catalog.CopyProfiles, Catalog.SavedDatabases, _scenario.Credentials, Catalog,
            NullLogger<DeleteDatabaseConnectionHandler>.Instance);

        await handler.HandleAsync(new DeleteDatabaseConnection(id), Ct);

        Catalog.Connections.Items.Should().NotContain(connection => connection.Id == id);
        _scenario.Credentials.Secrets.Should().BeEmpty();
    }

    [Fact]
    public async Task TestingADraft_UsesTheTypedPassword_WithoutSavingAnything()
    {
        var handler = new TestDatabaseConnectionHandler(
            Catalog.Connections, _scenario.Inspector, _scenario.Policy, _scenario.Clock, NullLogger<TestDatabaseConnectionHandler>.Instance);

        var result = await handler.HandleAsync(
            new TestDatabaseConnection(null, "", "10.0.0.5", 5432, "eco", "app", DatabaseEnvironment.Production, DatabaseSslMode.Prefer, new SecretText("digitada")),
            Ct);

        result.Connected.Should().BeTrue();
        var test = _scenario.Inspector.Tests.Single();
        test.Password!.Reveal().Should().Be("digitada");
        test.Connection.SecretReference.Should().BeNull();
        Catalog.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task TestingASavedConnection_UsesTheStoredReference()
    {
        var id = await SaveHandler().HandleAsync(NewConnection(), Ct);
        var handler = new TestDatabaseConnectionHandler(
            Catalog.Connections, _scenario.Inspector, _scenario.Policy, _scenario.Clock, NullLogger<TestDatabaseConnectionHandler>.Instance);

        await handler.HandleAsync(
            new TestDatabaseConnection(id, "ECO Homologação", "10.0.0.5", 5432, "eco_hml", "app", DatabaseEnvironment.Staging, DatabaseSslMode.Require, null),
            Ct);

        var test = _scenario.Inspector.Tests.Single();
        test.Connection.Id.Should().Be(id);
        test.Connection.SecretReference.Should().StartWith("postgres-");
        test.Password.Should().BeNull();
    }

    [Fact]
    public async Task TestingAnInvalidDraft_NeverReachesTheNetwork()
    {
        var handler = new TestDatabaseConnectionHandler(
            Catalog.Connections, _scenario.Inspector, _scenario.Policy, _scenario.Clock, NullLogger<TestDatabaseConnectionHandler>.Instance);

        await FluentActions.Awaiting(() => handler.HandleAsync(
                new TestDatabaseConnection(null, "x", "-oProxyCommand=x", 5432, "eco", "app", DatabaseEnvironment.Development, DatabaseSslMode.Prefer, null),
                Ct))
            .Should().ThrowAsync<DomainException>();

        _scenario.Inspector.Tests.Should().BeEmpty();
    }

    // --- Perfis de anonimização ----------------------------------------------------

    [Fact]
    public async Task AnAnonymizationProfile_IsSavedWithItsConfirmedRules()
    {
        var handler = new SaveAnonymizationProfileHandler(
            Catalog.AnonymizationProfiles, Catalog.Connections, Catalog, _scenario.Clock, NullLogger<SaveAnonymizationProfileHandler>.Instance);

        var id = await handler.HandleAsync(new SaveAnonymizationProfile(
            null, "Outro LGPD", null, _scenario.Production.Id,
            [new AnonymizationRuleRow("public", "clientes", "cpf", MaskingMethod.Partial, "3,2", ColumnSensitivity.High)]), Ct);

        var rows = await new GetAnonymizationProfilesHandler(Catalog.AnonymizationProfiles).HandleAsync(new GetAnonymizationProfiles(), Ct);
        var rule = rows.Single(row => row.Id == id).Rules.Should().ContainSingle().Subject;
        rule.ColumnKey.Should().Be("public.clientes.cpf");
        rule.Method.Should().Be(MaskingMethod.Partial);
        rule.Argument.Should().Be("3,2");

        await handler.HandleAsync(new SaveAnonymizationProfile(id, "Outro LGPD", "d", _scenario.Production.Id, []), Ct);
        Catalog.AnonymizationProfiles.Items.Single(profile => profile.Id == id).Rules.Should().BeEmpty();
    }

    [Fact]
    public async Task SkippedTables_AreSavedWithTheProfile_AndComeBackInTheRow()
    {
        var handler = new SaveAnonymizationProfileHandler(
            Catalog.AnonymizationProfiles, Catalog.Connections, Catalog, _scenario.Clock, NullLogger<SaveAnonymizationProfileHandler>.Instance);

        var id = await handler.HandleAsync(new SaveAnonymizationProfile(null, "Com logs vazios", null, _scenario.Production.Id, [])
        {
            SkippedTables = [new SkippedTableRow("public", "logs"), new SkippedTableRow("audit", "eventos")],
        }, Ct);

        var row = (await new GetAnonymizationProfilesHandler(Catalog.AnonymizationProfiles).HandleAsync(new GetAnonymizationProfiles(), Ct))
            .Single(profile => profile.Id == id);
        row.SkippedTables.Select(table => table.TableKey).Should().Equal("audit.eventos", "public.logs");
    }

    [Fact]
    public async Task TheSourceTables_ShowAPartitionedTableOnce_WithItsPartsAdded()
    {
        _scenario.Copier.Catalog = new SourceCatalog(
        [
            new SourceTable("public", "clientes", 100, 10, []),
            new SourceTable("public", "eventos_2025", 30, 3, [], "public.eventos"),
            new SourceTable("public", "eventos_2026", 70, 7, [], "public.eventos"),
        ], [], [], 0);

        var rows = await new GetSourceTablesHandler(Catalog.Connections, _scenario.Copier, _scenario.Policy)
            .HandleAsync(new GetSourceTables(_scenario.Production.Id), Ct);

        rows.Should().Equal(
            new SourceTableRow("public", "clientes", 10, 100, 0),
            new SourceTableRow("public", "eventos", 10, 100, 2));
    }

    [Fact]
    public async Task ThePreview_SaysWhichForeignKeyASkippedTableWouldBreak()
    {
        _scenario.Copier.Catalog = FakeMaskedCopier.DefaultCatalog() with
        {
            ForeignKeys = [new ForeignKeyLink("public.pedidos", "public.clientes")],
        };

        var result = await PreviewHandler().HandleAsync(new PreviewMasking(_scenario.Production.Id, null,
            [new AnonymizationRuleRow("public", "clientes", "email", MaskingMethod.FakeEmail, null, ColumnSensitivity.High)])
        {
            SkippedTables = [new SkippedTableRow("public", "clientes")],
        }, Ct);

        result.Problems.Should().ContainSingle().Which.Should().Contain("public.pedidos tem FK para public.clientes");
        _scenario.Copier.Previews.Should().BeEmpty("a tabela sem dados não tem coluna mascarada para mostrar");
    }

    [Fact]
    public async Task AProfileForAnUnknownConnection_IsRefused()
    {
        var handler = new SaveAnonymizationProfileHandler(
            Catalog.AnonymizationProfiles, Catalog.Connections, Catalog, _scenario.Clock, NullLogger<SaveAnonymizationProfileHandler>.Instance);

        await FluentActions.Awaiting(() => handler.HandleAsync(new SaveAnonymizationProfile(null, "x", null, Guid.CreateVersion7(), []), Ct))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task AnAnonymizationProfileInUse_CannotBeDeleted_ButAFreeOneCan()
    {
        var handler = new DeleteAnonymizationProfileHandler(Catalog.AnonymizationProfiles, Catalog.CopyProfiles, Catalog.SavedDatabases, Catalog);
        Catalog.CopyProfiles.Items.Add(DatabaseCopyProfile.Create("x", _scenario.Production.Id, _scenario.Development.Id,
            _scenario.Profile.Id, DatabaseCopyOptions.Default, DatabaseCopyScenario.Now));

        await FluentActions.Awaiting(() => handler.HandleAsync(new DeleteAnonymizationProfile(_scenario.Profile.Id), Ct))
            .Should().ThrowAsync<DomainException>();

        Catalog.CopyProfiles.Items.Clear();
        await handler.HandleAsync(new DeleteAnonymizationProfile(_scenario.Profile.Id), Ct);
        Catalog.AnonymizationProfiles.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task EnablingProfiles_IsSaved()
    {
        await new SetAnonymizationProfileEnabledHandler(Catalog.AnonymizationProfiles, Catalog, _scenario.Clock)
            .HandleAsync(new SetAnonymizationProfileEnabled(_scenario.Profile.Id, false), Ct);

        _scenario.Profile.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Suggestions_ComeFromTheColumnsOnly()
    {
        var suggestions = await new SuggestSensitiveColumnsHandler(Catalog.Connections, _scenario.Inspector, _scenario.Policy)
            .HandleAsync(new SuggestSensitiveColumns(_scenario.Production.Id), Ct);

        suggestions.Select(suggestion => suggestion.ColumnKey).Should().Contain(["public.clientes.email", "public.clientes.cpf"]);
        _scenario.Inspector.Calls.Should().NotContain(call => call.StartsWith("fingerprint", StringComparison.Ordinal));
    }

    private PreviewMaskingHandler PreviewHandler() => new(Catalog.Connections, _scenario.Copier, _scenario.Policy, _scenario.Clock);

    [Fact]
    public async Task ThePreview_ShowsOnlyMaskedValues_OfTheMarkedColumns()
    {
        var result = await PreviewHandler().HandleAsync(new PreviewMasking(_scenario.Production.Id, null,
        [
            new AnonymizationRuleRow("public", "clientes", "email", MaskingMethod.FakeEmail, null, ColumnSensitivity.High),
        ], Rows: 3), Ct);

        result.Problems.Should().BeEmpty();
        var column = result.Columns.Should().ContainSingle().Subject;
        column.ColumnKey.Should().Be("public.clientes.email");
        column.Values.Should().Equal("m1", "m2", "m3");

        // Só a tabela com coluna mascarada vai ao servidor.
        _scenario.Copier.Previews.Single().Tables.Select(table => table.Table).Should().Equal("clientes");
    }

    [Fact]
    public async Task ThePreview_SaysWhatDoesNotFit_AndReadsNothingWhenNothingIsMasked()
    {
        var result = await PreviewHandler().HandleAsync(new PreviewMasking(_scenario.Production.Id, null,
        [
            new AnonymizationRuleRow("public", "clientes", "id", MaskingMethod.Hash, null, ColumnSensitivity.Low),
            new AnonymizationRuleRow("public", "sumiu", "x", MaskingMethod.Hash, null, ColumnSensitivity.Low),
        ]), Ct);

        result.Problems.Should().Contain(problem => problem.Contains("public.clientes.id"));
        result.Problems.Should().Contain(problem => problem.Contains("public.sumiu.x não existe"));
    }

    [Fact]
    public async Task ThePreview_OfAnInvalidArgument_IsADomainError()
    {
        await FluentActions.Awaiting(() => PreviewHandler().HandleAsync(new PreviewMasking(_scenario.Production.Id, null,
            [new AnonymizationRuleRow("public", "clientes", "cpf", MaskingMethod.Partial, "abc", ColumnSensitivity.High)]), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*início,fim*");

        _scenario.Copier.Previews.Should().BeEmpty();
    }

    // --- Perfis de cópia -------------------------------------------------------

    private SaveDatabaseCopyProfileHandler CopyHandler() => new(
        Catalog.CopyProfiles, Catalog.Connections, Catalog.AnonymizationProfiles, _scenario.Policy, Catalog, _scenario.Clock);

    [Fact]
    public async Task ACopyProfile_IsSaved_AndListed()
    {
        var id = await CopyHandler().HandleAsync(new SaveDatabaseCopyProfile(
            null, "ECO Production → ECO Development", _scenario.Production.Id, _scenario.Development.Id, _scenario.Profile.Id, DatabaseCopyOptions.Default), Ct);

        var rows = await new GetDatabaseCopyProfilesHandler(Catalog.CopyProfiles).HandleAsync(new GetDatabaseCopyProfiles(), Ct);
        rows.Single().Id.Should().Be(id);

        await CopyHandler().HandleAsync(new SaveDatabaseCopyProfile(
            id, "Renomeado", _scenario.Production.Id, _scenario.Development.Id, _scenario.Profile.Id, DatabaseCopyOptions.Default), Ct);
        Catalog.CopyProfiles.Items.Single().Name.Should().Be("Renomeado");

        await new SetDatabaseCopyProfileEnabledHandler(Catalog.CopyProfiles, Catalog, _scenario.Clock)
            .HandleAsync(new SetDatabaseCopyProfileEnabled(id, false), Ct);
        Catalog.CopyProfiles.Items.Single().IsEnabled.Should().BeFalse();

        await new DeleteDatabaseCopyProfileHandler(Catalog.CopyProfiles, Catalog).HandleAsync(new DeleteDatabaseCopyProfile(id), Ct);
        Catalog.CopyProfiles.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ACopyProfileIntoProduction_IsNeverSaved()
    {
        await FluentActions.Awaiting(() => CopyHandler().HandleAsync(new SaveDatabaseCopyProfile(
                null, "Dev → Prod", _scenario.Development.Id, _scenario.Production.Id, null, DatabaseCopyOptions.Default with { RequireAnonymization = false }), Ct))
            .Should().ThrowAsync<DatabaseSecurityException>();

        Catalog.CopyProfiles.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ACopyProfileFromProduction_CannotDropAnonymization()
    {
        await FluentActions.Awaiting(() => CopyHandler().HandleAsync(new SaveDatabaseCopyProfile(
                null, "Prod → Dev cru", _scenario.Production.Id, _scenario.Development.Id, null, DatabaseCopyOptions.Default with { RequireAnonymization = false }), Ct))
            .Should().ThrowAsync<DomainException>().WithMessage("*exige anonimização*");
    }

    // --- Histórico e recuperação -------------------------------------------------

    [Fact]
    public async Task History_ListsTheAudits()
    {
        Catalog.Audit.Entries.Add(DatabaseOperationAudit.Blocked(DatabaseOperationType.Restore, null, _scenario.Production.Snapshot(),
            null, null, "PC", "u", "É produção.", DatabaseCopyScenario.Now));

        var rows = await new GetDatabaseOperationHistoryHandler(Catalog.Audit).HandleAsync(new GetDatabaseOperationHistory(), Ct);

        var row = rows.Should().ContainSingle().Subject;
        row.Status.Should().Be(DatabaseOperationStatus.Blocked);
        row.Destination.Should().Be("ECO Produção");
        row.Error.Should().Be("É produção.");
    }

    [Fact]
    public async Task Recovery_InterruptsWhatNobodyIsRunning_AndSweepsTheWorkspaces()
    {
        var orphan = DatabaseOperationAudit.Start(DatabaseOperationType.CopyAndAnonymize, null, null, null, null, "PC", "u", DatabaseCopyScenario.Now);
        var live = DatabaseOperationAudit.Start(DatabaseOperationType.CopyAndAnonymize, null, null, null, null, "PC", "u", DatabaseCopyScenario.Now);
        Catalog.Audit.Entries.AddRange([orphan, live]);
        using var running = _scenario.Gate.Enter(live.Id);
        _scenario.Workspaces.SweepResult = 1;

        var handler = new RecoverInterruptedDatabaseOperationsHandler(
            Catalog.Audit, _scenario.Workspaces, _scenario.Gate, Catalog, _scenario.Clock,
            NullLogger<RecoverInterruptedDatabaseOperationsHandler>.Instance);

        (await handler.HandleAsync(new RecoverInterruptedDatabaseOperations(), Ct)).Should().Be(1);

        orphan.Status.Should().Be(DatabaseOperationStatus.Interrupted);
        live.Status.Should().Be(DatabaseOperationStatus.Running);
        _scenario.Workspaces.Sweeps.Single().Should().Equal(live.Id);
        Catalog.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task Recovery_WithNothingToDo_SavesNothing()
    {
        var handler = new RecoverInterruptedDatabaseOperationsHandler(
            Catalog.Audit, _scenario.Workspaces, _scenario.Gate, Catalog, _scenario.Clock,
            NullLogger<RecoverInterruptedDatabaseOperationsHandler>.Instance);

        (await handler.HandleAsync(new RecoverInterruptedDatabaseOperations(), Ct)).Should().Be(0);
        Catalog.SaveCount.Should().Be(0);
    }
}

/// <summary>O [Validar] da tela Copiar Banco: tudo o que a cópia vai conferir, sem processo nenhum.</summary>
public class ValidateDatabaseCopyHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DatabaseCopyScenario _scenario = new();

    [Fact]
    public async Task AValidCopy_CanRun_AndAsksForTheProductionConfirmation()
    {
        var validation = await _scenario.ValidateHandler().HandleAsync(new ValidateDatabaseCopy(_scenario.Request()), Ct);

        validation.CanRun.Should().BeTrue(string.Join(" ", validation.Checks.Select(check => check.Detail)));
        validation.RequiresProductionConfirmation.Should().BeTrue();
        validation.RequiresTypedConfirmation.Should().BeFalse();
        validation.SourceName.Should().Be("ECO Produção");
        validation.DestinationName.Should().Be("ECO Desenvolvimento");
        validation.AnonymizationProfileName.Should().Be("ECO LGPD");
        validation.ConfirmationText.Should().Be("eco_core");
        validation.Checks.Should().Contain(check => check.Category == ValidateDatabaseCopyHandler.MaskingCategory && check.Outcome == CheckOutcome.Pass);
        _scenario.Tools.Calls.Should().Equal("catalog:ECO Produção:eco_core");
    }

    [Fact]
    public async Task AnInvalidCopy_ListsThePolicyViolations_WithoutTouchingTheServers()
    {
        var request = new DatabaseCopyRequest(_scenario.Development.Id, _scenario.Production.Id, DatabaseOperationType.Copy, null,
            DatabaseCopyOptions.Default with { RequireAnonymization = false });

        var validation = await _scenario.ValidateHandler().HandleAsync(new ValidateDatabaseCopy(request), Ct);

        validation.CanRun.Should().BeFalse();
        validation.Decision.Has(SecurityViolationCode.DestinationIsProduction).Should().BeTrue();
        _scenario.Inspector.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task MasksThatDoNotFitTheSource_AreJudgedToo()
    {
        _scenario.Profile.ReplaceRules(
            [new AnonymizationRuleSpec("public", "clientes", "id", MaskingMethod.FixedNumber, "0", ColumnSensitivity.Low)], DatabaseCopyScenario.Now);

        var validation = await _scenario.ValidateHandler().HandleAsync(new ValidateDatabaseCopy(_scenario.Request()), Ct);

        validation.Decision.Has(SecurityViolationCode.MaskingRulesInvalid).Should().BeTrue();
        validation.Checks.Should().Contain(check => check.Outcome == CheckOutcome.Fail && check.Detail!.Contains("é chave"));
        validation.CanRun.Should().BeFalse();
    }

    [Fact]
    public async Task UncoveredColumnsAndLargeObjects_AreWarnings()
    {
        var catalog = FakeMaskedCopier.DefaultCatalog();
        _scenario.Copier.Catalog = catalog with
        {
            AllColumns = [.. catalog.AllColumns, new ColumnInfo("public", "clientes", "telefone", "text", null)],
            LargeObjects = 2,
        };

        var validation = await _scenario.ValidateHandler().HandleAsync(new ValidateDatabaseCopy(_scenario.Request()), Ct);

        validation.CanRun.Should().BeTrue();
        validation.Checks.Should().Contain(check => check.Name == "Colunas sem regra" && check.Detail!.Contains("public.clientes.telefone"));
        validation.Checks.Should().Contain(check => check.Name == "Aviso" && check.Detail!.Contains("objeto(s) grande(s)"));
    }

    [Fact]
    public async Task UnreachableServersAndOldTools_AreFailures()
    {
        _scenario.Inspector.Diagnostics["ECO Desenvolvimento"] = ServerDiagnostics.Failed("recusado");
        _scenario.Locator.Tools = FakeToolLocator.WithVersion(15, 1);

        var validation = await _scenario.ValidateHandler().HandleAsync(new ValidateDatabaseCopy(_scenario.Request()), Ct);

        validation.CanRun.Should().BeFalse();
        validation.Checks.Should().Contain(check => check.Name == "Destino" && check.Outcome == CheckOutcome.Fail);
        validation.Checks.Should().Contain(check => check.Name == "pg_dump × origem" && check.Outcome == CheckOutcome.Fail);
    }


    [Fact]
    public async Task CriticalProduction_AsksForTheTypedName()
    {
        var critical = new DatabaseCopyScenario(DatabaseEnvironment.CriticalProduction);

        var validation = await critical.ValidateHandler().HandleAsync(new ValidateDatabaseCopy(critical.Request()), Ct);

        validation.RequiresTypedConfirmation.Should().BeTrue();
        validation.ConfirmationText.Should().Be("eco_core");
    }
}
