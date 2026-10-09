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
            Catalog.Connections, Catalog.AnonymizationProfiles, Catalog.CopyProfiles, _scenario.Credentials, Catalog,
            NullLogger<DeleteDatabaseConnectionHandler>.Instance);

        await FluentActions.Awaiting(() => handler.HandleAsync(new DeleteDatabaseConnection(_scenario.Masked.Id), Ct))
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
            Catalog.Connections, Catalog.AnonymizationProfiles, Catalog.CopyProfiles, _scenario.Credentials, Catalog,
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
            null, "Outro LGPD", null, _scenario.Masked.Id, null,
            [new AnonymizationRuleRow("public", "clientes", "email", MaskingKind.Function, "anon.partial_email(email)", ColumnSensitivity.High)]), Ct);

        var rows = await new GetAnonymizationProfilesHandler(Catalog.AnonymizationProfiles).HandleAsync(new GetAnonymizationProfiles(), Ct);
        rows.Single(row => row.Id == id).Rules.Should().ContainSingle().Which.ColumnKey.Should().Be("public.clientes.email");

        await handler.HandleAsync(new SaveAnonymizationProfile(id, "Outro LGPD", "d", _scenario.Masked.Id, "anon", []), Ct);
        Catalog.AnonymizationProfiles.Items.Single(profile => profile.Id == id).Rules.Should().BeEmpty();
    }

    [Fact]
    public async Task AProfileForAnUnknownConnection_IsRefused()
    {
        var handler = new SaveAnonymizationProfileHandler(
            Catalog.AnonymizationProfiles, Catalog.Connections, Catalog, _scenario.Clock, NullLogger<SaveAnonymizationProfileHandler>.Instance);

        await FluentActions.Awaiting(() => handler.HandleAsync(new SaveAnonymizationProfile(null, "x", null, Guid.CreateVersion7(), null, []), Ct))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task AnAnonymizationProfileInUse_CannotBeDeleted_ButAFreeOneCan()
    {
        var handler = new DeleteAnonymizationProfileHandler(Catalog.AnonymizationProfiles, Catalog.CopyProfiles, Catalog);
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

    [Fact]
    public async Task TheScript_UsesTheMaskedRoleAndDatabase()
    {
        var script = await new GenerateMaskingScriptHandler(Catalog.AnonymizationProfiles, Catalog.Connections, _scenario.Clock)
            .HandleAsync(new GenerateMaskingScript(_scenario.Profile.Id), Ct);

        script.Should().Contain("ON ROLE \"dump_anon\"").And.Contain("\"eco_core\"");
    }

    [Fact]
    public async Task ValidatingAProfile_ComparesItWithTheServer()
    {
        _scenario.Anonymizer.Status = FakeAnonymizerInspector.Healthy(
            new ServerMaskingRule("public", "clientes", "email", "MASKED WITH FUNCTION anon.fake_email()"),
            new ServerMaskingRule("public", "outra", "nome", "MASKED WITH VALUE NULL"));

        var validation = await new ValidateAnonymizationProfileHandler(Catalog.AnonymizationProfiles, Catalog.Connections, _scenario.Anonymization())
            .HandleAsync(new ValidateAnonymizationProfile(_scenario.Profile.Id), Ct);

        validation.IsValid.Should().BeFalse();
        validation.MissingOnServer.Should().Equal("public.clientes.cpf");
        validation.DifferentOnServer.Should().Equal("public.clientes.email");
        validation.ExtraOnServer.Should().Equal("public.outra.nome");
        validation.Problems().Should().HaveCount(2);
    }

    [Fact]
    public async Task LabelsDifferingOnlyInSpacingOrCase_StillMatch()
    {
        _scenario.Anonymizer.Status = FakeAnonymizerInspector.Healthy(
            new ServerMaskingRule("public", "clientes", "email", "masked with function   anon.partial_email(email)"),
            new ServerMaskingRule("public", "clientes", "cpf", "MASKED WITH FUNCTION anon.partial(cpf,0,$$*********$$,2)"));

        var validation = await _scenario.Anonymization().ValidateAsync(_scenario.Profile, _scenario.Masked.Snapshot(), Ct);

        validation.IsValid.Should().BeTrue();
        validation.MaskedColumns.Should().Be(2);
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
        _scenario.Tools.Calls.Should().BeEmpty();
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
    public async Task TheServerFacts_AreJudgedToo()
    {
        _scenario.Anonymizer.Status = FakeAnonymizerInspector.Healthy() with { CurrentRoleMasked = false };

        var validation = await _scenario.ValidateHandler().HandleAsync(new ValidateDatabaseCopy(_scenario.Request()), Ct);

        validation.Decision.Has(SecurityViolationCode.MaskedRoleNotVerified).Should().BeTrue();
        validation.CanRun.Should().BeFalse();
    }

    [Fact]
    public async Task UnreachableServersAndOldTools_AreFailures()
    {
        _scenario.Inspector.Diagnostics["ECO Desenvolvimento"] = ServerDiagnostics.Failed("recusado");
        _scenario.Locator.Tools = FakeToolLocator.WithVersion(15, 1);

        var validation = await _scenario.ValidateHandler().HandleAsync(new ValidateDatabaseCopy(_scenario.Request()), Ct);

        validation.CanRun.Should().BeFalse();
        validation.Checks.Should().Contain(check => check.Name == "Destino" && check.Outcome == CheckOutcome.Fail);
        validation.Checks.Should().Contain(check => check.Name == "Dump anônimo" && check.Outcome == CheckOutcome.Fail);
    }

    [Fact]
    public async Task UncoveredColumns_AreAWarning()
    {
        _scenario.Inspector.Columns = [.. _scenario.Inspector.Columns, new ColumnInfo("public", "clientes", "telefone", "text", null)];

        var validation = await _scenario.ValidateHandler().HandleAsync(new ValidateDatabaseCopy(_scenario.Request()), Ct);

        validation.Checks.Should().Contain(check => check.Name == "Colunas sem regra" && check.Outcome == CheckOutcome.Warning);
        validation.CanRun.Should().BeTrue();
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
